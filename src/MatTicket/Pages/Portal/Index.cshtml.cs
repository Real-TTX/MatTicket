using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Authorization;

namespace MatTicket.Pages.Portal;

/// <summary>The help portal (/hilfe): the forms the visitor may use (and the ones that need a sign-in), and the own requests of a signed-in person.</summary>
[AllowAnonymous]
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public List<(TicketForm Form, FormAccessResult Access)> Forms { get; private set; } = [];

    public List<Ticket> Mine { get; private set; } = [];

    public SiteSettings Site { get; private set; } = new();

    public Viewer Viewer { get; private set; } = Viewer.Guest;

    public async Task OnGetAsync()
    {
        Site = SettingLogic.Get<SiteSettings>(biz, SettingStore.Site);
        Viewer = await PortalLogic.ViewerAsync(biz);
        Forms = await PortalLogic.PortalFormsAsync(biz, Viewer);
        Mine = await PortalLogic.MyTicketsAsync(biz);
        ViewData["Title"] = Site.ServiceName;
        ViewData["Robots"] = "index, follow";
    }
}
