using System.Globalization;
using System.Text;
using MatTicket.Configuration;
using MatTicket.Core.Ai;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.Services.Ai;
using MatTicket.Services.Auth;
using MatTicket.Services.Mail;
using MatTicket.UiKit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// The image checks itself (HEALTHCHECK in the Dockerfile): it needs no curl.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var dataDir = Environment.GetEnvironmentVariable("MATTICKET_DATA") is { Length: > 0 } fromEnv
    ? fromEnv
    : Path.Combine(Directory.GetCurrentDirectory(), "data");
Directory.CreateDirectory(dataDir);
var config = new ConfigStore(dataDir);
Fmt.Configure(config.Current.Server.TimeZone);

var builder = WebApplication.CreateBuilder(args);

// Port 2740; ASPNETCORE_URLS still wins (tests, a reverse proxy on the same machine).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("MATTICKET_PORT") ?? "2740"}");
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Sessions live in the database; the keys that protect the cookie and the secrets live in the data volume, so a restart keeps everybody signed in.
builder.Services.AddDataProtection()
    .SetApplicationName("MatTicket")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddDbContextFactory<MtDbContext>(options => options
    .UseNpgsql(config.ConnectionString())
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MtDbContext>>().CreateDbContext());

builder.Services.AddSingleton<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IconSprite>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SessionCache>();
builder.Services.AddSingleton<TwoFactorTicket>();
builder.Services.AddSingleton<ActivityLogger>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<SettingStore>();
builder.Services.AddSingleton<MailQueue>();
builder.Services.AddHostedService<MailSender>();
builder.Services.AddSingleton<AiSecretStore>();
builder.Services.AddSingleton<AiRateLimiter>();
builder.Services.AddSingleton<ChatGptLoginStates>();
builder.Services.AddSingleton<AiQueue>();
builder.Services.AddHostedService<AiWorker>();
builder.Services.AddHostedService<SyncScheduler>();
builder.Services.AddScoped<SessionCookieEvents>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<PublicUrl>();
builder.Services.AddScoped<Biz>();
builder.Services.AddScoped<NavCounts>();
builder.Services.AddScoped<ShellFactory>();

// The AI: generous timeouts (a reasoning model thinks first), no cookies, the app names itself (OpenAI's edge refuses a library default).
builder.Services.AddHttpClient("ai", c =>
{
    c.Timeout = TimeSpan.FromSeconds(280);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(AiProfileLogic.UserAgent);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });

builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddAuthentication(SignInService.CookieScheme)
    .AddCookie(SignInService.CookieScheme, options =>
    {
        options.Cookie.Name = ".MatTicket";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = config.Current.Server.ForceSecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = false;
        options.EventsType = typeof(SessionCookieEvents);
    })
    // The answer of Microsoft lands here for a moment; the callback page turns it into a session of the app and removes it.
    .AddCookie(EntraLogic.ExternalScheme, options =>
    {
        options.Cookie.Name = ".MatTicket.Entra";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddOpenIdConnect(EntraLogic.Scheme, "Microsoft", _ => { });
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, EntraOptionsSetup>();

builder.Services.AddAuthorizationBuilder()
    // Everything needs a signed-in agent or administrator unless a page says otherwise: a new page is closed by default.
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SignInService.CookieScheme)
        .RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin), nameof(UserRole.Agent)).Build())
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireRole(nameof(UserRole.Admin)))
    .AddPolicy("SignedIn", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireAuthenticatedUser());

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AuthorizeFolder("/Account", "SignedIn");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/TwoFactor");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Account/Entra");
    options.Conventions.AllowAnonymousToPage("/Account/EntraCallback");
    options.Conventions.AllowAnonymousToPage("/Offline");
    options.Conventions.AllowAnonymousToFolder("/Portal");
})
.AddMvcOptions(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Numbers in forms and in the query string are always written the invariant way ("12.5"); the pages show them German with Fmt.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture);
    options.SupportedCultures = [CultureInfo.InvariantCulture];
    options.SupportedUICultures = [CultureInfo.InvariantCulture];
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

