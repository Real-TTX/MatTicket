namespace MatTicket.Data.Entities;

/// <summary>The two kinds of records of the small CRM. Custom fields, actions and the layout of the pages are defined per kind.</summary>
public enum CrmEntity
{
    Company,
    Contact,
}

/// <summary>A company (a customer). Records that came from a sync task carry its key (<see cref="ExternalId"/>) and the task.</summary>
public class Company : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? CustomerNumber { get; set; }

    public string? Street { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Website { get; set; }

    public string? Notes { get; set; }

    /// <summary>The values of the custom fields: key → text (jsonb).</summary>
    public string ExtraJson { get; set; } = "{}";

    /// <summary>The key of the record in the source of a sync task.</summary>
    public string? ExternalId { get; set; }

    public long? SyncTaskId { get; set; }

    public DateTime? SyncedDate { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<Contact> Contacts { get; set; } = [];
}

/// <summary>A person, usually of a company: who writes in, who is called back, where the technician goes.</summary>
public class Contact : AuditedEntity, ISoftDeletable
{
    public long? CompanyId { get; set; }

    public Company? Company { get; set; }

    public string? Salutation { get; set; }

    public string FirstName { get; set; } = "";

    public string LastName { get; set; } = "";

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Mobile { get; set; }

    public string? Position { get; set; }

    public string? CustomerNumber { get; set; }

    public string? Street { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    public string? Notes { get; set; }

    public string ExtraJson { get; set; } = "{}";

    public string? ExternalId { get; set; }

    public long? SyncTaskId { get; set; }

    public DateTime? SyncedDate { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public enum CustomFieldType
{
    Text,
    Textarea,
    Number,
    Date,
    Bool,
    Select,
    Url,
    Email,
    Phone,
}

/// <summary>A field the administrator added to companies or contacts. The value lives in the record's <c>ExtraJson</c> under <see cref="Key"/>.</summary>
public class CustomField : AuditedEntity, ISoftDeletable
{
    public CrmEntity Entity { get; set; }

    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public CustomFieldType Type { get; set; } = CustomFieldType.Text;

    /// <summary>The choices of a select, one per line.</summary>
    public string? Options { get; set; }

    public string? Hint { get; set; }

    /// <summary>The tab of the edit page the field is on ("Zusatzfelder" when empty).</summary>
    public string? Section { get; set; }

    public bool Required { get; set; }

    public int SortOrder { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public enum ViewActionKind
{
    /// <summary>A button that opens an address (another CRM, Navision, a portal); placeholders are filled from the record.</summary>
    Link,

    /// <summary>A map of the address (Google Maps), loaded only when it is opened.</summary>
    Map,

    /// <summary>A phone call (tel:) to the number in the template.</summary>
    Call,

    /// <summary>A mail (mailto:) to the address in the template.</summary>
    Mail,
}

/// <summary>
/// A button on the page of a company or a contact: "Open in Navision", "Open in the CRM", "Route" ... The template holds placeholders like
/// <c>{CustomerNumber}</c> or <c>{x.branche}</c> (a custom field); the values are URL-encoded when they are put in.
/// </summary>
public class ViewAction : AuditedEntity, ISoftDeletable
{
    public CrmEntity Entity { get; set; }

    public ViewActionKind Kind { get; set; } = ViewActionKind.Link;

    public string Label { get; set; } = "";

    public string Icon { get; set; } = "external";

    public string Template { get; set; } = "";

    public bool NewWindow { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}
