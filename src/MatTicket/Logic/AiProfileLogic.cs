using MatTicket.Core.Ai;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>
/// The AI profiles: named connections, exactly one active. A profile is provider, address, model and key (or a ChatGPT sign-in); the key
/// lies in <see cref="AiSecretStore"/>, never in the database and never in a page.
/// </summary>
public static class AiProfileLogic
{
    public const string UserAgent = "MatTicket/0.1";

    public static async Task<List<AiProfile>> ListAsync(Biz biz)
    {
        biz.RequireAdmin();
        await EnsureLegacyAsync(biz.Db, biz.Service<AiSecretStore>(), biz.Config);
        return await biz.Db.AiProfile.AsNoTracking().OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToListAsync();
    }

    public static async Task<AiProfile?> GetAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.AiProfile.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public static bool HasKey(Biz biz, AiProfile profile)
        => biz.Service<AiSecretStore>().Has(profile.SecretToken, profile.Provider == AiProvider.ChatGpt ? AiSecretStore.ChatGpt : AiSecretStore.ApiKey);

    /// <summary>
    /// An API key of an older installation (one key, no profiles) becomes the first profile the first time the profiles are read; it is moved
    /// out of the configuration, so it exists once.
    /// </summary>
    internal static async Task EnsureLegacyAsync(MtDbContext db, AiSecretStore secrets, Configuration.ConfigStore config)
    {
        var legacy = config.Current.Ai;
        if (string.IsNullOrWhiteSpace(legacy.LegacyApiKey) || await db.AiProfile.AnyAsync())
        {
            return;
        }

        var profile = new AiProfile
        {
            Name = "Standard",
            Provider = AiProvider.OpenAiCompatible,
            BaseUrl = string.IsNullOrWhiteSpace(legacy.LegacyBaseUrl) ? ChatCompletionsClient.DefaultBaseUrl : legacy.LegacyBaseUrl.Trim(),
            Model = string.IsNullOrWhiteSpace(legacy.LegacyModel) ? "gpt-4o-mini" : legacy.LegacyModel.Trim(),
            IsActive = true,
            Note = "Aus dem früheren einzelnen Schlüssel übernommen.",
        };
        db.AiProfile.Add(profile);
        await db.SaveChangesAsync();
        await secrets.SaveAsync(profile.SecretToken, AiSecretStore.ApiKey, legacy.LegacyApiKey.Trim());
        config.Update(c =>
        {
            c.Ai.LegacyApiKey = "";
            c.Ai.LegacyBaseUrl = "";
            c.Ai.LegacyModel = "";
        });
    }

    public sealed record ProfileInput(string Name, AiProvider Provider, string? Preset, string BaseUrl, string Model, string? ApiKey, bool RemoveKey, string? Note);

    public static async Task<AiProfile> SaveAsync(Biz biz, long? id, ProfileInput input)
    {
        biz.RequireAdmin();
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new BizException("Bitte gib dem Profil einen Namen.", "Input.Name");
        }

        if (await biz.Db.AiProfile.AnyAsync(p => p.Name == name && p.Id != id))
        {
            throw new BizException("Ein Profil mit diesem Namen gibt es schon.", "Input.Name");
        }

