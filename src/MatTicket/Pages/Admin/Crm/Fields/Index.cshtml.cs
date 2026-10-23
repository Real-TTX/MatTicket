using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Crm.Fields;

/// <summary>The fields the administrator adds to companies and contacts.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Felder", "Eigene Felder für Firmen und Kontakte. Sie erscheinen auf den Seiten der Datensätze, in Listen und in Vorlagen von Knöpfen ({x.schlüssel}).", ("Einstellungen", null), ("Felder", null));
        var spec = new ListSpec<CustomField>
        {
            Id = "fields",
            Action = "/Admin/Crm/Fields",
            Label = "Felder",
            Noun = "Felder",
            SearchPlaceholder = "Beschriftung oder Schlüssel ...",
            Search = (q, text) => q.Where(f => EF.Functions.ILike(f.Label, Like.Contains(text)) || EF.Functions.ILike(f.Key, Like.Contains(text))),
            Filters =
            [
                new FilterSpec<CustomField>("fuer", "Für", ListBuilder.EnumOptions<CrmEntity>(Labels.Of), (q, v) => q.Where(f => f.Entity == Enum.Parse<CrmEntity>(v))),
                new FilterSpec<CustomField>("art", "Art", ListBuilder.EnumOptions<CustomFieldType>(Labels.Of), (q, v) => q.Where(f => f.Type == Enum.Parse<CustomFieldType>(v))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<CustomField>, bool, IOrderedQueryable<CustomField>>)>
            {
                ["order"] = ("Reihenfolge", (q, d) => q.OrderBy(f => f.Entity).ThenBy(f => f.SortOrder).ThenBy(f => f.Label)),
                ["label"] = ("Beschriftung", (q, d) => d ? q.OrderByDescending(f => f.Label) : q.OrderBy(f => f.Label)),
            },
            DefaultSort = "order",
        };
        var result = await ListBuilder.RunAsync(CrmLogic.CustomFields(biz), Request, spec);
        var table = TableModel.Of("field-table", "Felder", result.Items,
        [
            (new TableColumn("Feld"), f => Ui.NameWithSub(f.Label, $"/Admin/Crm/Fields/Edit?id={f.Id}", f.Hint)),
            (new TableColumn("Für"), f => Ui.Text(Labels.Of(f.Entity))),
            (new TableColumn("Schlüssel", Mono: true, HideOnPhone: true), f => Ui.Text("x." + f.Key)),
            (new TableColumn("Art"), f => Ui.Text(Labels.Of(f.Type))),
            (new TableColumn("Reiter", HideOnPhone: true), f => Ui.Text(f.Section ?? "Zusatzfelder")),
        ],
        f => [new RowAction("edit", "Bearbeiten", $"/Admin/Crm/Fields/Edit?id={f.Id}")]) with { EmptyTitle = "Noch keine eigenen Felder", EmptyText = "Zum Beispiel Branche, Vertragsart oder Gerätenummer.", EmptyIcon = "columns" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Crm/Fields/Edit", "Neues Feld");
    }
}
