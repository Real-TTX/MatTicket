namespace MatTicket.Data.Entities;

/// <summary>A category of the tickets; with <see cref="ParentId"/> it is a subcategory (one level deep). It says where a new ticket goes by default.</summary>
public class Category : AuditedEntity, ISoftDeletable
{
    public long? ParentId { get; set; }

    public Category? Parent { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>An icon name of the sprite, shown in the form and in lists.</summary>
    public string? Icon { get; set; }

    public long? DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? TeamId { get; set; }

    public Team? Team { get; set; }

    public TicketPriority DefaultPriority { get; set; } = TicketPriority.Normal;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Entra groups (object ids, jsonb) whose members may choose this category in a form; empty: everybody. Service staff always may.</summary>
    public string AccessGroupsJson { get; set; } = "[]";

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<Category> Children { get; set; } = [];
}

public enum TicketStatus
{
    New,
    Open,
    Waiting,
    Resolved,
    Closed,
}

public enum TicketPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum TicketSource
{
    Form,
    Agent,
    Portal,
}

/// <summary>A ticket. The customer reaches it by <see cref="Token"/> (the link of its status page); agents by number.</summary>
public class Ticket : AuditedEntity, ISoftDeletable
{
    /// <summary>The number people quote ("T26-00042"); set right after the first save from the id.</summary>
    public string Number { get; set; } = "";

    /// <summary>The secret of the status page of the customer (/t/{token}).</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    public string Subject { get; set; } = "";

    /// <summary>The description as it was sent (safe HTML).</summary>
    public string Description { get; set; } = "";

    public TicketStatus Status { get; set; } = TicketStatus.New;

    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    public TicketSource Source { get; set; } = TicketSource.Form;

    public long? CategoryId { get; set; }

    public Category? Category { get; set; }

    public long? DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? TeamId { get; set; }

    public Team? Team { get; set; }

    public long? AssigneeUserId { get; set; }

    public User? Assignee { get; set; }

    public long? ContactId { get; set; }

    public Contact? Contact { get; set; }

    public long? CompanyId { get; set; }

    public Company? Company { get; set; }

    public long? TicketFormId { get; set; }

    public TicketForm? TicketForm { get; set; }

    /// <summary>The published version of the form the answers belong to.</summary>
    public int? FormVersion { get; set; }

    /// <summary>The answers of the form: a list of label, key and value, kept as they were shown (jsonb).</summary>
    public string AnswersJson { get; set; } = "[]";

    /// <summary>The account that sent the ticket, when it was signed in: name and address then come from the account, not from the form.</summary>
    public long? RequesterUserId { get; set; }

    public User? RequesterUser { get; set; }

    public string RequesterName { get; set; } = "";

    public string RequesterEmail { get; set; } = "";

    public string? RequesterPhone { get; set; }

    public string? RequesterCompany { get; set; }

    public DateTime? DueDate { get; set; }

    public DateTime? FirstResponseDate { get; set; }

    public DateTime? ResolvedDate { get; set; }

    public DateTime LastActivityDate { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<TicketMessage> Messages { get; set; } = [];
}

/// <summary>What a message of a ticket is. Customer, Reply and Ai are what the customer sees; Note and System only the agents.</summary>
public enum MessageKind
{
    Customer,
    Reply,
    Note,
    System,
    Ai,
}

public class TicketMessage : AuditedEntity, ISoftDeletable
{
    public long TicketId { get; set; }

    public Ticket? Ticket { get; set; }

    public MessageKind Kind { get; set; }

    /// <summary>Safe HTML (sanitized when it is written).</summary>
    public string Body { get; set; } = "";

    public long? AuthorUserId { get; set; }

    public string AuthorName { get; set; } = "";

    /// <summary>The AI run that wrote or suggested it, when there was one.</summary>
    public long? AiRunId { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public bool IsPublic => Kind is MessageKind.Customer or MessageKind.Reply or MessageKind.Ai;
}
