using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Forms;

/// <summary>The forms (assistants) of the portal: drafts and what is published at which address.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Formulare", "Assistenten und Formulare für Kunden. Gebaut im Designer, veröffentlicht unter /adresse.", ("Einstellungen", null), ("Formulare", null));
        var spec = new ListSpec<TicketForm>
        {
            Id = "forms",
            Action = "/Admin/Forms",
            Label = "Formulare",
            Noun = "Formulare",
            SearchPlaceholder = "Name oder Adresse ...",
            Search = (q, text) => q.Where(f => EF.Functions.ILike(f.Name, Like.Contains(text)) || EF.Functions.ILike(f.Slug, Like.Contains(text))),
            Filters = [new FilterSpec<TicketForm>("stand", "Stand", ListBuilder.EnumOptions<FormState>(Labels.Of), (q, v) => q.Where(f => f.State == Enum.Parse<FormState>(v)))],
            Sorts = new Dictionary<string, (string, Func<IQueryable<TicketForm>, bool, IOrderedQueryable<TicketForm>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(f => f.Name) : q.OrderBy(f => f.Name)),
                ["changed"] = ("Zuletzt geändert", (q, d) => d ? q.OrderByDescending(f => f.UpdateDate) : q.OrderBy(f => f.UpdateDate)),
            },
            DefaultSort = "name",
        };
        var result = await ListBuilder.RunAsync(FormLogic.Query(biz), Request, spec);
        var table = TableModel.Of("form-table", "Formulare", result.Items,
        [
            (new TableColumn("Formular"), f => Ui.NameWithSub(f.Name, $"/Admin/Forms/Edit?id={f.Id}", f.Description)),
            (new TableColumn("Adresse", Mono: true), f => f.State == FormState.Published ? Ui.Link("/" + f.Slug, "/" + f.Slug) : Ui.Text("/" + f.Slug)),
            (new TableColumn("Stand"), f => Labels.Pill(f.State)),
            (new TableColumn("Version", HideOnPhone: true), f => f.PublishedVersion == 0 ? Ui.None() : Ui.Text($"{f.PublishedVersion} · {Fmt.Date(f.PublishedDate)}")),
            (new TableColumn("Abteilung", HideOnPhone: true), f => f.Department is null ? Ui.None() : Ui.Text(f.Department.Name)),
        ],
        f => [new RowAction("workflow", "Designer", $"/Admin/Forms/Designer?id={f.Id}"), new RowAction("edit", "Einstellungen", $"/Admin/Forms/Edit?id={f.Id}")])
            with { EmptyTitle = "Noch keine Formulare", EmptyText = "Lege ein Formular an; es beginnt mit einem kleinen Ablauf, den du im Designer ausbaust.", EmptyIcon = "template" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Forms/Edit", "Neues Formular");
    }
}
