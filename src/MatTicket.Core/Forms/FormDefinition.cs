using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatTicket.Core.Forms;

/// <summary>
/// A form as the designer builds it: steps (pages, an AI answer, the ticket, an end) that hold blocks (texts and questions), and routes between
/// them. Stored as JSON (the draft of a form, and every published version). The flow starts at <see cref="Start"/>; each step goes on along
/// the first route whose condition holds.
/// </summary>
public sealed class FormDefinition
{
    public int Schema { get; set; } = 1;

    public string Title { get; set; } = "";

    /// <summary>A text above the first step (safe HTML).</summary>
    public string? Intro { get; set; }

    public string Start { get; set; } = "";

    public List<FormStep> Steps { get; set; } = [];

    public FormStep? Step(string? id) => Steps.FirstOrDefault(s => s.Id == id);

    public IEnumerable<FormBlock> Questions => Steps.SelectMany(s => s.Blocks).Where(b => BlockTypes.IsQuestion(b.Type));

    public FormBlock? Question(string key) => Questions.FirstOrDefault(b => b.Key == key);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static FormDefinition Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return new FormDefinition();
        }

        try
        {
            return JsonSerializer.Deserialize<FormDefinition>(json, Json) ?? new FormDefinition();
        }
        catch (JsonException)
        {
            return new FormDefinition();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>The kinds of steps.</summary>
public static class StepKinds
{
    /// <summary>A page of blocks; "Weiter" goes along the routes.</summary>
    public const string Page = "page";

    /// <summary>The AI answers from what was entered so far; the visitor says whether it helped (the answer <c>$ai</c> is "solved" or "open").</summary>
    public const string Ai = "ai";

    /// <summary>Asks for the last details (contact) and creates the ticket; the end of the flow.</summary>
    public const string Ticket = "ticket";

    /// <summary>A last page without a ticket ("Schön, dass es geklappt hat").</summary>
    public const string End = "end";

    public static readonly string[] All = [Page, Ai, Ticket, End];

    public static bool IsFinal(string kind) => kind is Ticket or End;
}

public sealed class FormStep
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = StepKinds.Page;

    public string Title { get; set; } = "";

    public List<FormBlock> Blocks { get; set; } = [];

    public List<FormRoute> Next { get; set; } = [];

    /// <summary>The position of the node in the flow editor.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>The text of the button that goes on ("Weiter", "Ticket senden").</summary>
    public string? ButtonText { get; set; }

    // ---- an AI step ----

    /// <summary>What the AI should do in this step, on top of the general instructions.</summary>
    public string? Instruction { get; set; }

    public string? HelpedText { get; set; }

    public string? NotHelpedText { get; set; }

    // ---- a ticket step ----

    /// <summary>The subject of the ticket with placeholders of the answers: "{bereich}: {kurzbeschreibung}".</summary>
    public string? SubjectTemplate { get; set; }

    public string? Priority { get; set; }

    public long? CategoryId { get; set; }

    public long? DepartmentId { get; set; }

    public long? TeamId { get; set; }

    /// <summary>The text after the ticket was created, or the text of an end step (safe HTML).</summary>
    public string? DoneHtml { get; set; }
}

/// <summary>A route to another step. Without <see cref="When"/> it always holds (the default, put it last).</summary>
public sealed class FormRoute
{
    public FormCondition? When { get; set; }

    public string To { get; set; } = "";
}

public sealed class FormCondition
{
    /// <summary>The key of a question, or <c>$ai</c> for the outcome of an AI step.</summary>
    public string Field { get; set; } = "";

    /// <summary>eq, neq, contains, in, empty, notempty, gt, lt.</summary>
    public string Op { get; set; } = "eq";

    public string? Value { get; set; }
}

/// <summary>A block of a step: a text, or a question with a key.</summary>
public sealed class FormBlock
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = BlockTypes.Text;

    public string? Key { get; set; }

    public string? Label { get; set; }

    public string? Help { get; set; }

    public string? Placeholder { get; set; }

    public bool Required { get; set; }

    /// <summary>The content of a richtext block (safe HTML).</summary>
    public string? Html { get; set; }

    public List<FormOption> Options { get; set; } = [];

    /// <summary>The question of the catalogue this block was copied from.</summary>
    public long? QuestionId { get; set; }

    /// <summary>Where the answer also goes: requester.name, requester.email, requester.phone, requester.company, ticket.subject, ticket.description, ticket.priority.</summary>
    public string? MapTo { get; set; }

    /// <summary>"full" or "half": the width on a wide screen.</summary>
    public string? Width { get; set; }

    /// <summary>Who sees the block: null (everybody), "guests" (not signed in), "signedin", "staff" (service staff and administrators).</summary>
    public string? Visibility { get; set; }
}

