using MatTicket.Logic;

namespace MatTicket.Pages.Tickets;

/// <summary>Every ticket the person may see.</summary>
public sealed class IndexModel(Biz biz) : TicketListModel(biz)
{
    protected override string Path => "/Tickets";

    protected override bool OnlyMine => false;

    public override void OnPageHandlerExecuting(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context)
        => Head("Tickets", "Alle Tickets deiner Abteilungen und Teams.", ("Tickets", null));
}
