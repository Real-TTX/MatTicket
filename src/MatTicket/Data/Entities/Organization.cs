namespace MatTicket.Data.Entities;

/// <summary>A department (IT, Sales, Service): the first level a ticket belongs to. Agents see the tickets of the departments of their teams.</summary>
public class Department : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The address the department answers from and where its new tickets are announced (optional).</summary>
    public string? Email { get; set; }

    /// <summary>A colour the administrator chose, shown as a dot next to the name (#rrggbb).</summary>
    public string? Colour { get; set; }

    public int SortOrder { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<Team> Teams { get; set; } = [];
}

/// <summary>A team inside a department (1st level, field service, accounting). A ticket can be pushed to a team; its members see it.</summary>
public class Team : AuditedEntity, ISoftDeletable
{
    public long DepartmentId { get; set; }

    public Department? Department { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public List<TeamMember> Members { get; set; } = [];
}

public class TeamMember : AuditedEntity
{
    public long TeamId { get; set; }

    public Team? Team { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The lead of a team may assign its tickets to others (agents may always take a ticket themselves).</summary>
    public bool IsLead { get; set; }
}