await DatabaseInitializer.InitializeAsync(app.Services, config, logger);
var settings = app.Services.GetRequiredService<SettingStore>();
await settings.LoadAsync();
settings.Changed += key =>
{
    if (key == SettingStore.Entra)
    {
        app.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>().TryRemove(EntraLogic.Scheme);
    }
};

app.UseForwardedHeaders();
app.UseRequestLocalization();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

// A rule of the business layer that says "not for you" ends the request with 403, whatever the page did or did not check.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ForbiddenException) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
});

app.Use(async (context, next) =>
{
    PublicUrl.Remember(context);
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // The map of an address (Google Maps) is the one frame from another site, and it is only loaded when somebody opens it.
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; " +
        "frame-src 'self' https://maps.google.com https://www.google.com; object-src 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com; frame-ancestors 'self'";
    await next();
});

// The web app manifest has its own type; the service worker must never be cached by the browser, or an update would wait for days.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name is "sw.js" or "manifest.webmanifest")
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.UseAuthentication();

// While there is no administrator the only page is the one that makes the first one (the public forms wait as well).
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/Account/Setup") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/css")
        && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/ui") && !path.StartsWithSegments("/img") && !path.StartsWithSegments("/Offline")
        && !path.Value!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !path.Value.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase)
        && path.Value is not ("/sw.js" or "/robots.txt" or "/llms.txt")
        && !await context.RequestServices.GetRequiredService<SetupState>().HasAdminAsync(context.RequestServices))
    {
        context.Response.Redirect("/Account/Setup");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapGet("/health", async (MtDbContext db) =>
{
    await db.Database.CanConnectAsync();
    return Results.Text("ok");
}).AllowAnonymous();

app.MapPost("/api/theme", async (HttpContext http, IAntiforgery antiforgery, Biz biz, SignInService signIn) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var theme = http.Request.Form["theme"].ToString();
    if (theme is not ("system" or "light" or "dark"))
    {
        return Results.BadRequest();
    }

    await UserLogic.SetThemeAsync(biz, theme);
    await signIn.RefreshAsync(http, await UserLogic.MeAsync(biz));
    return Results.NoContent();
}).RequireAuthorization("SignedIn");

// ------------------------------------------------------------------------------------------------ crawlers and language models

// Only the published forms that may be found are open to crawlers; everything else is the work of the team.
app.MapGet("/robots.txt", async (HttpContext http, MtDbContext db, ConfigStore cfg) =>
{
    var forms = await PortalLogic.PublishedAsync(db, onlyIndexable: true);
    var sb = new StringBuilder();
    sb.AppendLine("User-agent: *");
    sb.AppendLine("Allow: /hilfe");
    foreach (var f in forms)
    {
        sb.AppendLine($"Allow: /{f.Slug}$");
    }

    sb.AppendLine("Allow: /llms.txt");
    sb.AppendLine("Disallow: /");
    sb.AppendLine();
    sb.AppendLine($"Sitemap: {PublicUrl.Base(cfg, http)}/sitemap.xml");
    return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
}).AllowAnonymous();

app.MapGet("/sitemap.xml", async (HttpContext http, MtDbContext db, ConfigStore cfg) =>
{
    var forms = await PortalLogic.PublishedAsync(db, onlyIndexable: true);
    var root = PublicUrl.Base(cfg, http);
    var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    sb.Append($"  <url><loc>{System.Security.SecurityElement.Escape(root)}/hilfe</loc></url>\n");
    foreach (var f in forms)
    {
        sb.Append($"  <url><loc>{System.Security.SecurityElement.Escape($"{root}/{f.Slug}")}</loc><lastmod>{f.PublishedDate:yyyy-MM-dd}</lastmod></url>\n");
    }

    sb.Append("</urlset>\n");
    return Results.Text(sb.ToString(), "application/xml; charset=utf-8");
}).AllowAnonymous();

