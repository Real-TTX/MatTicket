using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Authorization;

namespace MatTicket.Pages.Portal;

/// <summary>The help portal (/hilfe): the published forms, for customers.</summary>
[AllowAnonymous]
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public List<TicketForm> Forms { get; private set; } = [];

    public SiteSettings Site { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Site = SettingLogic.Get<SiteSettings>(biz, SettingStore.Site);
        Forms = await PortalLogic.PublishedAsync(biz.Db, onlyIndexable: false);
        ViewData["Title"] = Site.ServiceName;
        ViewData["Robots"] = "index, follow";
    }
}
