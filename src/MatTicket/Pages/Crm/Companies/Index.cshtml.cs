using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Crm.Companies;

/// <summary>The companies (customers). The columns are the ones the administrator chose under Einstellungen → Ansicht.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Firmen", "Kunden und Partner – aus der Sync-Aufgabe oder von Hand gepflegt.", ("Kunden", null), ("Firmen", null));
        var fields = await CrmLogic.FieldsAsync(biz, CrmEntity.Company);
        var layout = CrmLogic.Layout(biz, CrmEntity.Company);
        var spec = new ListSpec<Company>
        {
            Id = "companies",
            Action = "/Crm/Companies",
            Label = "Firmen",
            Noun = "Firmen",
            SearchPlaceholder = "Name, Kundennummer, Ort ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.CustomerNumber ?? "", like) || EF.Functions.ILike(c.City ?? "", like) || EF.Functions.ILike(c.Email ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Company>("herkunft", "Herkunft", [new("sync", "Aus einer Sync-Aufgabe"), new("hand", "Von Hand")], (q, v) => v == "sync" ? q.Where(c => c.SyncTaskId != null) : q.Where(c => c.SyncTaskId == null)),
                .. CrmPage.SelectFilters<Company>(fields, c => c.ExtraJson),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Company>, bool, IOrderedQueryable<Company>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
                ["number"] = ("Kundennummer", (q, d) => d ? q.OrderByDescending(c => c.CustomerNumber) : q.OrderBy(c => c.CustomerNumber)),
                ["city"] = ("Ort", (q, d) => d ? q.OrderByDescending(c => c.City) : q.OrderBy(c => c.City)),
                ["changed"] = ("Zuletzt geändert", (q, d) => d ? q.OrderByDescending(c => c.UpdateDate) : q.OrderBy(c => c.UpdateDate)),
            },
            DefaultSort = "name",
            PageSize = 25,
        };
        var result = await ListBuilder.RunAsync(CrmLogic.Companies(biz), Request, spec);
        var table = TableModel.Of("company-table", "Firmen", result.Items,
            CrmPage.Columns<Company>(fields, layout, CrmLogic.ValueOf, c => $"/Crm/Companies/Edit?id={c.Id}", "Name"),
            c => [new RowAction("edit", "Öffnen", $"/Crm/Companies/Edit?id={c.Id}")]) with { EmptyTitle = "Keine Firmen", EmptyText = "Lege Firmen an oder importiere sie mit einer Sync-Aufgabe.", EmptyIcon = "building" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Crm/Companies/Edit", "Neue Firma");
    }
}
