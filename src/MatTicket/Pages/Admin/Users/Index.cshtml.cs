using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using MatTicket.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Admin.Users;

/// <summary>Everybody who can sign in: administrators and service staff.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Benutzer", "Wer sich anmelden kann, was er darf und in welchen Teams er ist.", ("Einstellungen", null), ("Benutzer", null));

        var spec = new ListSpec<User>
        {
            Id = "users",
            Action = "/Admin/Users",
            Label = "Benutzer",
            Noun = "Benutzer",
            SearchPlaceholder = "Name oder E-Mail-Adresse ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(u => EF.Functions.ILike(u.DisplayName, like) || EF.Functions.ILike(u.Email, like));
            },
            Filters =
            [
                new FilterSpec<User>("rolle", "Rolle", ListBuilder.EnumOptions<UserRole>(Labels.Of), (q, v) => q.Where(u => u.Role == Enum.Parse<UserRole>(v))),
                new FilterSpec<User>("quelle", "Anmeldung", ListBuilder.EnumOptions<AuthSource>(Labels.Of), (q, v) => q.Where(u => u.Source == Enum.Parse<AuthSource>(v))),
                new FilterSpec<User>("status", "Status", [new("aktiv", "Aktiv"), new("gesperrt", "Gesperrt")], (q, v) => v == "gesperrt" ? q.Where(u => !u.IsActive) : q.Where(u => u.IsActive)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<User>, bool, IOrderedQueryable<User>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(u => u.DisplayName) : q.OrderBy(u => u.DisplayName)),
                ["email"] = ("E-Mail", (q, d) => d ? q.OrderByDescending(u => u.Email) : q.OrderBy(u => u.Email)),
                ["role"] = ("Rolle", (q, d) => d ? q.OrderByDescending(u => u.Role).ThenBy(u => u.DisplayName) : q.OrderBy(u => u.Role).ThenBy(u => u.DisplayName)),
                ["login"] = ("Letzte Anmeldung", (q, d) => d ? q.OrderByDescending(u => u.LastLoginDate) : q.OrderBy(u => u.LastLoginDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(UserLogic.Query(biz), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var me = biz.User.UserId;
        Table = TableModel.Of("user-table", "Benutzer", result.Items,
        [
            (new TableColumn("Name"), u => Ui.NameWithSub(u.DisplayName, $"/Admin/Users/Edit?id={u.Id}", u.Id == me ? "das bist du" : u.Email)),
            (new TableColumn("Rolle"), u => Ui.Text(Labels.Of(u.Role))),
            (new TableColumn("Teams", HideOnPhone: true), u => u.Teams.Count == 0 ? Ui.None() : Ui.Text(string.Join(", ", u.Teams.Where(t => t.Team is not null).Select(t => t.Team!.Name)))),
            (new TableColumn("Anmeldung", HideOnPhone: true), u => Ui.Text(Labels.Of(u.Source))),
            (new TableColumn("Letzte Anmeldung", HideOnPhone: true), u => Ui.Text(u.LastLoginDate is null ? "noch nie" : Fmt.Moment(u.LastLoginDate))),
            (new TableColumn("Status"), u => u.IsActive ? Ui.Pill("aktiv", "ok") : Ui.Pill("gesperrt", "error")),
        ],
        u => [new RowAction("edit", "Bearbeiten", $"/Admin/Users/Edit?id={u.Id}")]);
        Table = Table with { EmptyIcon = "users" };
    }
}