/// <summary>Who is looking at a form: what they may see decides which blocks are shown and read.</summary>
public enum FormViewer
{
    Guest,
    SignedIn,
    Staff,
}

/// <summary>The choices that come from the data of the app, already narrowed to what the viewer may choose.</summary>
public sealed record FormLists(IReadOnlyList<FormOption> Categories, IReadOnlyList<FormOption> Customers)
{
    public static readonly FormLists Empty = new([], []);
}

public static class Visibilities
{
    public const string Guests = "guests";
    public const string SignedIn = "signedin";
    public const string Staff = "staff";

    public static readonly IReadOnlyList<(string? Value, string Label)> All =
    [
        (null, "Alle"),
        (Guests, "Nur Gäste (nicht angemeldet)"),
        (SignedIn, "Nur Angemeldete"),
        (Staff, "Nur Service-Mitarbeiter"),
    ];

    public static bool IsKnown(string? value) => value is null or Guests or SignedIn or Staff;

    /// <summary>Whether <paramref name="viewer"/> sees a block of <paramref name="visibility"/>.</summary>
    public static bool Sees(string? visibility, FormViewer viewer) => visibility switch
    {
        Guests => viewer == FormViewer.Guest,
        SignedIn => viewer != FormViewer.Guest,
        Staff => viewer == FormViewer.Staff,
        _ => true,
    };
}

public sealed class FormOption
{
    public string Value { get; set; } = "";

    public string Label { get; set; } = "";
}

public static class BlockTypes
{
    public const string RichText = "richtext";
    public const string Divider = "divider";
    public const string Text = "text";
    public const string Textarea = "textarea";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Number = "number";
    public const string Date = "date";
    public const string Select = "select";
    public const string Radio = "radio";
    public const string Checkboxes = "checkboxes";
    public const string YesNo = "yesno";
    public const string Category = "category";
    public const string Consent = "consent";
    public const string Customer = "customer";

    /// <summary>Every type with its German name and its icon, in the order of the palette of the designer.</summary>
    public static readonly IReadOnlyList<(string Type, string Label, string Icon)> All =
    [
        (RichText, "Text", "pilcrow"),
        (Divider, "Trennlinie", "minus"),
        (Text, "Textfeld", "text-cursor-input"),
        (Textarea, "Mehrzeiliger Text", "file-text"),
        (Email, "E-Mail", "at-sign"),
        (Phone, "Telefon", "phone"),
        (Number, "Zahl", "hash"),
        (Date, "Datum", "calendar"),
        (Select, "Auswahlliste", "list"),
        (Radio, "Einfachauswahl", "circle-dot"),
        (Checkboxes, "Mehrfachauswahl", "check-square"),
        (YesNo, "Ja / Nein", "toggle-left"),
        (Category, "Kategorie", "tag"),
        (Consent, "Zustimmung", "shield"),
        (Customer, "Kunde (CRM)", "building"),
    ];

    public static bool IsQuestion(string type) => type is not (RichText or Divider);

    public static bool HasOptions(string type) => type is Select or Radio or Checkboxes;

    public static bool IsKnown(string type) => All.Any(t => t.Type == type);

    public static string Label(string type) => All.FirstOrDefault(t => t.Type == type).Label ?? type;
}

/// <summary>Where the answer of a question can also go on the ticket.</summary>
public static class MapTargets
{
    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("requester.name", "Name des Anfragenden"),
        ("requester.email", "E-Mail des Anfragenden"),
        ("requester.phone", "Telefon des Anfragenden"),
        ("requester.company", "Firma des Anfragenden"),
        ("ticket.subject", "Betreff des Tickets"),
        ("ticket.description", "Beschreibung des Tickets"),
        ("ticket.priority", "Priorität des Tickets"),
    ];

    public static bool IsKnown(string? value) => value is null || All.Any(t => t.Value == value);
}
