using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Departments;

/// <summary>The departments, each with its teams.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Abteilungen", "Die erste Ebene, zu der ein Ticket gehört.", ("Einstellungen", null), ("Abteilungen", null));
        var spec = new ListSpec<Department>
        {
            Id = "departments",
            Action = "/Admin/Departments",
            Label = "Abteilungen",
            Noun = "Abteilungen",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(d => EF.Functions.ILike(d.Name, Like.Contains(text)) || EF.Functions.ILike(d.Description ?? "", Like.Contains(text))),
            Sorts = new Dictionary<string, (string, Func<IQueryable<Department>, bool, IOrderedQueryable<Department>>)>
            {
                ["order"] = ("Reihenfolge", (q, d) => d ? q.OrderByDescending(x => x.SortOrder).ThenBy(x => x.Name) : q.OrderBy(x => x.SortOrder).ThenBy(x => x.Name)),
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(x => x.Name) : q.OrderBy(x => x.Name)),
            },
            DefaultSort = "order",
        };
        var result = await ListBuilder.RunAsync(OrganizationLogic.Departments(biz), Request, spec);
        var table = TableModel.Of("department-table", "Abteilungen", result.Items,
        [
            (new TableColumn("Name"), d => new HtmlString($"<span class=\"mt-person\"><span class=\"mt-swatch\" data-swatch=\"{d.Colour}\" aria-hidden=\"true\"></span>{Ui.NameWithSub(d.Name, $"/Admin/Departments/Edit?id={d.Id}", d.Description).ToHtml()}</span>")),
            (new TableColumn("Teams"), d => d.Teams.Count == 0 ? Ui.None() : Ui.Text(string.Join(", ", d.Teams.OrderBy(t => t.Name).Select(t => t.Name)))),
            (new TableColumn("E-Mail", HideOnPhone: true), d => d.Email is null ? Ui.None() : Ui.Text(d.Email)),
        ],
        d => [new RowAction("edit", "Bearbeiten", $"/Admin/Departments/Edit?id={d.Id}")]);
        table = table with { EmptyTitle = "Noch keine Abteilungen", EmptyText = "Lege die erste an, oder erzeuge Beispieldaten unter System.", EmptyIcon = "briefcase" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Departments/Edit", "Neue Abteilung");
    }
}