// llms.txt (https://llmstxt.org/): what this site offers, in Markdown, for language models.
app.MapGet("/llms.txt", async (HttpContext http, MtDbContext db, ConfigStore cfg, SettingStore store) =>
{
    var site = store.Get<SiteSettings>(SettingStore.Site);
    var forms = await PortalLogic.PublishedAsync(db, onlyIndexable: true);
    var root = PublicUrl.Base(cfg, http);
    var sb = new StringBuilder();
    sb.AppendLine($"# {site.ServiceName}{(string.IsNullOrWhiteSpace(site.Company) ? "" : " – " + site.Company)}");
    sb.AppendLine();
    sb.AppendLine("> Das Service-Portal: Hilfe-Assistenten und Formulare, über die Kunden Anfragen stellen. Jede Anfrage wird ein Ticket mit einer eigenen Statusseite.");
    sb.AppendLine();
    sb.AppendLine("Der Bereich der Mitarbeiter (Tickets, CRM, Einstellungen) ist nicht öffentlich. Personenbezogene Daten werden hier nicht veröffentlicht.");
    sb.AppendLine();
    sb.AppendLine("## Formulare");
    sb.AppendLine();
    sb.AppendLine($"- [Übersicht]({root}/hilfe): alle öffentlichen Formulare");
    foreach (var f in forms)
    {
        sb.AppendLine($"- [{f.Name}]({root}/{f.Slug}){(string.IsNullOrWhiteSpace(f.Description) ? "" : ": " + f.Description)}");
    }

    if (!string.IsNullOrWhiteSpace(site.PrivacyUrl) || !string.IsNullOrWhiteSpace(site.ImprintUrl))
    {
        sb.AppendLine();
        sb.AppendLine("## Optional");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(site.PrivacyUrl))
        {
            sb.AppendLine($"- [Datenschutz]({site.PrivacyUrl})");
        }

        if (!string.IsNullOrWhiteSpace(site.ImprintUrl))
        {
            sb.AppendLine($"- [Impressum]({site.ImprintUrl})");
        }
    }

    return Results.Text(sb.ToString(), "text/markdown; charset=utf-8");
}).AllowAnonymous();

// ------------------------------------------------------------------------------------------------ the AI of the agents (JSON for the pages)

app.MapPost("/api/tickets/{id:long}/ai/reply", async (long id, HttpContext http, IAntiforgery antiforgery, Biz biz) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var s = await AiLogic.SuggestReplyAsync(biz, id, http.Connection.RemoteIpAddress?.ToString());
    return Results.Ok(new { runId = s.RunId, status = s.Status.ToString(), html = s.Html, note = s.Note, error = s.Error });
});

app.MapPost("/api/tickets/{id:long}/ai/instruction", async (long id, string action, HttpContext http, IAntiforgery antiforgery, Biz biz) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var a = Enum.TryParse<AiAction>(action, out var parsed) ? parsed : AiAction.Reply;
    return Results.Ok(new { text = await AiLogic.FullInstructionAsync(biz, a, id) });
});

app.MapGet("/api/admin/ai/models", async (long? id, AiProvider provider, string? baseUrl, HttpContext http, Biz biz) =>
{
    // The key typed into the page comes as a header (never in the address of a request).
    var key = http.Request.Headers["X-Ai-Key"].ToString();
    var (models, error) = await AiProfileLogic.LoadModelsAsync(biz, id, provider, baseUrl, key);
    return Results.Ok(new { error, models = models?.Select(m => new { id = m.Id, name = m.Name, free = m.Free }) });
}).RequireAuthorization("Admin");

app.MapGet("/api/admin/ai/profiles/{id:long}/chatgpt", async (long id, Biz biz) =>
{
    var status = await AiProfileLogic.ChatGptStatusAsync(biz, id);
    return Results.Ok(ChatGptAccount.StatusJson(status));
}).RequireAuthorization("Admin");

app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>Whether the first administrator exists: the question is asked on every request until the answer is yes, then never again.</summary>
public sealed class SetupState
{
    private volatile bool _done;

    public async Task<bool> HasAdminAsync(IServiceProvider services)
    {
        if (_done)
        {
            return true;
        }

        _done = await UserLogic.HasAdminAsync(services.GetRequiredService<MtDbContext>());
        return _done;
    }

    public void MarkDone() => _done = true;
}

/// <summary>The command <c>dotnet MatTicket.dll --healthcheck</c>: asks the running site; exit code 0 means healthy.</summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("MATTICKET_PORT") ?? "2740";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}

/// <summary>Makes the entry point visible to the tests (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
