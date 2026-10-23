using System.Net;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.Extensions.Caching.Memory;

namespace MatTicket.UiKit;

/// <summary>An entry of the user's context menu. A <see cref="Post"/> item changes something (sign out): a button of a small form, not a link.</summary>
public sealed record UserMenuItem(string Title, string Href, string Icon, bool Danger = false, bool SeparatorBefore = false, bool Post = false);

/// <summary>Everything the layout needs to draw the shell around a page: the menus, the user, the version.</summary>
public sealed class ShellModel
{
    public IReadOnlyList<NavSet> Menus { get; init; } = [];

    public bool HasSwitcher => Menus.Count > 1;

    public NavSet CurrentMenu(string? active) => Menus.FirstOrDefault(m => m.Contains(active)) ?? Menus[0];

    public string UserName { get; init; } = "";

    public string UserRole { get; init; } = "";

    public string UserLine { get; init; } = "";

    public IReadOnlyList<UserMenuItem> UserMenu { get; init; } = [];

    public string Version { get; init; } = "";

    public string Tagline { get; init; } = "Service und Tickets";

    /// <summary>"light" or "dark" when the user chose one; null follows the system.</summary>
    public string? Theme { get; init; }

    public string HomeHref { get; init; } = "/";

    /// <summary>The optional attributes of &lt;html&gt;, written only when they have a value (an empty data-theme would count as a choice).</summary>
    public string RootAttributes => Theme is "light" or "dark" ? $"data-theme=\"{WebUtility.HtmlEncode(Theme)}\"" : string.Empty;
}

/// <summary>Counts shown as badges in the menu, per person. Cached for half a minute: a badge may be a little late, a page must not wait for queries.</summary>
public sealed class NavCounts(IMemoryCache cache, Biz biz)
{
    public async Task<TicketLogic.Badges> GetAsync()
        => (await cache.GetOrCreateAsync("nav-counts-" + biz.User.UserId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await TicketLogic.BadgesAsync(biz);
        }))!;
}

/// <summary>Builds the shell for the person who is signed in: the settings menu only for administrators.</summary>
public sealed class ShellFactory(ICurrentUser user, NavCounts counts, IHttpContextAccessor accessor)
{
    public async Task<ShellModel> BuildAsync()
    {
        var principal = accessor.HttpContext?.User;
        var theme = principal?.FindFirst(AppClaims.Theme)?.Value;

        var menus = new List<NavSet> { await WorkMenuAsync() };
        if (user.IsAdmin)
        {
            menus.Add(AdminMenu());
        }

        return new ShellModel
        {
            Menus = menus,
            UserName = user.Name ?? "Gast",
            UserRole = user.IsAdmin ? "Administrator" : "Service",
            UserLine = principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
            UserMenu = UserMenu(),
            Version = $"MatTicket {AppVersion.Current}",
            Theme = theme,
        };
    }

    /// <summary>The shell of a page without a menu (sign-in, setup).</summary>
    public static ShellModel Bare(string? theme = null) => new()
    {
        Menus = [new NavSet("none", "MatTicket", "home", [])],
        Version = $"MatTicket {AppVersion.Current}",
        Theme = theme,
    };

    private async Task<NavSet> WorkMenuAsync()
    {
        var c = await counts.GetAsync();
        return new NavSet("work", "MatTicket", "mt-logo",
        [
            new(null,
            [
                new("Neues Ticket", "/Tickets/Create", "plus", Primary: true),
                new("Übersicht", "/", "dashboard"),
            ]),
            new("Tickets",
            [
                new("Alle Tickets", "/Tickets", "ticket", c.New > 0 ? NavBadge.Count(c.New, "info", "neue Tickets") : null),
                new("Meine Tickets", "/Tickets/Mine", "user", c.Mine > 0 ? NavBadge.Count(c.Mine, "accent", "mir zugewiesen") : null),
            ]),
            new("Kunden",
            [
                new("Firmen", "/Crm/Companies", "building"),
                new("Kontakte", "/Crm/Contacts", "users"),
            ]),
            new("Portal",
            [
                new("Hilfe-Portal", "/hilfe", "globe"),
            ]),
        ]);
    }

    private static NavSet AdminMenu() => new("admin", "Einstellungen", "settings",
    [
        new("Service",
        [
            new("Formulare", "/Admin/Forms", "template"),
            new("Fragenkatalog", "/Admin/Questions", "list-checks"),
            new("Kategorien", "/Admin/Categories", "tag"),
        ]),
        new("Organisation",
        [
            new("Abteilungen", "/Admin/Departments", "briefcase"),
            new("Teams", "/Admin/Teams", "users"),
            new("Benutzer", "/Admin/Users", "user"),
        ]),
        new("Kunden",
        [
            new("Felder", "/Admin/Crm/Fields", "columns"),
            new("Knöpfe", "/Admin/Crm/Actions", "external"),
            new("Ansicht", "/Admin/Crm/Layout", "layers"),
        ]),
        new("Daten",
        [
            new("Verbindungen", "/Admin/Sync/Connections", "database"),
            new("Sync-Aufgaben", "/Admin/Sync/Tasks", "refresh"),
        ]),
        new("KI",
        [
            new("Profile", "/Admin/Ai/Profiles", "bot"),
            new("Anweisungen und Grenzen", "/Admin/Ai", "sliders"),
            new("Läufe", "/Admin/Ai/Runs", "activity"),
        ]),
        new("System",
        [
            new("Anmeldung", "/Admin/Entra", "shield"),
            new("E-Mail", "/Admin/Mail", "mail"),
            new("Portal", "/Admin/Portal", "globe"),
            new("System", "/Admin/System", "server"),
            new("Aktivität", "/Admin/Activity", "history"),
        ]),
    ]);

    private static List<UserMenuItem> UserMenu() =>
    [
        new("Mein Konto", "/Account/Profile", "user"),
        new("Sicherheit", "/Account/Security", "shield"),
        new("Abmelden", "/Account/Logout", "logout", Danger: true, SeparatorBefore: true, Post: true),
    ];
}
