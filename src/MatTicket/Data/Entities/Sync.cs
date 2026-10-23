namespace MatTicket.Data.Entities;

public enum ConnectionKind
{
    SqlServer,
    PostgreSql,
    MySql,
}

/// <summary>A database the sync tasks read from. The connection string is protected with the key ring and never shown again.</summary>
public class DataConnection : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public ConnectionKind Kind { get; set; } = ConnectionKind.SqlServer;

    public string ConnectionStringProtected { get; set; } = "";

    public string? Note { get; set; }

    public DateTime? LastTestDate { get; set; }

    public bool? LastTestOk { get; set; }

    public string? LastTestMessage { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

/// <summary>
/// Imports companies or contacts: a SELECT on a connection, the column that is the key, and which column fills which field. Runs every
/// <see cref="IntervalMinutes"/> (0 = only by hand). Records are matched by key; a record that is no longer in the source is kept unless
/// <see cref="DeleteMissing"/> is on.
/// </summary>
public class SyncTask : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public long DataConnectionId { get; set; }

    public DataConnection? DataConnection { get; set; }

    public CrmEntity Target { get; set; } = CrmEntity.Company;

    public string Query { get; set; } = "";

    public string KeyColumn { get; set; } = "";

    /// <summary>For contacts: the column that holds the key of the company (as a sync task of companies stored it).</summary>
    public string? CompanyKeyColumn { get; set; }

    /// <summary>Column → field: a JSON list of { "column": "...", "field": "Name" | "x.key" }.</summary>
    public string MappingJson { get; set; } = "[]";

    public int IntervalMinutes { get; set; } = 60;

    public bool IsEnabled { get; set; } = true;

    public bool DeleteMissing { get; set; }

    public DateTime? LastRunDate { get; set; }

    public bool? LastRunOk { get; set; }

    public string? LastStatus { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public class SyncRun : AuditedEntity
{
    public long SyncTaskId { get; set; }

    public SyncTask? SyncTask { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public bool Ok { get; set; }

    public bool Manual { get; set; }

    public int Read { get; set; }

    public int Created { get; set; }

    public int Updated { get; set; }

    public int Unchanged { get; set; }

    public int Deleted { get; set; }

    public int Skipped { get; set; }

    public string? Message { get; set; }
}
