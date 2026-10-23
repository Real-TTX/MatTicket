using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Categories;

/// <summary>Categories and subcategories: what a ticket is about, and where it goes by default.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Kategorien", "Worum es in einem Ticket geht – und wohin es deshalb geht.", ("Einstellungen", null), ("Kategorien", null));
        var departments = await OrganizationLogic.DepartmentChoicesAsync(biz);
        var spec = new ListSpec<Category>
        {
            Id = "categories",
            Action = "/Admin/Categories",
            Label = "Kategorien",
            Noun = "Kategorien",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(c => EF.Functions.ILike(c.Name, Like.Contains(text)) || (c.Parent != null && EF.Functions.ILike(c.Parent.Name, Like.Contains(text)))),
            Filters =
            [
                new FilterSpec<Category>("abteilung", "Abteilung", [.. departments.Select(d => new FilterOption(d.Id.ToString(), d.Name))], (q, v) => q.Where(c => c.DepartmentId == long.Parse(v))),
                new FilterSpec<Category>("ebene", "Ebene", [new("haupt", "Hauptkategorien"), new("unter", "Unterkategorien")], (q, v) => v == "haupt" ? q.Where(c => c.ParentId == null) : q.Where(c => c.ParentId != null)),
                new FilterSpec<Category>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Ausgeblendet")], (q, v) => q.Where(c => c.IsActive == (v == "aktiv"))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Category>, bool, IOrderedQueryable<Category>>)>
            {
                ["tree"] = ("Baum", (q, d) => q.OrderBy(c => c.Parent != null ? c.Parent.SortOrder : c.SortOrder).ThenBy(c => c.Parent != null ? c.Parent.Name : c.Name).ThenBy(c => c.ParentId != null).ThenBy(c => c.SortOrder).ThenBy(c => c.Name)),
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
            },
            DefaultSort = "tree",
            PageSize = 50,
        };
        var result = await ListBuilder.RunAsync(CategoryLogic.Query(biz), Request, spec);
        var table = TableModel.Of("category-table", "Kategorien", result.Items,
        [
            (new TableColumn("Kategorie"), c => Ui.NameWithSub(c.Parent is null ? c.Name : $"{c.Parent.Name} › {c.Name}", $"/Admin/Categories/Edit?id={c.Id}", c.Description)),
            (new TableColumn("Abteilung / Team", HideOnPhone: true), c => c.Department is null ? Ui.None() : Ui.NameWithSub(c.Department.Name, null, c.Team?.Name)),
            (new TableColumn("Priorität", HideOnPhone: true), c => Ui.Text(Labels.Of(c.DefaultPriority))),
            (new TableColumn("Status"), c => c.IsActive ? Ui.Pill("aktiv", "ok") : Ui.Pill("ausgeblendet", "plain")),
        ],
        c => [new RowAction("edit", "Bearbeiten", $"/Admin/Categories/Edit?id={c.Id}")]) with { EmptyTitle = "Noch keine Kategorien", EmptyText = "Lege Kategorien an, damit Tickets automatisch richtig landen.", EmptyIcon = "tag" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Categories/Edit", "Neue Kategorie");
    }
}
