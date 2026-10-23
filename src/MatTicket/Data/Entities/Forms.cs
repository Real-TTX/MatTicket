namespace MatTicket.Data.Entities;

public enum FormState
{
    Draft,
    Published,
    Archived,
}

/// <summary>
/// A form (an assistant) the administrator builds in the designer: steps with blocks, and a flow between them. The draft is edited; publishing
/// copies it into a <see cref="TicketFormVersion"/>, which is what the public address /{slug} shows.
/// </summary>
public class TicketForm : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    /// <summary>The address of the published form: /{Slug} (lower case letters, digits, dashes).</summary>
    public string Slug { get; set; } = "";

    public string? Description { get; set; }

    public FormState State { get; set; } = FormState.Draft;

    /// <summary>The definition being edited (JSON of <see cref="Core.Forms.FormDefinition"/>).</summary>
    public string DraftJson { get; set; } = "{}";

    /// <summary>The number of the published version, 0 when it was never published.</summary>
    public int PublishedVersion { get; set; }

    public DateTime? PublishedDate { get; set; }

    /// <summary>Where the tickets of this form go when its ticket step says nothing else.</summary>
    public long? DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? TeamId { get; set; }

    public Team? Team { get; set; }

    public long? CategoryId { get; set; }

    public Category? Category { get; set; }

    /// <summary>The published form may be listed in robots.txt, the sitemap and llms.txt.</summary>
    public bool Indexable { get; set; } = true;

    /// <summary>A new ticket of this form gets an automatic first answer of the AI (marked as such).</summary>
    public bool AiAutoReply { get; set; }

    /// <summary>A new ticket of this form is sorted by the AI (category, priority, department, team), undoable.</summary>
    public bool AiTriage { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<TicketFormVersion> Versions { get; set; } = [];
}

/// <summary>A published version of a form: never changed, so that the answers of a ticket can always be read against the form they were given in.</summary>
public class TicketFormVersion : AuditedEntity
{
    public long TicketFormId { get; set; }

    public TicketForm? TicketForm { get; set; }

    public int Version { get; set; }

    public string DefinitionJson { get; set; } = "{}";

    public DateTime PublishedDate { get; set; }

    public string? Note { get; set; }
}

/// <summary>A question of the catalogue: built once, inserted into many forms (the form keeps a copy and the id it came from).</summary>
public class Question : AuditedEntity, ISoftDeletable
{
    public string Label { get; set; } = "";

    /// <summary>The key the answer is stored under ("seriennummer").</summary>
    public string Key { get; set; } = "";

    /// <summary>The block type of the form (text, textarea, select, radio ...).</summary>
    public string Type { get; set; } = "text";

    /// <summary>The choices, one per line ("value|Label" or just "Label").</summary>
    public string? Options { get; set; }

    public string? Help { get; set; }

    public string? Placeholder { get; set; }

    public bool Required { get; set; }

    public long? CategoryId { get; set; }

    public Category? Category { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}
