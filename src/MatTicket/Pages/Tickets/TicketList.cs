using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Tickets;

/// <summary>
/// The list of tickets, shared by "Alle Tickets" and "Meine Tickets": the scopes are link tabs above the toolbar (new, in progress, waiting,
/// done), search, filters (department, team, category, priority, assignee) and sorting in the toolbar, the pager below.
/// </summary>
public abstract class TicketListModel(Biz biz) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public LinkTabsModel Tabs { get; private set; } = null!;

    protected abstract string Path { get; }

    protected abstract bool OnlyMine { get; }

    public async Task OnGetAsync(string? scope)
    {
        var all = await TicketLogic.VisibleAsync(biz);
        var me = biz.UserId;
        if (OnlyMine)
        {
            all = all.Where(t => t.AssigneeUserId == me);
        }

        scope ??= OnlyMine ? "offen" : "aktiv";
        var scoped = scope switch
        {
            "neu" => all.Where(t => t.Status == TicketStatus.New),
            "offen" => all.Where(t => t.Status == TicketStatus.New || t.Status == TicketStatus.Open),
            "wartend" => all.Where(t => t.Status == TicketStatus.Waiting),
            "erledigt" => all.Where(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
            "alle" => all,
            _ => all.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
        };

        var counts = await all.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        int Count(params TicketStatus[] s) => counts.Where(c => s.Contains(c.Key)).Sum(c => c.Count);
        Tabs = new LinkTabsModel("Tickets nach Stand",
        [
            new("aktiv", "Offen", $"{Path}?scope=aktiv", "inbox", Count(TicketStatus.New, TicketStatus.Open, TicketStatus.Waiting)),
            new("neu", "Neu", $"{Path}?scope=neu", null, Count(TicketStatus.New)),
            new("offen", "In Arbeit", $"{Path}?scope=offen", null, Count(TicketStatus.New, TicketStatus.Open)),
            new("wartend", "Wartet auf Kunde", $"{Path}?scope=wartend", null, Count(TicketStatus.Waiting)),
            new("erledigt", "Erledigt", $"{Path}?scope=erledigt", null, Count(TicketStatus.Resolved, TicketStatus.Closed)),
            new("alle", "Alle", $"{Path}?scope=alle", null, counts.Sum(c => c.Count)),
        ], scope);

        var departments = await OrganizationLogic.DepartmentChoicesAsync(biz);
        var teams = await OrganizationLogic.TeamChoicesAsync(biz);
        var categories = await CategoryLogic.ChoicesAsync(biz);
        var agents = await OrganizationLogic.AgentChoicesAsync(biz);
        var filters = new List<FilterSpec<Ticket>>
        {
            new("abteilung", "Abteilung", [new("ohne", "Ohne Abteilung"), .. departments.Select(d => new FilterOption(d.Id.ToString(), d.Name))],
                (q, v) => v == "ohne" ? q.Where(t => t.DepartmentId == null) : q.Where(t => t.DepartmentId == long.Parse(v))),
            new("team", "Team", [.. teams.Select(t => new FilterOption(t.Id.ToString(), $"{t.Name} ({t.Detail})"))], (q, v) => q.Where(t => t.TeamId == long.Parse(v))),
            new("kategorie", "Kategorie", [.. categories.Select(c => new FilterOption(c.Id.ToString(), c.Name))],
                (q, v) => q.Where(t => t.CategoryId == long.Parse(v) || (t.Category != null && t.Category.ParentId == long.Parse(v)))),
            new("prio", "Priorität", ListBuilder.EnumOptions<TicketPriority>(Labels.Of), (q, v) => q.Where(t => t.Priority == Enum.Parse<TicketPriority>(v))),
        };
        if (!OnlyMine)
        {
            filters.Add(new("bearbeiter", "Bearbeitet von", [new("ich", "Mir"), new("niemand", "Niemandem"), .. agents.Select(a => new FilterOption(a.Id.ToString(), a.Name))],
                (q, v) => v switch
                {
                    "ich" => q.Where(t => t.AssigneeUserId == me),
                    "niemand" => q.Where(t => t.AssigneeUserId == null),
                    _ => q.Where(t => t.AssigneeUserId == long.Parse(v)),
                }));
        }

        var spec = new ListSpec<Ticket>
        {
            Id = "tickets",
            Action = Path,
            Label = "Tickets",
            Noun = "Tickets",
            SearchPlaceholder = "Nummer, Betreff, Kunde ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(t => EF.Functions.ILike(t.Number, like) || EF.Functions.ILike(t.Subject, like) || EF.Functions.ILike(t.RequesterName, like)
                    || EF.Functions.ILike(t.RequesterEmail, like) || (t.Company != null && EF.Functions.ILike(t.Company.Name, like)));
            },
            Filters = filters,
            Sorts = new Dictionary<string, (string, Func<IQueryable<Ticket>, bool, IOrderedQueryable<Ticket>>)>
            {
                ["aktivitaet"] = ("Letzte Aktivität", (q, d) => d ? q.OrderByDescending(t => t.LastActivityDate) : q.OrderBy(t => t.LastActivityDate)),
                ["erstellt"] = ("Erstellt", (q, d) => d ? q.OrderByDescending(t => t.CreateDate) : q.OrderBy(t => t.CreateDate)),
                ["prio"] = ("Priorität", (q, d) => d ? q.OrderByDescending(t => t.Priority == TicketPriority.Urgent ? 3 : t.Priority == TicketPriority.High ? 2 : t.Priority == TicketPriority.Normal ? 1 : 0).ThenByDescending(t => t.LastActivityDate)
                    : q.OrderBy(t => t.Priority == TicketPriority.Urgent ? 3 : t.Priority == TicketPriority.High ? 2 : t.Priority == TicketPriority.Normal ? 1 : 0).ThenByDescending(t => t.LastActivityDate)),
                ["faellig"] = ("Fällig", (q, d) => d ? q.OrderByDescending(t => t.DueDate) : q.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate)),
                ["nummer"] = ("Nummer", (q, d) => d ? q.OrderByDescending(t => t.Id) : q.OrderBy(t => t.Id)),
            },
            DefaultSort = "aktivitaet",
            DefaultDescending = true,
            PageSize = 25,
            Keep = new Dictionary<string, string?> { ["scope"] = scope },
        };

        var result = await ListBuilder.RunAsync(scoped.Include(t => t.Department).Include(t => t.Team).Include(t => t.Assignee).Include(t => t.Category).Include(t => t.Company), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var now = biz.Now;
        Table = TableModel.Of("ticket-table", "Tickets", result.Items,
        [
            (new TableColumn("Ticket"), t => Ui.NameWithSub($"{t.Number} · {t.Subject}", $"/Tickets/Details?id={t.Id}", string.Join(" · ", new[] { t.Company?.Name ?? t.RequesterCompany, t.RequesterName }.Where(s => !string.IsNullOrWhiteSpace(s))))),
            (new TableColumn("Status"), t => Labels.Pill(t.Status)),
            (new TableColumn("Priorität", HideOnPhone: true), t => Labels.Pill(t.Priority)),
            (new TableColumn("Abteilung / Team", HideOnPhone: true), t => t.Department is null ? Ui.None() : Ui.NameWithSub(t.Department.Name, null, t.Team?.Name)),
            (new TableColumn("Bearbeiter", HideOnPhone: true), t => Ui.Person(t.Assignee?.DisplayName)),
            (new TableColumn("Aktivität", HideOnPhone: true), t => Ui.Text(Fmt.Ago(t.LastActivityDate, now))),
        ],
        t => [new RowAction("eye", "Öffnen", $"/Tickets/Details?id={t.Id}")]);
        Table = Table with
        {
            EmptyTitle = OnlyMine ? "Dir ist nichts zugewiesen" : "Keine Tickets",
            EmptyText = result.Toolbar.SearchActive || result.Toolbar.ActiveFilters > 0 ? "Ändere die Suche oder den Filter." : "Hier ist gerade nichts zu tun.",
            EmptyIcon = "inbox",
        };
    }
}
