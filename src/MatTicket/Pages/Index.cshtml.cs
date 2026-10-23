using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages;

/// <summary>The start page: what is open, what is mine, what came in during the last two weeks, and where it waits.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public TicketLogic.Overview Overview { get; private set; } = null!;

    public List<Ticket> Mine { get; private set; } = [];

    public List<Ticket> Newest { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Head("Übersicht", $"Guten Tag, {biz.User.Name}.", ("Übersicht", null));
        Overview = await TicketLogic.OverviewAsync(biz);
        var visible = await TicketLogic.VisibleAsync(biz);
        var me = biz.UserId;
        Mine = await visible.Where(t => t.AssigneeUserId == me && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .OrderByDescending(t => t.Priority == TicketPriority.Urgent).ThenByDescending(t => t.Priority == TicketPriority.High).ThenByDescending(t => t.LastActivityDate).Take(8).ToListAsync();
        Newest = await visible.Where(t => t.Status == TicketStatus.New).Include(t => t.Department).OrderByDescending(t => t.CreateDate).Take(8).ToListAsync();
    }
}
