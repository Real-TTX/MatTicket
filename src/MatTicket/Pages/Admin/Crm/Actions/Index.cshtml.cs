using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Crm.Actions;

/// <summary>The buttons on the pages of companies and contacts: to Navision, to the main CRM, to Google Maps, a call, a mail.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Knöpfe", "Knöpfe auf den Seiten von Firmen und Kontakten, die in andere Systeme führen.", ("Einstellungen", null), ("Knöpfe", null));
        var spec = new ListSpec<ViewAction>
        {
            Id = "actions",
            Action = "/Admin/Crm/Actions",
            Label = "Knöpfe",
            Noun = "Knöpfe",
            SearchPlaceholder = "Beschriftung oder Vorlage ...",
            Search = (q, text) => q.Where(a => EF.Functions.ILike(a.Label, Like.Contains(text)) || EF.Functions.ILike(a.Template, Like.Contains(text))),
            Filters =
            [
                new FilterSpec<ViewAction>("fuer", "Für", ListBuilder.EnumOptions<CrmEntity>(Labels.Of), (q, v) => q.Where(a => a.Entity == Enum.Parse<CrmEntity>(v))),
                new FilterSpec<ViewAction>("art", "Art", ListBuilder.EnumOptions<ViewActionKind>(Labels.Of), (q, v) => q.Where(a => a.Kind == Enum.Parse<ViewActionKind>(v))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ViewAction>, bool, IOrderedQueryable<ViewAction>>)>
            {
                ["order"] = ("Reihenfolge", (q, d) => q.OrderBy(a => a.Entity).ThenBy(a => a.SortOrder).ThenBy(a => a.Label)),
                ["label"] = ("Beschriftung", (q, d) => d ? q.OrderByDescending(a => a.Label) : q.OrderBy(a => a.Label)),
            },
            DefaultSort = "order",
        };
        var result = await ListBuilder.RunAsync(CrmLogic.ViewActions(biz), Request, spec);
        var table = TableModel.Of("action-table", "Knöpfe", result.Items,
        [
            (new TableColumn("Knopf"), a => Ui.Concat(Ui.Icon(a.Icon), Ui.Link(a.Label, $"/Admin/Crm/Actions/Edit?id={a.Id}"))),
            (new TableColumn("Für"), a => Ui.Text(Labels.Of(a.Entity))),
            (new TableColumn("Art", HideOnPhone: true), a => Ui.Text(Labels.Of(a.Kind))),
            (new TableColumn("Vorlage", Mono: true, HideOnPhone: true), a => Ui.Text(a.Template)),
            (new TableColumn("Status"), a => a.IsActive ? Ui.Pill("aktiv", "ok") : Ui.Pill("aus", "plain")),
        ],
        a => [new RowAction("edit", "Bearbeiten", $"/Admin/Crm/Actions/Edit?id={a.Id}")]) with { EmptyTitle = "Noch keine Knöpfe", EmptyText = "Zum Beispiel „In Navision öffnen“ oder „Route planen“.", EmptyIcon = "external" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Crm/Actions/Edit", "Neuer Knopf");
    }
}
