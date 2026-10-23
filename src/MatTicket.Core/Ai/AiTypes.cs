namespace MatTicket.Core.Ai;

/// <summary>One message of a chat with a model.</summary>
public sealed class AiMessage
{
    /// <summary>"system" | "user" | "assistant".</summary>
    public string Role { get; set; } = "user";

    public string Content { get; set; } = "";

    /// <summary>Files that ride along with a user message (pictures, PDFs); empty for nearly every message.</summary>
    public List<AiAttachment> Attachments { get; set; } = [];

    public static AiMessage System(string text) => new() { Role = "system", Content = text };

    public static AiMessage User(string text) => new() { Role = "user", Content = text };

    public static AiMessage Assistant(string text) => new() { Role = "assistant", Content = text };
}

/// <summary>One file attached to an <see cref="AiMessage"/>, inline as base64.</summary>
public sealed class AiAttachment
{
    /// <summary>"image" | "file".</summary>
    public string Kind { get; set; } = "image";

    public string MediaType { get; set; } = "";

    public string Name { get; set; } = "";

    public string Data { get; set; } = "";
}

/// <summary>What is asked of a model: the messages, a length cap and whether the answer must be one JSON object.</summary>
public sealed class AiRequest
{
    public List<AiMessage> Messages { get; set; } = [];

    public int? MaxTokens { get; set; }

    public bool Json { get; set; }
}

/// <summary>The answer. <c>Ok=false</c> carries a reason a person can read; an empty answer is never <c>Ok</c>.</summary>
public sealed class AiResponse
{
    public bool Ok { get; set; }

    public string? Text { get; set; }

    public string? Error { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public string? Model { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Starting points for the address of an AI profile. Mistral is the one inside the EU (Mistral AI, Paris): the choice where visitors write to
/// the AI (the assistant step of a public form), because a provider in the USA or China means a transfer to a third country.
/// </summary>
public static class AiPresets
{
    public sealed record Preset(string Id, string Label, string Url, string ModelHint, string Region);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", "EU"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "USA"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat", "China"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free", "USA (je nach Modell)"),
        new("custom", "Eigener Server", "", "", "je nach Server"),
    ];

    public static Preset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>The rough cost of a text when the provider gives no numbers: characters divided by four.</summary>
public static class TokenEstimate
{
    public static int Of(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static int Of(IEnumerable<AiMessage> messages) => messages.Sum(m => Of(m.Content));
}
