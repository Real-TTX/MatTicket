using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Sync.Tasks;

/// <summary>The sync tasks: which query fills which records, how often, and how the last run went.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Sync-Aufgaben", "Kunden aus anderen Datenbanken übernehmen: welche Abfrage, welche Felder, wie oft.", ("Einstellungen", null), ("Sync-Aufgaben", null));
        var spec = new ListSpec<SyncTask>
        {
            Id = "tasks",
            Action = "/Admin/Sync/Tasks",
            Label = "Sync-Aufgaben",
            Noun = "Aufgaben",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(t => EF.Functions.ILike(t.Name, Like.Contains(text))),
            Filters =
            [
                new FilterSpec<SyncTask>("ziel", "Ziel", ListBuilder.EnumOptions<CrmEntity>(Labels.Of), (q, v) => q.Where(t => t.Target == Enum.Parse<CrmEntity>(v))),
                new FilterSpec<SyncTask>("stand", "Letzter Lauf", [new("ok", "Erfolgreich"), new("fehler", "Fehlgeschlagen"), new("nie", "Noch nie")],
                    (q, v) => v switch { "ok" => q.Where(t => t.LastRunOk == true), "fehler" => q.Where(t => t.LastRunOk == false), _ => q.Where(t => t.LastRunDate == null) }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<SyncTask>, bool, IOrderedQueryable<SyncTask>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name)),
                ["run"] = ("Letzter Lauf", (q, d) => d ? q.OrderByDescending(t => t.LastRunDate) : q.OrderBy(t => t.LastRunDate)),
            },
            DefaultSort = "name",
        };
        var result = await ListBuilder.RunAsync(SyncLogic.Tasks(biz), Request, spec);
        var table = TableModel.Of("task-table", "Sync-Aufgaben", result.Items,
        [
            (new TableColumn("Aufgabe"), t => Ui.NameWithSub(t.Name, $"/Admin/Sync/Tasks/Edit?id={t.Id}", t.DataConnection?.Name)),
            (new TableColumn("Ziel"), t => Ui.Text(Labels.Of(t.Target))),
            (new TableColumn("Takt", HideOnPhone: true), t => Ui.Text(!t.IsEnabled ? "aus" : t.IntervalMinutes == 0 ? "nur von Hand" : $"alle {t.IntervalMinutes} Min.")),
            (new TableColumn("Letzter Lauf"), t => t.LastRunDate is null ? Ui.Pill("noch nie", "plain")
                : Ui.Concat(t.LastRunOk == true ? Ui.Pill(Fmt.Moment(t.LastRunDate), "ok", "check") : Ui.Pill(Fmt.Moment(t.LastRunDate), "error", "x-circle"))),
        ],
        t => [new RowAction("edit", "Bearbeiten", $"/Admin/Sync/Tasks/Edit?id={t.Id}")]) with { EmptyTitle = "Noch keine Sync-Aufgaben", EmptyText = "Lege zuerst eine Verbindung an, dann eine Aufgabe.", EmptyIcon = "refresh" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Sync/Tasks/Edit", "Neue Aufgabe");
    }
}
