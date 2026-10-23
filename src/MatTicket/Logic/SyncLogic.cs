using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Services;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>One line of the mapping of a sync task: a column of the query fills a field of the record.</summary>
public sealed record SyncMapping(string Column, string Field);

/// <summary>The first rows of a query, for the preview on the page of a sync task.</summary>
public sealed record SyncPreview(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? Error);

/// <summary>
/// Imports companies and contacts from another database (Navision on SQL Server, an ERP on PostgreSQL or MySQL): a connection, a SELECT, the key
/// column and which column fills which field. Only reading queries are allowed, and they run in a read-only transaction where the database
/// has one; still, give the connection a user that can only read.
/// </summary>
public static partial class SyncLogic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const int MaxRows = 100_000;

    // ================================================================================================ connections

    public static IQueryable<DataConnection> Connections(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.DataConnection.AsNoTracking();
    }

    public static async Task<List<Choice>> ConnectionChoicesAsync(Biz biz)
    {
        biz.RequireAdmin();
        return [.. (await biz.Db.DataConnection.AsNoTracking().OrderBy(c => c.Name).ToListAsync()).Select(c => new Choice(c.Id, c.Name, UiKit.Labels.Of(c.Kind)))];
    }

    public static async Task<DataConnection?> ConnectionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.DataConnection.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    public static async Task<DataConnection> SaveConnectionAsync(Biz biz, long? id, string name, ConnectionKind kind, string? connectionString, string? note)
    {
        biz.RequireAdmin();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BizException("Bitte gib einen Namen ein.", "Input.Name");
        }

        if (id is null && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new BizException("Bitte gib die Verbindungszeichenfolge ein.", "Input.ConnectionString");
        }

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                using var test = Create(kind, connectionString.Trim());
            }
            catch (ArgumentException ex)
            {
                throw new BizException("Die Verbindungszeichenfolge ist nicht lesbar: " + ex.Message, "Input.ConnectionString");
            }
        }

        var connection = id is { } existing ? await biz.Db.DataConnection.FirstOrDefaultAsync(c => c.Id == existing) ?? throw new BizException("Die Verbindung gibt es nicht mehr.") : new DataConnection();
        connection.Name = name.Trim();
        connection.Kind = kind;
        connection.Note = OrganizationLogic.Clean(note);
        connection.ConnectionStringProtected = SettingLogic.KeepOrProtect(biz, connectionString, connection.ConnectionStringProtected);
        if (id is null)
        {
            biz.Db.DataConnection.Add(connection);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Datenverbindung angelegt" : "Datenverbindung geändert", nameof(DataConnection), connection.Id, connection.Name);
        return connection;
    }

    public static async Task DeleteConnectionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        if (await biz.Db.SyncTask.AnyAsync(t => t.DataConnectionId == id))
        {
            throw new BizException("Die Verbindung wird noch von Sync-Aufgaben benutzt.");
        }

        var connection = await biz.Db.DataConnection.FirstOrDefaultAsync(c => c.Id == id) ?? throw new BizException("Die Verbindung gibt es nicht mehr.");
        biz.Db.DataConnection.Remove(connection);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Datenverbindung gelöscht", nameof(DataConnection), id, connection.Name);
    }

    /// <summary>Opens the connection once and says how it went (stored on the connection).</summary>
    public static async Task<string> TestConnectionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var connection = await biz.Db.DataConnection.FirstOrDefaultAsync(c => c.Id == id) ?? throw new BizException("Die Verbindung gibt es nicht mehr.");
        string message;
        bool ok;
        try
        {
            await using var db = Create(connection.Kind, biz.Secrets.Unprotect(connection.ConnectionStringProtected));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await db.OpenAsync(timeout.Token);
            message = $"Verbunden ({db.ServerVersion}).";
            ok = true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            message = "Keine Verbindung: " + ex.Message;
            ok = false;
        }

        connection.LastTestDate = biz.Now;
        connection.LastTestOk = ok;
        connection.LastTestMessage = message.Length > 500 ? message[..500] : message;
        await biz.Db.SaveChangesAsync();
        return message;
    }

    private static DbConnection Create(ConnectionKind kind, string connectionString) => kind switch
    {
        ConnectionKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
        ConnectionKind.MySql => new MySqlConnector.MySqlConnection(connectionString),
        _ => new Npgsql.NpgsqlConnection(connectionString),
    };

    // ================================================================================================ tasks

    public static IQueryable<SyncTask> Tasks(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.SyncTask.AsNoTracking().Include(t => t.DataConnection);
    }

    public static async Task<SyncTask?> TaskAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.SyncTask.AsNoTracking().Include(t => t.DataConnection).FirstOrDefaultAsync(t => t.Id == id);
    }

    public static async Task<List<SyncRun>> RunsAsync(Biz biz, long taskId)
    {
        biz.RequireAdmin();
        return await biz.Db.SyncRun.AsNoTracking().Where(r => r.SyncTaskId == taskId).OrderByDescending(r => r.StartDate).Take(30).ToListAsync();
    }

    public static List<SyncMapping> Mappings(SyncTask task)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SyncMapping>>(task.MappingJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The fields a sync task can fill (the name of the company of a contact comes through its key).</summary>
    public static async Task<List<CrmField>> TargetFieldsAsync(Biz biz, CrmEntity entity)
        => [.. (await CrmLogic.FieldsAsync(biz, entity)).Where(f => f.Key != "CompanyName")];

    public static async Task<SyncTask> SaveTaskAsync(Biz biz, long? id, SyncTask input, IReadOnlyList<SyncMapping> mappings)
    {
        biz.RequireAdmin();
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new BizException("Bitte gib einen Namen ein.", "Input.Name");
        }

        if (!await biz.Db.DataConnection.AnyAsync(c => c.Id == input.DataConnectionId))
        {
            throw new BizException("Bitte wähle eine Verbindung.", "Input.DataConnectionId");
        }

        CheckQuery(input.Query);
        if (string.IsNullOrWhiteSpace(input.KeyColumn))
        {
            throw new BizException("Bitte gib die Spalte an, die einen Datensatz eindeutig macht.", "Input.KeyColumn");
        }

        var known = (await TargetFieldsAsync(biz, input.Target)).Select(f => f.Key).ToHashSet();
        var clean = mappings.Where(m => !string.IsNullOrWhiteSpace(m.Column) && known.Contains(m.Field)).Select(m => new SyncMapping(m.Column.Trim(), m.Field)).DistinctBy(m => m.Field).ToList();
        var nameField = input.Target == CrmEntity.Company ? "Name" : "LastName";
        if (clean.All(m => m.Field != nameField))
        {
            throw new BizException(input.Target == CrmEntity.Company ? "Eine Spalte muss den Namen der Firma füllen." : "Eine Spalte muss den Nachnamen füllen.", "Mapping");
        }

        var task = id is { } existing ? await biz.Db.SyncTask.FirstOrDefaultAsync(t => t.Id == existing) ?? throw new BizException("Die Aufgabe gibt es nicht mehr.") : new SyncTask();
        task.Name = input.Name.Trim();
        task.DataConnectionId = input.DataConnectionId;
        task.Target = input.Target;
        task.Query = input.Query.Trim();
        task.KeyColumn = input.KeyColumn.Trim();
        task.CompanyKeyColumn = input.Target == CrmEntity.Contact ? OrganizationLogic.Clean(input.CompanyKeyColumn) : null;
        task.MappingJson = JsonSerializer.Serialize(clean, Json);
        task.IntervalMinutes = Math.Clamp(input.IntervalMinutes, 0, 60 * 24 * 7);
        task.IsEnabled = input.IsEnabled;
        task.DeleteMissing = input.DeleteMissing;
        if (id is null)
        {
            biz.Db.SyncTask.Add(task);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Sync-Aufgabe angelegt" : "Sync-Aufgabe geändert", nameof(SyncTask), task.Id, task.Name);
        return task;
    }

    public static async Task DeleteTaskAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var task = await biz.Db.SyncTask.FirstOrDefaultAsync(t => t.Id == id) ?? throw new BizException("Die Aufgabe gibt es nicht mehr.");
        biz.Db.SyncTask.Remove(task);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Sync-Aufgabe gelöscht", nameof(SyncTask), id, task.Name);
    }

    /// <summary>Only one reading statement: SELECT or WITH ... SELECT, no second statement, nothing that writes.</summary>
    public static void CheckQuery(string? query)
    {
        var q = (query ?? "").Trim().TrimEnd(';').Trim();
        if (q.Length == 0)
        {
            throw new BizException("Bitte gib die Abfrage ein.", "Input.Query");
        }

        if (!StartRegex().IsMatch(q))
        {
            throw new BizException("Die Abfrage muss mit SELECT oder WITH beginnen.", "Input.Query");
        }

        if (q.Contains(';') || WriteRegex().IsMatch(q))
        {
            throw new BizException("Die Abfrage darf nur lesen und nur aus einer Anweisung bestehen.", "Input.Query");
        }
    }

    [GeneratedRegex(@"^(select|with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|grant|revoke|exec|execute|call|into)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteRegex();

    /// <summary>The first ten rows of a query (the page shows them to choose the columns).</summary>
    public static async Task<SyncPreview> PreviewAsync(Biz biz, long connectionId, string query)
    {
        biz.RequireAdmin();
        CheckQuery(query);
        var connection = await biz.Db.DataConnection.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId) ?? throw new BizException("Bitte wähle eine Verbindung.");
        try
        {
            var rows = new List<IReadOnlyList<string>>();
            IReadOnlyList<string> columns = [];
            await ReadAsync(connection.Kind, biz.Secrets.Unprotect(connection.ConnectionStringProtected), query, 10, (names, values) =>
            {
                columns = names;
                rows.Add([.. values.Select(v => Text(v) ?? "")]);
            }, CancellationToken.None);
            return new SyncPreview(columns, rows, null);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return new SyncPreview([], [], ex.Message);
        }
    }

    private static async Task ReadAsync(ConnectionKind kind, string connectionString, string query, int max, Action<IReadOnlyList<string>, object?[]> row, CancellationToken ct)
    {
        await using var connection = Create(kind, connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        if (kind == ConnectionKind.PostgreSql)
        {
            await using var readOnly = connection.CreateCommand();
            readOnly.Transaction = transaction;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = query.Trim().TrimEnd(';');
        command.CommandTimeout = 120;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var count = 0;
        while (count < max && await reader.ReadAsync(ct))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }

            row(names, values);
            count++;
        }

        await transaction.RollbackAsync(ct);
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] => null,
        _ => value.ToString(),
    };

    // ================================================================================================ a run

    /// <summary>Starts a task now (the button); the scheduler calls <see cref="RunInternalAsync"/> the same way.</summary>
    public static async Task<SyncRun> RunNowAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var run = await RunInternalAsync(biz.Db, biz.Secrets, id, manual: true, biz.Clock, CancellationToken.None);
        await biz.Log.LogAsync("Sync-Aufgabe gestartet", nameof(SyncTask), id, run.Message);
        return run;
    }

    internal static async Task<SyncRun> RunInternalAsync(MtDbContext db, Configuration.SecretProtector secrets, long taskId, bool manual, TimeProvider clock, CancellationToken ct)
    {
        var task = await db.SyncTask.Include(t => t.DataConnection).FirstAsync(t => t.Id == taskId, ct);
        var run = new SyncRun { SyncTaskId = task.Id, StartDate = clock.GetUtcNow().UtcDateTime, Manual = manual };
        db.SyncRun.Add(run);
        try
        {
            CheckQuery(task.Query);
            var mappings = Mappings(task);
            var rows = new List<Dictionary<string, string?>>();
            await ReadAsync(task.DataConnection!.Kind, secrets.Unprotect(task.DataConnection.ConnectionStringProtected), task.Query, MaxRows + 1, (names, values) =>
            {
                var r = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < names.Count; i++)
                {
                    r[names[i]] = Text(values[i])?.Trim();
                }

                rows.Add(r);
            }, ct);

            if (rows.Count > MaxRows)
            {
                throw new InvalidOperationException($"Die Abfrage liefert mehr als {MaxRows:N0} Zeilen. Bitte schränke sie ein.");
            }

            if (rows.Count > 0 && !rows[0].ContainsKey(task.KeyColumn))
            {
                throw new InvalidOperationException($"Die Schlüsselspalte „{task.KeyColumn}“ ist nicht im Ergebnis.");
            }

            run.Read = rows.Count;
            var byKey = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var key = r.GetValueOrDefault(task.KeyColumn);
                if (string.IsNullOrEmpty(key) || !byKey.TryAdd(key, r))
                {
                    run.Skipped++;
                }
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (task.Target == CrmEntity.Company)
            {
                await SyncCompaniesAsync(db, task, mappings, byKey, run, now, ct);
            }
            else
            {
                await SyncContactsAsync(db, task, mappings, byKey, run, now, ct);
            }

            run.Ok = true;
            run.Message = $"{run.Read} gelesen, {run.Created} neu, {run.Updated} geändert, {run.Unchanged} unverändert" +
                (run.Deleted > 0 ? $", {run.Deleted} entfernt" : "") + (run.Skipped > 0 ? $", {run.Skipped} übersprungen (ohne oder doppelter Schlüssel)" : "") + ".";
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or TimeoutException or BizException or System.Net.Sockets.SocketException)
        {
            db.ChangeTracker.Clear();
            db.SyncRun.Add(run);
            task = await db.SyncTask.FirstAsync(t => t.Id == taskId, ct);
            run.Ok = false;
            run.Message = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
        }

        run.EndDate = clock.GetUtcNow().UtcDateTime;
        task.LastRunDate = run.StartDate;
        task.LastRunOk = run.Ok;
        task.LastStatus = run.Message;
        await db.SaveChangesAsync(ct);
        return run;
    }

    private static async Task SyncCompaniesAsync(MtDbContext db, SyncTask task, List<SyncMapping> mappings, Dictionary<string, Dictionary<string, string?>> rows, SyncRun run, DateTime now, CancellationToken ct)
    {
        var existing = await db.Company.Where(c => c.SyncTaskId == task.Id).ToListAsync(ct);
        var byKey = existing.Where(c => c.ExternalId != null).GroupBy(c => c.ExternalId!).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, row) in rows)
        {
            var company = byKey.GetValueOrDefault(key);
            var isNew = company is null;
            company ??= new Company { SyncTaskId = task.Id, ExternalId = key };
            var changed = Apply(mappings, row, (field, value) => SetCompany(company, field, value));
            if (string.IsNullOrWhiteSpace(company.Name))
            {
                run.Skipped++;
                continue;
            }

            if (isNew)
            {
                company.SyncedDate = now;
                db.Company.Add(company);
                run.Created++;
            }
            else if (changed)
            {
                company.SyncedDate = now;
                run.Updated++;
            }
            else
            {
                run.Unchanged++;
            }
        }

        if (task.DeleteMissing)
        {
            foreach (var gone in existing.Where(c => c.ExternalId is null || !rows.ContainsKey(c.ExternalId)))
            {
                db.Company.Remove(gone);
                run.Deleted++;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SyncContactsAsync(MtDbContext db, SyncTask task, List<SyncMapping> mappings, Dictionary<string, Dictionary<string, string?>> rows, SyncRun run, DateTime now, CancellationToken ct)
    {
        var existing = await db.Contact.Where(c => c.SyncTaskId == task.Id).ToListAsync(ct);
        var byKey = existing.Where(c => c.ExternalId != null).GroupBy(c => c.ExternalId!).ToDictionary(g => g.Key, g => g.First());
        var companies = task.CompanyKeyColumn is null
            ? []
            : await db.Company.AsNoTracking().Where(c => c.ExternalId != null).Select(c => new { c.Id, c.ExternalId }).ToListAsync(ct);
        var companyByKey = companies.GroupBy(c => c.ExternalId!).ToDictionary(g => g.Key, g => g.First().Id);
        foreach (var (key, row) in rows)
        {
            var contact = byKey.GetValueOrDefault(key);
            var isNew = contact is null;
            contact ??= new Contact { SyncTaskId = task.Id, ExternalId = key };
            var changed = Apply(mappings, row, (field, value) => SetContact(contact, field, value));
            if (task.CompanyKeyColumn is { } column)
            {
                var companyKey = row.GetValueOrDefault(column);
                var companyId = companyKey is null ? null : companyByKey.TryGetValue(companyKey, out var cid) ? cid : (long?)null;
                if (contact.CompanyId != companyId)
                {
                    contact.CompanyId = companyId;
                    changed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(contact.LastName))
            {
                run.Skipped++;
                continue;
            }

            if (isNew)
            {
                contact.SyncedDate = now;
                db.Contact.Add(contact);
                run.Created++;
            }
            else if (changed)
            {
                contact.SyncedDate = now;
                run.Updated++;
            }
            else
            {
                run.Unchanged++;
            }
        }

        if (task.DeleteMissing)
        {
            foreach (var gone in existing.Where(c => c.ExternalId is null || !rows.ContainsKey(c.ExternalId)))
            {
                db.Contact.Remove(gone);
                run.Deleted++;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool Apply(List<SyncMapping> mappings, Dictionary<string, string?> row, Func<string, string?, bool> set)
    {
        var changed = false;
        foreach (var m in mappings)
        {
            if (row.TryGetValue(m.Column, out var value))
            {
                changed |= set(m.Field, string.IsNullOrWhiteSpace(value) ? null : value);
            }
        }

        return changed;
    }

    private static bool SetCompany(Company c, string field, string? value)
    {
        string? old = CrmLogic.ValueOf(c, field);
        if (old == value)
        {
            return false;
        }

        switch (field)
        {
            case "Name": c.Name = value ?? ""; break;
            case "CustomerNumber": c.CustomerNumber = value; break;
            case "Street": c.Street = value; break;
            case "PostalCode": c.PostalCode = value; break;
            case "City": c.City = value; break;
            case "Country": c.Country = value; break;
            case "Phone": c.Phone = value; break;
            case "Email": c.Email = value; break;
            case "Website": c.Website = value; break;
            case "Notes": c.Notes = value; break;
            default:
                if (!field.StartsWith("x.", StringComparison.Ordinal))
                {
                    return false;
                }

                c.ExtraJson = SetExtra(c.ExtraJson, field[2..], value);
                break;
        }

        return true;
    }

    private static bool SetContact(Contact c, string field, string? value)
    {
        string? old = field switch
        {
            "Street" => c.Street,
            "PostalCode" => c.PostalCode,
            "City" => c.City,
            "Country" => c.Country,
            "CustomerNumber" => c.CustomerNumber,
            _ => CrmLogic.ValueOf(c, field),
        };
        if (old == value)
        {
            return false;
        }

        switch (field)
        {
            case "Salutation": c.Salutation = value; break;
            case "FirstName": c.FirstName = value ?? ""; break;
            case "LastName": c.LastName = value ?? ""; break;
            case "Position": c.Position = value; break;
            case "Email": c.Email = value?.ToLowerInvariant(); break;
            case "Phone": c.Phone = value; break;
            case "Mobile": c.Mobile = value; break;
            case "CustomerNumber": c.CustomerNumber = value; break;
            case "Street": c.Street = value; break;
            case "PostalCode": c.PostalCode = value; break;
            case "City": c.City = value; break;
            case "Country": c.Country = value; break;
            case "Notes": c.Notes = value; break;
            default:
                if (!field.StartsWith("x.", StringComparison.Ordinal))
                {
                    return false;
                }

                c.ExtraJson = SetExtra(c.ExtraJson, field[2..], value);
                break;
        }

        return true;
    }

    private static string SetExtra(string json, string key, string? value)
    {
        var values = CrmLogic.Extras(json);
        if (value is null)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = value;
        }

        return JsonSerializer.Serialize(values, Json);
    }
}

/// <summary>Starts the sync tasks that are due (every 30 seconds it looks). A run never throws at the scheduler: it writes its outcome on the task.</summary>
public sealed class SyncScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MtDbContext>();
                var now = clock.GetUtcNow().UtcDateTime;
                var due = await db.SyncTask.AsNoTracking()
                    .Where(t => t.IsEnabled && t.IntervalMinutes > 0)
                    .Select(t => new { t.Id, t.LastRunDate, t.IntervalMinutes })
                    .ToListAsync(stoppingToken);
                foreach (var task in due.Where(t => t.LastRunDate is null || t.LastRunDate.Value.AddMinutes(t.IntervalMinutes) <= now))
                {
                    using var runScope = scopes.CreateScope();
                    var runDb = runScope.ServiceProvider.GetRequiredService<MtDbContext>();
                    var user = runScope.ServiceProvider.GetRequiredService<ICurrentUser>();
                    using (user.RunAs(null, "Sync"))
                    {
                        await SyncLogic.RunInternalAsync(runDb, runScope.ServiceProvider.GetRequiredService<Configuration.SecretProtector>(), task.Id, manual: false, clock, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The sync scheduler failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
