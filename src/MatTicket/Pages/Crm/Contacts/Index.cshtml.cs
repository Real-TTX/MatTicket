using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Crm.Contacts;

/// <summary>The contacts (people at customers). The columns are the ones the administrator chose.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Kontakte", "Ansprechpartner bei Kunden.", ("Kunden", null), ("Kontakte", null));
        var fields = await CrmLogic.FieldsAsync(biz, CrmEntity.Contact);
        var layout = CrmLogic.Layout(biz, CrmEntity.Contact);
        var companies = await CrmLogic.CompanyChoicesAsync(biz);
        var spec = new ListSpec<Contact>
        {
            Id = "contacts",
            Action = "/Crm/Contacts",
            Label = "Kontakte",
            Noun = "Kontakte",
            SearchPlaceholder = "Name, E-Mail, Firma ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(c => EF.Functions.ILike(c.LastName, like) || EF.Functions.ILike(c.FirstName, like) || EF.Functions.ILike(c.Email ?? "", like)
                    || EF.Functions.ILike(c.Phone ?? "", like) || (c.Company != null && EF.Functions.ILike(c.Company.Name, like)));
            },
            Filters =
            [
                new FilterSpec<Contact>("firma", "Firma", [new("ohne", "Ohne Firma"), .. companies.Take(200).Select(c => new FilterOption(c.Id.ToString(), c.Name))],
                    (q, v) => v == "ohne" ? q.Where(c => c.CompanyId == null) : q.Where(c => c.CompanyId == long.Parse(v))),
                .. CrmPage.SelectFilters<Contact>(fields, c => c.ExtraJson),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Contact>, bool, IOrderedQueryable<Contact>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(c => c.LastName).ThenByDescending(c => c.FirstName) : q.OrderBy(c => c.LastName).ThenBy(c => c.FirstName)),
                ["company"] = ("Firma", (q, d) => d ? q.OrderByDescending(c => c.Company!.Name) : q.OrderBy(c => c.Company!.Name)),
                ["changed"] = ("Zuletzt geändert", (q, d) => d ? q.OrderByDescending(c => c.UpdateDate) : q.OrderBy(c => c.UpdateDate)),
            },
            DefaultSort = "name",
            PageSize = 25,
        };
        var result = await ListBuilder.RunAsync(CrmLogic.Contacts(biz), Request, spec);
        var table = TableModel.Of("contact-table", "Kontakte", result.Items,
            CrmPage.Columns<Contact>(fields, layout, (c, k) => k == "LastName" ? (string.IsNullOrWhiteSpace(c.FullName) ? null : c.FullName) : CrmLogic.ValueOf(c, k), c => $"/Crm/Contacts/Edit?id={c.Id}", "LastName"),
            c => [new RowAction("edit", "Öffnen", $"/Crm/Contacts/Edit?id={c.Id}")]) with { EmptyTitle = "Keine Kontakte", EmptyText = "Lege Kontakte an oder importiere sie mit einer Sync-Aufgabe.", EmptyIcon = "users" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Crm/Contacts/Edit", "Neuer Kontakt", "user-plus");
    }
}
