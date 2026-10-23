using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Teams;

/// <summary>The teams of all departments with their members.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Teams", "Wer in welchem Team arbeitet. Ein Ticket kann an ein Team geschoben werden.", ("Einstellungen", null), ("Teams", null));
        var departments = await OrganizationLogic.DepartmentChoicesAsync(biz);
        var spec = new ListSpec<Team>
        {
            Id = "teams",
            Action = "/Admin/Teams",
            Label = "Teams",
            Noun = "Teams",
            SearchPlaceholder = "Team oder Mitglied ...",
            Search = (q, text) => q.Where(t => EF.Functions.ILike(t.Name, Like.Contains(text)) || t.Members.Any(m => EF.Functions.ILike(m.User!.DisplayName, Like.Contains(text)))),
            Filters = [new FilterSpec<Team>("abteilung", "Abteilung", [.. departments.Select(d => new FilterOption(d.Id.ToString(), d.Name))], (q, v) => q.Where(t => t.DepartmentId == long.Parse(v)))],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Team>, bool, IOrderedQueryable<Team>>)>
            {
                ["department"] = ("Abteilung", (q, d) => d ? q.OrderByDescending(t => t.Department!.Name).ThenBy(t => t.Name) : q.OrderBy(t => t.Department!.Name).ThenBy(t => t.Name)),
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name)),
            },
            DefaultSort = "department",
        };
        var result = await ListBuilder.RunAsync(OrganizationLogic.Teams(biz), Request, spec);
        var table = TableModel.Of("team-table", "Teams", result.Items,
        [
            (new TableColumn("Team"), t => Ui.NameWithSub(t.Name, $"/Admin/Teams/Edit?id={t.Id}", t.Description)),
            (new TableColumn("Abteilung"), t => Ui.Text(t.Department?.Name)),
            (new TableColumn("Mitglieder", HideOnPhone: true), t => t.Members.Count == 0 ? Ui.None()
                : Ui.Text(string.Join(", ", t.Members.Where(m => m.User is not null).OrderByDescending(m => m.IsLead).Select(m => m.User!.DisplayName + (m.IsLead ? " (Leitung)" : ""))))),
        ],
        t => [new RowAction("edit", "Bearbeiten", $"/Admin/Teams/Edit?id={t.Id}")]) with { EmptyTitle = "Noch keine Teams", EmptyText = "Teams gehören zu einer Abteilung.", EmptyIcon = "users" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Teams/Edit", "Neues Team");
    }
}
