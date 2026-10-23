using System.Collections.Concurrent;
using System.Text.Json;
using MatTicket.Data;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>The sign-in with Microsoft Entra ID. The secret is protected with the key ring and never sent to a browser.</summary>
public sealed class EntraSettings
{
    public bool Enabled { get; set; }

    public string TenantId { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecretProtected { get; set; } = "";

    public string CallbackPath { get; set; } = "/signin-entra";

    /// <summary>Accounts of the tenant that are in no mapped group may still sign in as requesters (portal only). Off: they get no access.</summary>
    public bool UnmappedAsRequesters { get; set; }

    public bool HasSecret => ClientSecretProtected.Length > 0;

    /// <summary>Everything a sign-in needs is filled in (whether it is right, only Microsoft can say).</summary>
    public bool IsComplete => TenantIsValid && Guid.TryParse(ClientId, out _) && HasSecret && CallbackPath.StartsWith('/');

    /// <summary>A tenant is its id (a GUID) or its domain ("contoso.onmicrosoft.com").</summary>
    public bool TenantIsValid => Guid.TryParse(TenantId, out _) || (TenantId.Length > 3 && TenantId.Contains('.') && !TenantId.Contains('/'));

    public bool IsUsable => Enabled && IsComplete;
}

/// <summary>Mail to customers and agents over SMTP. Without a host nothing is sent (and nothing breaks).</summary>
public sealed class MailSettings
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>"starttls", "ssl" or "none".</summary>
    public string Security { get; set; } = "starttls";

    public string UserName { get; set; } = "";

    public string PasswordProtected { get; set; } = "";

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "MatTicket";

    /// <summary>The members of the department of a new ticket get a mail.</summary>
    public bool NotifyAgents { get; set; } = true;

    public bool IsUsable => Enabled && Host.Length > 0 && FromAddress.Contains('@');
}

/// <summary>The AI as the administrator set it up: on or off, the base instruction of the site, personal data, the limits.</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>The base instruction of the site; empty means the built-in text.</summary>
    public string BaseInstruction { get; set; } = "";

    /// <summary>Names, addresses and phone numbers of customers may go to the model. Off: they are replaced by placeholders.</summary>
    public bool SendPersonalData { get; set; }

    /// <summary>The assistant step of the public forms may call the AI.</summary>
    public bool PublicAssistant { get; set; } = true;

    public int UserDailyTokens { get; set; } = 200_000;

    public int MonthlyTokens { get; set; } = 5_000_000;

    /// <summary>How often one visitor (by the daily hash of the address) may ask the assistant per day.</summary>
    public int VisitorDailyRuns { get; set; } = 20;

    /// <summary>How many calls one address may start per minute (agents and visitors).</summary>
    public int PerMinutePerAddress { get; set; } = 6;

    public int MaxTokens { get; set; } = 16_000;

    public int TimeoutSeconds { get; set; } = 280;
}

/// <summary>Rules for tickets that are not about one form.</summary>
public sealed class TicketSettings
{
    /// <summary>Service staff create tickets (by hand) only in the departments of their teams; administrators everywhere. Moving stays allowed.</summary>
    public bool AgentsOnlyOwnDepartments { get; set; }
}

/// <summary>What the public pages say about who runs them.</summary>
public sealed class SiteSettings
{
    public string ServiceName { get; set; } = "Service-Portal";

    public string Company { get; set; } = "";

    public string PrivacyUrl { get; set; } = "";

    public string ImprintUrl { get; set; } = "";

    /// <summary>The start page of the portal for customers (/hilfe): a text above the published forms.</summary>
    public string PortalIntro { get; set; } = "";
}

/// <summary>The layout of the CRM pages of a kind of record: the columns of the list and the built-in fields that are hidden.</summary>
public sealed class CrmLayout
{
    public List<string> ListColumns { get; set; } = [];

    public List<string> Hidden { get; set; } = [];
}

/// <summary>
/// The settings of <see cref="Setting"/>, kept in memory (the sign-in handler and the mail sender read them outside of a request) and written
/// through. One instance for the application.
/// </summary>
public sealed class SettingStore(IServiceScopeFactory scopes)
{
    public const string Entra = "Entra";
    public const string Mail = "Mail";
    public const string Ai = "Ai";
    public const string Site = "Site";
    public const string Tickets = "Tickets";
    public const string VisitorSalt = "Ai.VisitorSalt";

    public static string Layout(CrmEntity entity) => "Crm.Layout." + entity;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    private volatile bool _loaded;

    /// <summary>Raised after a value changed (the sign-in reads its options again).</summary>
    public event Action<string>? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtDbContext>();
        var all = await db.Setting.AsNoTracking().ToListAsync(ct);
        _values.Clear();
        foreach (var s in all)
        {
            _values[s.Key] = s.Value;
        }

        _loaded = true;
    }

    public T Get<T>(string key) where T : new()
    {
        if (!_loaded)
        {
            LoadAsync().GetAwaiter().GetResult();
        }

        if (!_values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, Json) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public string? GetText(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public Task SetAsync<T>(MtDbContext db, string key, T value, CancellationToken ct = default)
        => SetTextAsync(db, key, JsonSerializer.Serialize(value, Json), ct);

    public async Task SetTextAsync(MtDbContext db, string key, string value, CancellationToken ct = default)
    {
        var row = await db.Setting.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.Setting.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(ct);
        _values[key] = value;
        Changed?.Invoke(key);
    }
}

/// <summary>Reading and changing the settings: reading is open to the code, changing only to administrators.</summary>
public static class SettingLogic
{
    public static T Get<T>(Biz biz, string key) where T : new() => biz.Service<SettingStore>().Get<T>(key);

    public static async Task SaveAsync<T>(Biz biz, string key, T value, string logText)
    {
        biz.RequireAdmin();
        await biz.Service<SettingStore>().SetAsync(biz.Db, key, value);
        await biz.Log.LogAsync(logText, "Setting", null, key);
    }

    /// <summary>A new secret replaces the old one; an empty input keeps it (the page never shows the old one).</summary>
    public static string KeepOrProtect(Biz biz, string? input, string current)
        => string.IsNullOrEmpty(input) ? current : biz.Secrets.Protect(input.Trim());
}
