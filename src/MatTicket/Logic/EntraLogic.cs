using System.Security.Claims;
using MatTicket.Data.Entities;
using MatTicket.Services.Auth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatTicket.Logic;

/// <summary>
/// The sign-in with Microsoft Entra ID: the settings (in the database, effective without a restart), the groups that may sign in and the role
/// each gives, and turning a successful sign-in into an account of the app. Only members of a mapped group get in; everybody else is told why.
/// </summary>
public static class EntraLogic
{
    public const string Scheme = "Entra";
    public const string ExternalScheme = "EntraExternal";

    public static EntraSettings Settings(Biz biz)
    {
        biz.RequireAdmin();
        return SettingLogic.Get<EntraSettings>(biz, SettingStore.Entra);
    }

    /// <summary>What the sign-in page needs to know: is the button shown.</summary>
    public static bool IsUsable(SettingStore store) => store.Get<EntraSettings>(SettingStore.Entra).IsUsable;

    public static async Task SaveSettingsAsync(Biz biz, bool enabled, string tenantId, string clientId, string? clientSecret, bool removeSecret, string callbackPath, bool unmappedAsRequesters)
    {
        biz.RequireAdmin();
        var current = SettingLogic.Get<EntraSettings>(biz, SettingStore.Entra);
        var path = string.IsNullOrWhiteSpace(callbackPath) ? "/signin-entra" : "/" + callbackPath.Trim().TrimStart('/');
        if (path.Contains(' ') || path.Contains('?') || path.Length > 100)
        {
            throw new BizException("Der Rückruf-Pfad ist ein einfacher Pfad wie /signin-entra.", "Input.CallbackPath");
        }

        var settings = new EntraSettings
        {
            Enabled = enabled,
            TenantId = (tenantId ?? "").Trim(),
            ClientId = (clientId ?? "").Trim(),
            ClientSecretProtected = removeSecret ? "" : SettingLogic.KeepOrProtect(biz, clientSecret, current.ClientSecretProtected),
            CallbackPath = path,
            UnmappedAsRequesters = unmappedAsRequesters,
        };
        if (enabled && !settings.TenantIsValid)
        {
            throw new BizException("Die Tenant-ID ist eine GUID oder eine Domäne wie contoso.onmicrosoft.com.", "Input.TenantId");
        }

        if (enabled && !Guid.TryParse(settings.ClientId, out _))
        {
            throw new BizException("Die Client-ID (Anwendungs-ID) ist eine GUID.", "Input.ClientId");
        }

        if (enabled && !settings.HasSecret)
        {
            throw new BizException("Bitte gib den geheimen Clientschlüssel ein.", "Input.ClientSecret");
        }

        await SettingLogic.SaveAsync(biz, SettingStore.Entra, settings, "Entra-Anmeldung geändert");

        // The handler of the sign-in reads its options again at the next request.
        biz.Service<IOptionsMonitorCache<OpenIdConnectOptions>>().TryRemove(Scheme);
    }

    public static async Task<List<EntraGroupRole>> GroupsAsync(Biz biz)
    {
        biz.RequireAdmin();
        return await biz.Db.EntraGroupRole.AsNoTracking().OrderBy(g => g.Role).ThenBy(g => g.Note).ToListAsync();
    }

    /// <summary>The known groups as choices (for the access of forms and categories): the note is the name.</summary>
    public static async Task<List<Choice>> GroupChoicesAsync(Biz biz)
    {
        biz.RequireAdmin();
        var groups = await biz.Db.EntraGroupRole.AsNoTracking().OrderBy(g => g.Note).ToListAsync();
        return [.. groups.Select(g => new Choice(g.Id, string.IsNullOrWhiteSpace(g.Note) ? g.GroupObjectId : g.Note!, g.GroupObjectId))];
    }

    public static async Task AddGroupAsync(Biz biz, string groupObjectId, UserRole role, string? note)
    {
        biz.RequireAdmin();
        if (!Guid.TryParse(groupObjectId?.Trim(), out var id))
        {
            throw new BizException("Die Objekt-ID einer Gruppe ist eine GUID (Entra → Gruppen → Gruppe → Übersicht).", "Group.ObjectId");
        }

        var value = id.ToString();
        if (await biz.Db.EntraGroupRole.AnyAsync(g => g.GroupObjectId == value))
        {
            throw new BizException("Diese Gruppe ist schon eingetragen.", "Group.ObjectId");
        }

        biz.Db.EntraGroupRole.Add(new EntraGroupRole { GroupObjectId = value, Role = role, Note = OrganizationLogic.Clean(note) });
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Entra-Gruppe eingetragen", nameof(EntraGroupRole), null, $"{value} → {role}");
    }

