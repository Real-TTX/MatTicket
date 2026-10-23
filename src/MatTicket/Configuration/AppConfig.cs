using System.Text.Json.Serialization;

namespace MatTicket.Configuration;

/// <summary>
/// The configuration of the installation (JSON, <c>{data}/config/app.json</c>): how it is deployed and what the operator presets. What the
/// administrator changes in the app (sign-in, mail, AI, CRM layout) lives in the database (<see cref="Data.Entities.Setting"/>).
/// The sections <see cref="Database"/> and <see cref="Server"/> can be overridden by environment variables (<c>MATTICKET__Database__Host</c>);
/// the compose files need none of them, the defaults match the stack.
/// </summary>
public sealed class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();

    public ServerConfig Server { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();

    public OperatorAiConfig Ai { get; set; } = new();
}

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "db";

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "matticket";

    public string Username { get; set; } = "matticket";

    public string Password { get; set; } = "matticket";
}

public sealed class ServerConfig
{
    /// <summary>The address people use, for the links in mails ("https://service.example.de"). Empty: taken from the request.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>"Europe/Berlin": the time zone of the installation, for dates and schedules.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The cookie is marked secure when the site is reached over HTTPS (also behind a reverse proxy that says so).</summary>
    public bool ForceSecureCookie { get; set; }
}

public sealed class SecurityConfig
{
    public int SessionDays { get; set; } = 14;

    [JsonIgnore]
    public int MaxFailedLogins => 5;
}

/// <summary>
/// What the operator of the installation presets for the AI (the outermost layer of the instructions), and an optional relay that is used
/// when the administrator has set up no connection of their own. The app works fully without a relay.
/// </summary>
public sealed class OperatorAiConfig
{
    /// <summary>The operator's instruction, put before everything else.</summary>
    public string OperatorInstruction { get; set; } = "";

    /// <summary>An addition of the operator per action ("Reply", "Triage", "Assist", "AutoReply").</summary>
    public Dictionary<string, string> ActionInstructions { get; set; } = [];

    /// <summary>An OpenAI-compatible endpoint of the operator (e.g. a cloud); empty: no relay.</summary>
    public string RelayUrl { get; set; } = "";

    public string RelayKey { get; set; } = "";

    public string RelayModel { get; set; } = "";

    /// <summary>An API key of an older version (a single key, no profiles); it becomes the first profile when the profiles are read the first time.</summary>
    public string LegacyApiKey { get; set; } = "";

    public string LegacyBaseUrl { get; set; } = "";

    public string LegacyModel { get; set; } = "";
}
