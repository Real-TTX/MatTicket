using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Sync.Connections;

/// <summary>The databases the sync tasks read from.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Datenverbindungen", "Datenbanken, aus denen Sync-Aufgaben Kunden lesen: SQL Server (z. B. Navision), PostgreSQL, MySQL.", ("Einstellungen", null), ("Verbindungen", null));
        var spec = new ListSpec<DataConnection>
        {
            Id = "connections",
            Action = "/Admin/Sync/Connections",
            Label = "Verbindungen",
            Noun = "Verbindungen",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(c => EF.Functions.ILike(c.Name, Like.Contains(text))),
            Filters = [new FilterSpec<DataConnection>("art", "Datenbank", ListBuilder.EnumOptions<ConnectionKind>(Labels.Of), (q, v) => q.Where(c => c.Kind == Enum.Parse<ConnectionKind>(v)))],
            Sorts = new Dictionary<string, (string, Func<IQueryable<DataConnection>, bool, IOrderedQueryable<DataConnection>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
            },
            DefaultSort = "name",
        };
        var result = await ListBuilder.RunAsync(SyncLogic.Connections(biz), Request, spec);
        var table = TableModel.Of("connection-table", "Verbindungen", result.Items,
        [
            (new TableColumn("Name"), c => Ui.NameWithSub(c.Name, $"/Admin/Sync/Connections/Edit?id={c.Id}", c.Note)),
            (new TableColumn("Datenbank"), c => Ui.Text(Labels.Of(c.Kind))),
            (new TableColumn("Letzter Test", HideOnPhone: true), c => c.LastTestOk is null ? Ui.Pill("nicht getestet", "plain")
                : c.LastTestOk.Value ? Ui.Pill("verbunden", "ok", "check") : Ui.Pill("Fehler", "error", "x-circle")),
        ],
        c => [new RowAction("edit", "Bearbeiten", $"/Admin/Sync/Connections/Edit?id={c.Id}")]) with { EmptyTitle = "Noch keine Verbindungen", EmptyText = "Lege eine Verbindung zur Datenbank deines ERP oder CRM an.", EmptyIcon = "database" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Sync/Connections/Edit", "Neue Verbindung");
    }
}