    public static async Task RemoveGroupAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var group = await biz.Db.EntraGroupRole.FirstOrDefaultAsync(g => g.Id == id) ?? throw new BizException("Diese Zuordnung gibt es nicht mehr.");
        biz.Db.EntraGroupRole.Remove(group);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Entra-Gruppe entfernt", nameof(EntraGroupRole), id, group.GroupObjectId);
    }

    /// <summary>The outcome of a sign-in: the account, or the reason why there is none.</summary>
    public sealed record Outcome(User? User, string? Denied);

    /// <summary>
    /// Turns the identity Microsoft confirmed into an account: the groups decide whether and with which role; the account is found by its object
    /// id (or, the first time, by its address) and created when it is new. Its role follows the groups at every sign-in.
    /// </summary>
    public static async Task<Outcome> SignInAsync(Biz biz, ClaimsPrincipal principal)
    {
        var oid = principal.FindFirstValue("oid") ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        if (string.IsNullOrEmpty(oid))
        {
            return new Outcome(null, "Microsoft hat keine Objekt-ID des Kontos geliefert.");
        }

        if (principal.HasClaim(c => c.Type is "hasgroups" or "_claim_names"))
        {
            return new Outcome(null, "Das Konto ist in zu vielen Gruppen, als dass Microsoft sie mitschicken könnte. Stelle in der App-Registrierung unter „Tokenkonfiguration“ den Gruppenanspruch auf „Der Anwendung zugewiesene Gruppen“.");
        }

        var groups = principal.FindAll("groups").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapped = await biz.Db.EntraGroupRole.AsNoTracking().ToListAsync();
        var roles = mapped.Where(m => groups.Contains(m.GroupObjectId)).Select(m => m.Role).ToList();
        var settings = biz.Service<SettingStore>().Get<EntraSettings>(SettingStore.Entra);
        if (roles.Count == 0 && settings.UnmappedAsRequesters)
        {
            roles.Add(UserRole.Requester);
        }

        if (roles.Count == 0)
        {
            return new Outcome(null, groups.Count == 0
                ? "Microsoft hat keine Gruppen mitgeschickt. In der App-Registrierung fehlt der Gruppenanspruch (Tokenkonfiguration → Gruppenanspruch hinzufügen)."
                : "Du bist in keiner Gruppe, die für MatTicket freigegeben ist. Bitte wende dich an die Administration.");
        }

        // The strongest role of the groups: administrator, then service staff, then requester (portal only).
        var role = roles.Contains(UserRole.Admin) ? UserRole.Admin : roles.Contains(UserRole.Agent) ? UserRole.Agent : UserRole.Requester;
        var groupsJson = PortalLogic.GroupsToJson(groups);
        var email = SignInService.NormalizeEmail(principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("upn"));
        var name = principal.FindFirstValue("name") ?? email;

        var user = await biz.Db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.EntraObjectId == oid);
        if (user is null && email.Length > 0)
        {
            user = await biz.Db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Email == email && u.EntraObjectId == null);
        }

        if (user is null)
        {
            user = new User { Email = email.Length > 0 ? email : $"{oid}@entra", DisplayName = name, Role = role, Source = AuthSource.Entra, EntraObjectId = oid, EntraGroupsJson = groupsJson };
            biz.Db.User.Add(user);
            await biz.Db.SaveChangesAsync();
            await biz.Log.LogAsync("Konto über Entra angelegt", nameof(User), user.Id, $"{user.Email} ({role})");
            return new Outcome(user, null);
        }

        if (!user.IsActive)
        {
            return new Outcome(null, "Dein Konto ist in MatTicket gesperrt. Bitte wende dich an die Administration.");
        }

        user.EntraObjectId = oid;
        user.Source = AuthSource.Entra;
        user.EntraGroupsJson = groupsJson;
        if (user.Role != role)
        {
            await biz.Log.LogAsync("Rolle aus Entra übernommen", nameof(User), user.Id, $"{user.Role} → {role}");
            user.Role = role;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            user.DisplayName = name;
        }

        await biz.Db.SaveChangesAsync();
        biz.Service<SessionCache>().InvalidateUser(user.Id);
        return new Outcome(user, null);
    }
}

/// <summary>
/// The options of the Entra handler, read from the settings whenever they are needed (and again after a change). An incomplete configuration
/// never breaks the app: the handler then gets harmless values and the sign-in page simply does not offer the button.
/// </summary>
public sealed class EntraOptionsSetup(SettingStore store, Configuration.SecretProtector secrets) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != EntraLogic.Scheme)
        {
            return;
        }

        var s = store.Get<EntraSettings>(SettingStore.Entra);
        var usable = s.IsUsable;
        options.SignInScheme = EntraLogic.ExternalScheme;
        options.Authority = $"https://login.microsoftonline.com/{(usable ? s.TenantId : "common")}/v2.0";
        options.ClientId = usable ? s.ClientId : "00000000-0000-0000-0000-000000000000";
        options.ClientSecret = usable ? secrets.Unprotect(s.ClientSecretProtected) : "not-configured";
        options.CallbackPath = usable ? s.CallbackPath : "/signin-entra";
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                if (!store.Get<EntraSettings>(SettingStore.Entra).IsUsable)
                {
                    context.Response.Redirect("/Account/Login?entra=" + Uri.EscapeDataString("Die Anmeldung mit Microsoft ist nicht vollständig eingerichtet."));
                    context.HandleResponse();
                }

                return Task.CompletedTask;
            },
            OnRemoteFailure = context =>
            {
                var message = context.Failure?.Message ?? "Unbekannter Fehler";
                var reason = message.Contains("AADSTS50011", StringComparison.Ordinal) ? "Die Rückruf-Adresse ist in der App-Registrierung nicht eingetragen (AADSTS50011)."
                    : message.Contains("AADSTS7000215", StringComparison.Ordinal) ? "Der geheime Clientschlüssel stimmt nicht oder ist abgelaufen (AADSTS7000215)."
                    : message.Contains("AADSTS700016", StringComparison.Ordinal) ? "Die Client-ID ist in diesem Tenant nicht bekannt (AADSTS700016)."
                    : message.Contains("AADSTS90002", StringComparison.Ordinal) ? "Den Tenant gibt es nicht (AADSTS90002)."
                    : message.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ? "Die Anmeldung wurde abgebrochen oder abgelehnt."
                    : message.Contains("IDX20803", StringComparison.Ordinal) || message.Contains("IDX20804", StringComparison.Ordinal) ? "Microsoft ist gerade nicht erreichbar (Metadaten konnten nicht geladen werden)."
                    : "Die Anmeldung mit Microsoft ist fehlgeschlagen: " + (message.Length > 300 ? message[..300] : message);
                context.Response.Redirect("/Account/Login?entra=" + Uri.EscapeDataString(reason));
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);
}
