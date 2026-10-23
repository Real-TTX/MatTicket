using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;

namespace MatTicket.Pages.Admin.Ai.Runs;

/// <summary>Every run of the AI: what was asked, through which profile, what it cost and how it ended.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ListPageModel List { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("KI-Läufe", "Jeder Lauf mit jedem Schritt. Änderungen lassen sich rückgängig machen, der neueste Lauf zuerst.", ("Einstellungen", null), ("KI-Läufe", null));
        var now = biz.Now;
        var spec = new ListSpec<AiRun>
        {
            Id = "runs",
            Action = "/Admin/Ai/Runs",
            Label = "KI-Läufe",
            Noun = "Läufe",
            Filters =
            [
                new FilterSpec<AiRun>("aktion", "Aktion", ListBuilder.EnumOptions<AiAction>(Labels.Of), (q, v) => q.Where(r => r.Action == Enum.Parse<AiAction>(v))),
                new FilterSpec<AiRun>("status", "Ende", ListBuilder.EnumOptions<AiRunStatus>(Labels.Of), (q, v) => q.Where(r => r.Status == Enum.Parse<AiRunStatus>(v))),
                new FilterSpec<AiRun>("zeit", "Zeitraum", [new("heute", "Heute"), new("woche", "Letzte 7 Tage")], (q, v) => q.Where(r => r.StartDate >= (v == "heute" ? now.Date : now.AddDays(-7)))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<AiRun>, bool, IOrderedQueryable<AiRun>>)>
            {
                ["date"] = ("Zeit", (q, d) => d ? q.OrderByDescending(r => r.Id) : q.OrderBy(r => r.Id)),
                ["tokens"] = ("Tokens", (q, d) => d ? q.OrderByDescending(r => r.PromptTokens + r.CompletionTokens) : q.OrderBy(r => r.PromptTokens + r.CompletionTokens)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
            PageSize = 30,
        };
        var result = await ListBuilder.RunAsync(AiLogic.Runs(biz), Request, spec);
        var table = TableModel.Of("run-table", "KI-Läufe", result.Items,
        [
            (new TableColumn("Lauf"), r => Ui.NameWithSub($"#{r.Id} · {Labels.Of(r.Action)}", $"/Admin/Ai/Runs/Details?id={r.Id}", Fmt.Moment(r.StartDate))),
            (new TableColumn("Ende"), r => Labels.Pill(r.Status)),
            (new TableColumn("Profil", HideOnPhone: true), r => string.IsNullOrEmpty(r.ProfileName) ? Ui.None() : Ui.NameWithSub(r.ProfileName, null, r.Model)),
            (new TableColumn("Tokens", Numeric: true, HideOnPhone: true), r => Ui.Text(Fmt.Integer(r.TotalTokens) + (r.TokensEstimated ? " ≈" : ""))),
            (new TableColumn("Für", HideOnPhone: true), r => r.TicketId is { } t ? Ui.Link("Ticket", $"/Tickets/Details?id={t}") : r.VisitorHash is not null ? Ui.Text("Besucher") : Ui.None()),
        ],
        r => [new RowAction("eye", "Ansehen", $"/Admin/Ai/Runs/Details?id={r.Id}")]) with { EmptyTitle = "Noch keine Läufe", EmptyText = "Hier erscheint jeder Aufruf der KI.", EmptyIcon = "activity" };
        List = new ListPageModel(result.Toolbar, table, result.Pager);
    }
}
