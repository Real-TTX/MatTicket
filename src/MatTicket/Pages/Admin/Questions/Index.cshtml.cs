using MatTicket.Core.Forms;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Questions;

/// <summary>The catalogue of questions: built once, inserted into many forms in the designer.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Fragenkatalog", "Fragen, die in vielen Formularen vorkommen. Im Designer fügst du sie mit einem Klick ein.", ("Einstellungen", null), ("Fragenkatalog", null));
        var types = BlockTypes.All.Where(t => BlockTypes.IsQuestion(t.Type)).Select(t => new FilterOption(t.Type, t.Label)).ToList();
        var spec = new ListSpec<Question>
        {
            Id = "questions",
            Action = "/Admin/Questions",
            Label = "Fragen",
            Noun = "Fragen",
            SearchPlaceholder = "Frage oder Schlüssel ...",
            Search = (q, text) => q.Where(x => EF.Functions.ILike(x.Label, Like.Contains(text)) || EF.Functions.ILike(x.Key, Like.Contains(text))),
            Filters = [new FilterSpec<Question>("art", "Art", types, (q, v) => q.Where(x => x.Type == v))],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Question>, bool, IOrderedQueryable<Question>>)>
            {
                ["label"] = ("Frage", (q, d) => d ? q.OrderByDescending(x => x.Label) : q.OrderBy(x => x.Label)),
                ["key"] = ("Schlüssel", (q, d) => d ? q.OrderByDescending(x => x.Key) : q.OrderBy(x => x.Key)),
            },
            DefaultSort = "label",
        };
        var result = await ListBuilder.RunAsync(CategoryLogic.Questions(biz), Request, spec);
        var table = TableModel.Of("question-table", "Fragen", result.Items,
        [
            (new TableColumn("Frage"), q => Ui.NameWithSub(q.Label, $"/Admin/Questions/Edit?id={q.Id}", q.Help)),
            (new TableColumn("Schlüssel", Mono: true), q => Ui.Text(q.Key)),
            (new TableColumn("Art"), q => Ui.Text(BlockTypes.Label(q.Type))),
            (new TableColumn("Kategorie", HideOnPhone: true), q => q.Category is null ? Ui.None() : Ui.Text(q.Category.Name)),
            (new TableColumn("Pflicht", HideOnPhone: true), q => q.Required ? Ui.Pill("Pflicht", "info") : Ui.None()),
        ],
        q => [new RowAction("edit", "Bearbeiten", $"/Admin/Questions/Edit?id={q.Id}")]) with { EmptyTitle = "Noch keine Fragen", EmptyText = "Lege Fragen an, die du in mehreren Formularen brauchst.", EmptyIcon = "list-checks" };
        List = new ListPageModel(result.Toolbar, table, result.Pager, "/Admin/Questions/Edit", "Neue Frage");
    }
}