        var baseUrl = (input.BaseUrl ?? "").Trim().TrimEnd('/');
        if (input.Provider == AiProvider.OpenAiCompatible)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            {
                throw new BizException("Bitte gib die Adresse der Schnittstelle ein (https://…/v1).", "Input.BaseUrl");
            }
        }

        if (string.IsNullOrWhiteSpace(input.Model))
        {
            throw new BizException("Bitte wähle ein Modell.", "Input.Model");
        }

        var secrets = biz.Service<AiSecretStore>();
        var profile = id is { } existing ? await biz.Db.AiProfile.FirstOrDefaultAsync(p => p.Id == existing) ?? throw new BizException("Das Profil gibt es nicht mehr.") : new AiProfile();
        if (id is not null && profile.Provider != input.Provider)
        {
            // A profile that changes its kind loses what belonged to the old kind.
            secrets.DeleteAll(profile.SecretToken);
        }

        profile.Name = name;
        profile.Provider = input.Provider;
        profile.Preset = input.Preset;
        profile.BaseUrl = input.Provider == AiProvider.ChatGpt ? ChatGptEndpoints.Default.BackendBase : baseUrl;
        profile.Model = input.Model.Trim();
        profile.Note = OrganizationLogic.Clean(input.Note);
        if (id is null)
        {
            profile.IsActive = !await biz.Db.AiProfile.AnyAsync();
            biz.Db.AiProfile.Add(profile);
        }

        await biz.Db.SaveChangesAsync();
        if (input.Provider == AiProvider.OpenAiCompatible)
        {
            if (input.RemoveKey)
            {
                await secrets.SaveAsync(profile.SecretToken, AiSecretStore.ApiKey, null);
            }
            else if (!string.IsNullOrWhiteSpace(input.ApiKey))
            {
                await secrets.SaveAsync(profile.SecretToken, AiSecretStore.ApiKey, input.ApiKey.Trim());
            }
        }

        await biz.Log.LogAsync(id is null ? "KI-Profil angelegt" : "KI-Profil geändert", nameof(AiProfile), profile.Id, name);
        return profile;
    }

    /// <summary>Makes a profile the active one; it applies at once for the whole app.</summary>
    public static async Task ActivateAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var all = await biz.Db.AiProfile.ToListAsync();
        if (all.All(p => p.Id != id))
        {
            throw new BizException("Das Profil gibt es nicht mehr.");
        }

        foreach (var p in all)
        {
            p.IsActive = p.Id == id;
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("KI-Profil aktiviert", nameof(AiProfile), id, all.First(p => p.Id == id).Name);
    }

    /// <summary>A copy of a profile, without its key or sign-in (a sign-in is never copied).</summary>
    public static async Task<AiProfile> DuplicateAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var source = await biz.Db.AiProfile.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id) ?? throw new BizException("Das Profil gibt es nicht mehr.");
        var name = source.Name + " (Kopie)";
        for (var i = 2; await biz.Db.AiProfile.AnyAsync(p => p.Name == name); i++)
        {
            name = $"{source.Name} (Kopie {i})";
        }

        var copy = new AiProfile { Name = name, Provider = source.Provider, Preset = source.Preset, BaseUrl = source.BaseUrl, Model = source.Model, Note = source.Note };
        biz.Db.AiProfile.Add(copy);
        await biz.Db.SaveChangesAsync();
        return copy;
    }

    public static async Task DeleteAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var profile = await biz.Db.AiProfile.FirstOrDefaultAsync(p => p.Id == id) ?? throw new BizException("Das Profil gibt es nicht mehr.");
        biz.Db.AiProfile.Remove(profile);
        await biz.Db.SaveChangesAsync();
        biz.Service<AiSecretStore>().DeleteAll(profile.SecretToken);
        await biz.Log.LogAsync("KI-Profil gelöscht", nameof(AiProfile), id, profile.Name);
    }

    /// <summary>
    /// The models of a provider (<c>GET /models</c>). An address and a key typed into the page but not saved yet may be used; without a typed
    /// key the saved one of the profile is taken. "Free" only when the provider says so.
    /// </summary>
    public static async Task<(List<ChatCompletionsClient.ModelInfo>? Models, string? Error)> LoadModelsAsync(Biz biz, long? id, AiProvider provider, string? baseUrl, string? typedKey)
    {
        biz.RequireAdmin();
        var profile = id is { } pid ? await biz.Db.AiProfile.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid) : null;
        if (provider == AiProvider.ChatGpt)
        {
            if (profile is null)
            {
                return (null, "Bitte speichere das Profil und melde dich bei ChatGPT an.");
            }

            var account = Account(biz.Services, profile);
            var models = await account.ListModelsAsync();
            return models.Count == 0
                ? (null, "ChatGPT hat keine Modelle genannt. Bist du angemeldet?")
                : ([.. models.Select(m => new ChatCompletionsClient.ModelInfo(m.Slug, m.Name, false))], null);
        }

        var key = !string.IsNullOrWhiteSpace(typedKey) ? typedKey.Trim()
            : profile is null ? "" : await biz.Service<AiSecretStore>().LoadAsync(profile.SecretToken, AiSecretStore.ApiKey) ?? "";
        var client = biz.Service<IHttpClientFactory>().CreateClient("ai");
        return await ChatCompletionsClient.ListModelsAsync(client, baseUrl ?? profile?.BaseUrl ?? "", key);
    }

    // ------------------------------------------------------------------------------------------------ ChatGPT sign-in of a profile

    public static ChatGptAccount Account(IServiceProvider services, AiProfile profile)
    {
        var secrets = services.GetRequiredService<AiSecretStore>();
        var states = services.GetRequiredService<ChatGptLoginStates>();
        var http = services.GetRequiredService<IHttpClientFactory>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ChatGpt");
        return new ChatGptAccount(secrets.ChatGptStore(profile.SecretToken), () => http.CreateClient("ai"), ChatGptEndpoints.Default, states.For(profile.SecretToken.ToString("N")),
            UserAgent, "matticket", "Einstellungen → KI → Profile", m => logger.LogInformation("{Message}", m));
    }

    private static async Task<AiProfile> ChatGptProfileAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var profile = await biz.Db.AiProfile.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id) ?? throw new BizException("Das Profil gibt es nicht mehr.");
        return profile.Provider == AiProvider.ChatGpt ? profile : throw new BizException("Dieses Profil ist kein ChatGPT-Profil.");
    }

    public static async Task<(ChatGptAccount.PendingLogin? Pending, string? Error)> StartChatGptLoginAsync(Biz biz, long id)
    {
        var profile = await ChatGptProfileAsync(biz, id);
        var result = await Account(biz.Services, profile).StartLoginAsync();
        await biz.Log.LogAsync("ChatGPT-Anmeldung gestartet", nameof(AiProfile), id, profile.Name);
        return result;
    }

    public static async Task<ChatGptAccount.Status> ChatGptStatusAsync(Biz biz, long id)
    {
        var profile = await ChatGptProfileAsync(biz, id);
        return await Account(biz.Services, profile).GetStatusAsync(poll: true);
    }

    public static async Task ChatGptLogoutAsync(Biz biz, long id)
    {
        var profile = await ChatGptProfileAsync(biz, id);
        await Account(biz.Services, profile).LogoutAsync();
        await biz.Log.LogAsync("ChatGPT abgemeldet", nameof(AiProfile), id, profile.Name);
    }

    /// <summary>Moves the ChatGPT sign-in of one profile to another (the source is signed out by it): sign-ins are moved, never copied.</summary>
    public static async Task MoveChatGptLoginAsync(Biz biz, long fromId, long toId)
    {
        var from = await ChatGptProfileAsync(biz, fromId);
        var to = await ChatGptProfileAsync(biz, toId);
        biz.Service<AiSecretStore>().Move(from.SecretToken, to.SecretToken, AiSecretStore.ChatGpt);
        await biz.Log.LogAsync("ChatGPT-Anmeldung verschoben", nameof(AiProfile), toId, $"{from.Name} → {to.Name}");
    }
}
