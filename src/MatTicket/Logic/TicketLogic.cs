using System.Linq.Expressions;
using System.Text.Json;
using MatTicket.Core.Text;
using MatTicket.Data;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>One answer of a form as it is kept on the ticket: what was asked, the key, and the value as the customer saw it.</summary>
public sealed record TicketAnswer(string Label, string Key, string Value);

/// <summary>
/// Tickets: who sees which, creating, changing, moving between departments and teams, answering. Administrators see every ticket; an agent
/// sees the tickets of the departments and teams they are in, the ones assigned to them and the ones that belong to no department yet.
/// </summary>
public static class TicketLogic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ================================================================================================ who sees what

    public static async Task<IQueryable<Ticket>> VisibleAsync(Biz biz)
    {
        biz.RequireAgent();
        var query = biz.Db.Ticket.AsNoTracking();
        return biz.IsAdmin ? query : query.Where(await VisibleToAgentAsync(biz));
    }

    private static async Task<Expression<Func<Ticket, bool>>> VisibleToAgentAsync(Biz biz)
    {
        var scope = await biz.ScopeAsync();
        var me = biz.UserId;
        var departments = scope.DepartmentIds.ToList();
        var teams = scope.TeamIds.ToList();
        return t => t.AssigneeUserId == me || t.DepartmentId == null
            || (t.DepartmentId != null && departments.Contains(t.DepartmentId.Value))
            || (t.TeamId != null && teams.Contains(t.TeamId.Value));
    }

    /// <summary>The ticket with everything its page shows, or null when it does not exist or is not for this person.</summary>
    public static async Task<Ticket?> GetAsync(Biz biz, long id)
    {
        var visible = await VisibleAsync(biz);
        return await visible
            .Include(t => t.Category).ThenInclude(c => c!.Parent)
            .Include(t => t.Department).Include(t => t.Team).Include(t => t.Assignee)
            .Include(t => t.Contact).ThenInclude(c => c!.Company).Include(t => t.Company).Include(t => t.TicketForm)
            .Include(t => t.Messages.OrderBy(m => m.CreateDate))
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    private static async Task<Ticket> TrackedAsync(Biz biz, long id)
    {
        biz.RequireAgent();
        var query = biz.Db.Ticket.AsQueryable();
        if (!biz.IsAdmin)
        {
            query = query.Where(await VisibleToAgentAsync(biz));
        }

        return await query.FirstOrDefaultAsync(t => t.Id == id) ?? throw new ForbiddenException("Dieses Ticket gibt es nicht, oder es ist nicht für dich.");
    }

    public static IReadOnlyList<TicketAnswer> Answers(Ticket ticket)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TicketAnswer>>(ticket.AnswersJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string AnswersToJson(IEnumerable<TicketAnswer> answers) => JsonSerializer.Serialize(answers, Json);

    // ================================================================================================ the numbers of the start page

    public sealed record Badges(int New, int Mine);

    /// <summary>The badges of the menu: new tickets the person sees, and open ones assigned to them.</summary>
    public static async Task<Badges> BadgesAsync(Biz biz)
    {
        if (!biz.IsAgent)
        {
            return new Badges(0, 0);
        }

        var visible = await VisibleAsync(biz);
        var me = biz.UserId;
        return new Badges(
            await visible.CountAsync(t => t.Status == TicketStatus.New),
            await visible.CountAsync(t => t.AssigneeUserId == me && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed));
    }

    public sealed record Overview(int Open, int New, int Mine, int Unassigned, int Waiting, int ResolvedThisWeek, IReadOnlyList<(string Name, int Count)> ByDepartment, IReadOnlyList<int> CreatedLast14Days);

    public static async Task<Overview> OverviewAsync(Biz biz)
    {
        var visible = await VisibleAsync(biz);
        var me = biz.UserId;
        var now = biz.Now;
        var open = visible.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        var since = now.Date.AddDays(-13);
        var created = await visible.Where(t => t.CreateDate >= since).Select(t => t.CreateDate).ToListAsync();
        var byDepartment = await open.GroupBy(t => t.Department == null ? "Ohne Abteilung" : t.Department.Name)
            .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).Take(8).ToListAsync();
        return new Overview(
            await open.CountAsync(),
            await visible.CountAsync(t => t.Status == TicketStatus.New),
            await open.CountAsync(t => t.AssigneeUserId == me),
            await open.CountAsync(t => t.AssigneeUserId == null),
            await visible.CountAsync(t => t.Status == TicketStatus.Waiting),
            await visible.CountAsync(t => t.ResolvedDate >= now.AddDays(-7)),
            [.. byDepartment.Select(d => (d.Key, d.Count))],
            [.. Enumerable.Range(0, 14).Select(i => created.Count(c => c.Date == since.AddDays(i)))]);
    }

    // ================================================================================================ creating

    public sealed record NewTicket(string Subject, string Description, TicketPriority Priority, long? CategoryId, long? DepartmentId, long? TeamId, long? AssigneeUserId,
        long? ContactId, string? RequesterName, string? RequesterEmail, string? RequesterPhone);

    /// <summary>A ticket an agent writes down (a call, a visit). The contact fills the requester when it is chosen.</summary>
    public static async Task<Ticket> CreateByAgentAsync(Biz biz, NewTicket input)
    {
        biz.RequireAgent();
        if (string.IsNullOrWhiteSpace(input.Subject))
        {
            throw new BizException("Bitte gib einen Betreff ein.", "Input.Subject");
        }

        var rules = SettingLogic.Get<TicketSettings>(biz, SettingStore.Tickets);
        if (rules.AgentsOnlyOwnDepartments && !biz.IsAdmin)
        {
            var scope = await biz.ScopeAsync();
            var placed = new Ticket();
            await ApplyPlacementAsync(biz.Db, placed, input.CategoryId, input.DepartmentId, input.TeamId);
            if (placed.DepartmentId is not { } department || !scope.DepartmentIds.Contains(department))
            {
                throw new BizException("Du kannst Tickets nur in den Abteilungen deiner Teams anlegen. Wähle eine davon (oder eine Kategorie, die dorthin gehört).", "Input.DepartmentId");
            }
        }

        var ticket = new Ticket
        {
            Subject = Limit(input.Subject, 300),
            Description = HtmlSafe.Clean(input.Description),
            Priority = input.Priority,
            Source = TicketSource.Agent,
            Status = input.AssigneeUserId is null ? TicketStatus.New : TicketStatus.Open,
            RequesterName = Limit(input.RequesterName, 200),
            RequesterEmail = Limit(input.RequesterEmail, 254),
            RequesterPhone = OrganizationLogic.Clean(input.RequesterPhone),
            LastActivityDate = biz.Now,
        };

        await ApplyPlacementAsync(biz.Db, ticket, input.CategoryId, input.DepartmentId, input.TeamId);
        if (input.AssigneeUserId is { } assignee && await biz.Db.User.AnyAsync(u => u.Id == assignee && u.IsActive))
        {
            ticket.AssigneeUserId = assignee;
        }

        if (input.ContactId is { } contactId && await biz.Db.Contact.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId) is { } contact)
        {
            ticket.ContactId = contact.Id;
            ticket.CompanyId = contact.CompanyId;
            if (ticket.RequesterName.Length == 0)
            {
                ticket.RequesterName = contact.FullName;
            }

            if (ticket.RequesterEmail.Length == 0)
            {
                ticket.RequesterEmail = contact.Email ?? "";
            }

            ticket.RequesterPhone ??= contact.Phone ?? contact.Mobile;
        }

        biz.Db.Ticket.Add(ticket);
        await biz.Db.SaveChangesAsync();
        await NumberAsync(biz.Db, ticket);
        await AddSystemAsync(biz.Db, ticket.Id, $"Ticket angelegt von {biz.User.Name}.");
        await biz.Log.LogAsync("Ticket angelegt", nameof(Ticket), ticket.Id, ticket.Number);
        biz.Service<Services.Mail.MailQueue>().TicketCreated(ticket.Id, toCustomer: false);
        return ticket;
    }

    /// <summary>Category, department and team: the category fills what was not given, a team that is not in the department is dropped.</summary>
    internal static async Task ApplyPlacementAsync(MtDbContext db, Ticket ticket, long? categoryId, long? departmentId, long? teamId)
    {
        var category = categoryId is { } cid ? await db.Category.AsNoTracking().Include(c => c.Parent).FirstOrDefaultAsync(c => c.Id == cid) : null;
        ticket.CategoryId = category?.Id;
        departmentId ??= category?.DepartmentId ?? category?.Parent?.DepartmentId;
        teamId ??= departmentId == (category?.DepartmentId ?? category?.Parent?.DepartmentId) ? category?.TeamId ?? category?.Parent?.TeamId : null;

        ticket.DepartmentId = departmentId is { } did && await db.Department.AnyAsync(d => d.Id == did) ? did : null;
        ticket.TeamId = teamId is { } tid && await db.Team.AnyAsync(t => t.Id == tid && (ticket.DepartmentId == null || t.DepartmentId == ticket.DepartmentId)) ? tid : null;
        if (ticket.TeamId is { } team && ticket.DepartmentId is null)
        {
            ticket.DepartmentId = await db.Team.Where(t => t.Id == team).Select(t => (long?)t.DepartmentId).FirstOrDefaultAsync();
        }
    }

    /// <summary>The number people quote: the year and the id ("T26-00042"), unique because the id is.</summary>
    internal static async Task NumberAsync(MtDbContext db, Ticket ticket)
    {
        ticket.Number = $"T{ticket.CreateDate:yy}-{ticket.Id:00000}";
        await db.SaveChangesAsync();
    }

    internal static async Task AddSystemAsync(MtDbContext db, long ticketId, string text)
    {
        db.TicketMessage.Add(new TicketMessage { TicketId = ticketId, Kind = MessageKind.System, Body = HtmlSafe.FromText(text), AuthorName = "System" });
        await db.SaveChangesAsync();
    }

    // ================================================================================================ changing

    public sealed record Properties(TicketStatus Status, TicketPriority Priority, long? CategoryId, long? DepartmentId, long? TeamId, long? AssigneeUserId, DateOnly? DueDate);

    /// <summary>Changes what the side panel of a ticket shows; every change is written into the history in words.</summary>
    public static async Task SavePropertiesAsync(Biz biz, long id, Properties input)
    {
        var ticket = await TrackedAsync(biz, id);
        var names = await NamesAsync(biz.Db);
        var changes = new List<string>();

        if (ticket.Status != input.Status)
        {
            changes.Add($"Status: {UiKit.Labels.Of(ticket.Status)} → {UiKit.Labels.Of(input.Status)}");
            SetStatus(ticket, input.Status, biz.Now);
        }

        if (ticket.Priority != input.Priority)
        {
            changes.Add($"Priorität: {UiKit.Labels.Of(ticket.Priority)} → {UiKit.Labels.Of(input.Priority)}");
            ticket.Priority = input.Priority;
        }

        if (ticket.CategoryId != input.CategoryId)
        {
            var category = input.CategoryId is { } c && await biz.Db.Category.AnyAsync(x => x.Id == c) ? input.CategoryId : null;
            changes.Add($"Kategorie: {names.Category(ticket.CategoryId)} → {names.Category(category)}");
            ticket.CategoryId = category;
        }

        await MoveInternalAsync(biz, ticket, input.DepartmentId, input.TeamId, names, changes);

        if (ticket.AssigneeUserId != input.AssigneeUserId)
        {
            var assignee = input.AssigneeUserId is { } a && await biz.Db.User.AnyAsync(u => u.Id == a && u.IsActive) ? input.AssigneeUserId : null;
            changes.Add($"Zugewiesen: {names.User(ticket.AssigneeUserId)} → {names.User(assignee)}");
            ticket.AssigneeUserId = assignee;
            if (assignee is not null && ticket.Status == TicketStatus.New)
            {
                ticket.Status = TicketStatus.Open;
            }
        }

        var due = input.DueDate is { } d ? Services.Fmt.UtcFromLocal(d.ToDateTime(new TimeOnly(18, 0))) : (DateTime?)null;
        if (ticket.DueDate != due)
        {
            changes.Add($"Fällig: {(due is null ? "–" : Services.Fmt.Date(due))}");
            ticket.DueDate = due;
        }

        if (changes.Count == 0)
        {
            return;
        }

        ticket.LastActivityDate = biz.Now;
        await biz.Db.SaveChangesAsync();
        await AddSystemAsync(biz.Db, ticket.Id, $"{biz.User.Name}: " + string.Join("; ", changes) + ".");
    }

    /// <summary>Pushes a ticket to another department or team (with a note for the history). The person it was assigned to keeps it only while they are in the new place.</summary>
    public static async Task MoveAsync(Biz biz, long id, long? departmentId, long? teamId, string? note)
    {
        var ticket = await TrackedAsync(biz, id);
        var names = await NamesAsync(biz.Db);
        var changes = new List<string>();
        await MoveInternalAsync(biz, ticket, departmentId, teamId, names, changes);
        if (changes.Count == 0)
        {
            throw new BizException("Das Ticket ist schon dort.");
        }

        if (ticket.AssigneeUserId is { } assignee
            && !await biz.Db.TeamMember.AnyAsync(m => m.UserId == assignee && (m.TeamId == ticket.TeamId || m.Team!.DepartmentId == ticket.DepartmentId)))
        {
            changes.Add($"Zuweisung an {names.User(assignee)} aufgehoben");
            ticket.AssigneeUserId = null;
        }

        ticket.LastActivityDate = biz.Now;
        await biz.Db.SaveChangesAsync();
        var text = $"{biz.User.Name} hat das Ticket verschoben: " + string.Join("; ", changes) + ".";
        if (!string.IsNullOrWhiteSpace(note))
        {
            text += "\n\nNotiz: " + note.Trim();
        }

        await AddSystemAsync(biz.Db, ticket.Id, text);
        await biz.Log.LogAsync("Ticket verschoben", nameof(Ticket), ticket.Id, ticket.Number);
        biz.Service<Services.Mail.MailQueue>().TicketMoved(ticket.Id);
    }

    private static async Task MoveInternalAsync(Biz biz, Ticket ticket, long? departmentId, long? teamId, Names names, List<string> changes)
    {
        if (teamId is { } tid)
        {
            var teamDepartment = await biz.Db.Team.Where(t => t.Id == tid).Select(t => (long?)t.DepartmentId).FirstOrDefaultAsync();
            if (teamDepartment is null)
            {
                teamId = null;
            }
            else if (departmentId is null)
            {
                departmentId = teamDepartment;
            }
            else if (teamDepartment != departmentId)
            {
                throw new BizException("Das Team gehört nicht zu der gewählten Abteilung.", "TeamId");
            }
        }

        if (departmentId is { } did && !await biz.Db.Department.AnyAsync(d => d.Id == did))
        {
            departmentId = null;
        }

        if (ticket.DepartmentId != departmentId)
        {
            changes.Add($"Abteilung: {names.Department(ticket.DepartmentId)} → {names.Department(departmentId)}");
            ticket.DepartmentId = departmentId;
        }

        if (ticket.TeamId != teamId)
        {
            changes.Add($"Team: {names.Team(ticket.TeamId)} → {names.Team(teamId)}");
            ticket.TeamId = teamId;
        }
    }

    /// <summary>"Übernehmen": the agent assigns the ticket to themselves.</summary>
    public static async Task TakeAsync(Biz biz, long id)
    {
        var ticket = await TrackedAsync(biz, id);
        if (ticket.AssigneeUserId == biz.UserId)
        {
            return;
        }

        ticket.AssigneeUserId = biz.UserId;
        if (ticket.Status == TicketStatus.New)
        {
            ticket.Status = TicketStatus.Open;
        }

        ticket.LastActivityDate = biz.Now;
        await biz.Db.SaveChangesAsync();
        await AddSystemAsync(biz.Db, ticket.Id, $"{biz.User.Name} hat das Ticket übernommen.");
    }

    private static void SetStatus(Ticket ticket, TicketStatus status, DateTime now)
    {
        ticket.Status = status;
        if (status is TicketStatus.Resolved or TicketStatus.Closed)
        {
            ticket.ResolvedDate ??= now;
        }
        else
        {
            ticket.ResolvedDate = null;
        }
    }

    /// <summary>An answer to the customer (they get a mail) or an internal note. <paramref name="status"/> is the status after it, if it changes.</summary>
    public static async Task<TicketMessage> ReplyAsync(Biz biz, long id, string html, bool internalNote, TicketStatus? status, long? aiRunId = null)
    {
        var ticket = await TrackedAsync(biz, id);
        if (HtmlSafe.ContainsScript(html))
        {
            throw new BizException("Die Nachricht enthält ein Skript. Das ist nicht erlaubt.", "Body");
        }

        var body = HtmlSafe.Clean(html);
        if (body.Length == 0)
        {
            throw new BizException(internalNote ? "Bitte schreibe eine Notiz." : "Bitte schreibe eine Antwort.", "Body");
        }

        var me = await biz.Db.User.AsNoTracking().FirstAsync(u => u.Id == biz.UserId);
        var message = new TicketMessage
        {
            TicketId = ticket.Id,
            Kind = internalNote ? MessageKind.Note : MessageKind.Reply,
            Body = body,
            AuthorUserId = me.Id,
            AuthorName = me.DisplayName,
            AiRunId = aiRunId,
        };
        biz.Db.TicketMessage.Add(message);
        if (!internalNote)
        {
            ticket.FirstResponseDate ??= biz.Now;
            ticket.AssigneeUserId ??= me.Id;
            if (ticket.Status == TicketStatus.New)
            {
                ticket.Status = TicketStatus.Open;
            }
        }

        if (status is { } s && s != ticket.Status)
        {
            SetStatus(ticket, s, biz.Now);
        }

        ticket.LastActivityDate = biz.Now;
        await biz.Db.SaveChangesAsync();
        if (!internalNote)
        {
            biz.Service<Services.Mail.MailQueue>().Replied(ticket.Id, message.Id);
        }

        return message;
    }

    public static async Task DeleteAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var ticket = await biz.Db.Ticket.FirstOrDefaultAsync(t => t.Id == id) ?? throw new BizException("Das Ticket gibt es nicht mehr.");
        biz.Db.Ticket.Remove(ticket);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Ticket gelöscht", nameof(Ticket), id, ticket.Number);
    }

    /// <summary>Links the ticket to a contact of the CRM (and its company).</summary>
    public static async Task LinkContactAsync(Biz biz, long id, long? contactId)
    {
        var ticket = await TrackedAsync(biz, id);
        var contact = contactId is { } cid ? await biz.Db.Contact.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid) : null;
        ticket.ContactId = contact?.Id;
        ticket.CompanyId = contact?.CompanyId;
        await biz.Db.SaveChangesAsync();
        await AddSystemAsync(biz.Db, ticket.Id, contact is null ? $"{biz.User.Name} hat die Verknüpfung zum Kontakt gelöst." : $"{biz.User.Name} hat den Kontakt {contact.FullName} verknüpft.");
    }

    /// <summary>Contacts whose address or name matches the requester: the suggestions on a ticket without a contact.</summary>
    public static async Task<List<Choice>> ContactSuggestionsAsync(Biz biz, Ticket ticket)
    {
        biz.RequireAgent();
        var email = ticket.RequesterEmail.Trim().ToLowerInvariant();
        var last = ticket.RequesterName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return await biz.Db.Contact.AsNoTracking()
            .Where(c => (email.Length > 0 && c.Email != null && c.Email.ToLower() == email) || (last.Length > 2 && c.LastName == last))
            .OrderBy(c => c.LastName).Take(5)
            .Select(c => new Choice(c.Id, c.FirstName + " " + c.LastName, c.Company != null ? c.Company.Name : c.Email, null))
            .ToListAsync();
    }

    // ================================================================================================ names for the history

    private sealed record Names(Dictionary<long, string> Departments, Dictionary<long, string> Teams, Dictionary<long, string> Users, Dictionary<long, string> Categories)
    {
        public string Department(long? id) => id is { } v && Departments.TryGetValue(v, out var n) ? n : "–";

        public string Team(long? id) => id is { } v && Teams.TryGetValue(v, out var n) ? n : "–";

        public string User(long? id) => id is { } v && Users.TryGetValue(v, out var n) ? n : "niemand";

        public string Category(long? id) => id is { } v && Categories.TryGetValue(v, out var n) ? n : "–";
    }

    private static async Task<Names> NamesAsync(MtDbContext db)
        => new(
            await db.Department.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name),
            await db.Team.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name),
            await db.User.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName),
            await db.Category.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name));

    internal static string Limit(string? text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }
}
