namespace MatTicket.Data.Entities;

public enum AiProvider
{
    /// <summary>Any server with the OpenAI-compatible /chat/completions (OpenAI, Mistral, DeepSeek, OpenRouter, an own server) and an API key.</summary>
    OpenAiCompatible,

    /// <summary>A ChatGPT account, signed in with a device code (like Codex); the tokens are renewed by the app.</summary>
    ChatGpt,
}

/// <summary>
/// A named connection to an AI ("DeepSeek Flash", "OpenRouter kostenlos", "ChatGPT"). Exactly one is active. The key or the sign-in is not in
/// this row: it lies encrypted in the data volume under <see cref="SecretToken"/>, outside the database, so it is never part of a backup or a
/// restore and never sent to a browser.
/// </summary>
public class AiProfile : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public AiProvider Provider { get; set; } = AiProvider.OpenAiCompatible;

    /// <summary>The template the address came from ("openai", "mistral" ...), for the page only.</summary>
    public string? Preset { get; set; }

    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    public bool IsActive { get; set; }

    /// <summary>The name of the secret file of this profile (the key, or the ChatGPT sign-in). A copy of a profile gets a new one.</summary>
    public Guid SecretToken { get; set; } = Guid.NewGuid();

    public string? Note { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public enum AiAction
{
    /// <summary>Suggests a reply to the customer for an agent (nothing is sent by the AI).</summary>
    Reply,

    /// <summary>Sorts a ticket: category, priority, department, team (undoable).</summary>
    Triage,

    /// <summary>Answers a visitor in the assistant step of a published form.</summary>
    Assist,

    /// <summary>Writes the first answer to a new ticket, marked as written by the AI (undoable).</summary>
    AutoReply,

    /// <summary>A test of the active profile from the settings.</summary>
    Test,
}

/// <summary>How a run ended. Running only while it runs.</summary>
public enum AiRunStatus
{
    Running,
    Done,
    Waiting,
    LimitReached,
    Stuck,
    Error,
    Cancelled,
}

/// <summary>One run of the AI: what was asked, by whom, through which profile, what it cost and how it ended. Every step is in <see cref="AiRunStep"/>.</summary>
public class AiRun : AuditedEntity
{
    public AiAction Action { get; set; }

    public AiRunStatus Status { get; set; } = AiRunStatus.Running;

    public long? TicketId { get; set; }

    public long? TicketFormId { get; set; }

    public long? UserId { get; set; }

    /// <summary>A visitor of a form: a hash of the address that changes every day, never the address.</summary>
    public string? VisitorHash { get; set; }

    public string ProfileName { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>"profile" (an own connection) or "relay".</summary>
    public string Transport { get; set; } = "profile";

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    /// <summary>The provider gave no numbers: the tokens are estimated (characters / 4).</summary>
    public bool TokensEstimated { get; set; }

    public int Nudges { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public string? Error { get; set; }

    /// <summary>The checked result (jsonb), what the page shows.</summary>
    public string ResultJson { get; set; } = "{}";

    public List<AiRunStep> Steps { get; set; } = [];

    public int TotalTokens => PromptTokens + CompletionTokens;
}

public enum AiStepKind
{
    Message,
    Action,
    Result,
    Warning,
}

public class AiRunStep : AuditedEntity
{
    public long AiRunId { get; set; }

    public AiRun? AiRun { get; set; }

    public int Number { get; set; }

    public AiStepKind Kind { get; set; }

    public string Text { get; set; } = "";

    public DateTime OccurredDate { get; set; }
}

/// <summary>A change a run made, with the value before: the journal that "Rückgängig" walks back, the newest run first.</summary>
public class AiJournalEntry : AuditedEntity
{
    public long AiRunId { get; set; }

    public AiRun? AiRun { get; set; }

    public string EntityType { get; set; } = "";

    public long EntityId { get; set; }

    public string Field { get; set; } = "";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTime? RevertedDate { get; set; }
}
