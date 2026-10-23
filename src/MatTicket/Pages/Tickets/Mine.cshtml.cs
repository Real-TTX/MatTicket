using MatTicket.Logic;

namespace MatTicket.Pages.Tickets;

/// <summary>The tickets assigned to the person.</summary>
public sealed class MineModel(Biz biz) : TicketListModel(biz)
{
    protected override string Path => "/Tickets/Mine";

    protected override bool OnlyMine => true;

    public override void OnPageHandlerExecuting(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context)
        => Head("Meine Tickets", "Was dir zugewiesen ist.", ("Tickets", "/Tickets"), ("Meine Tickets", null));
}
