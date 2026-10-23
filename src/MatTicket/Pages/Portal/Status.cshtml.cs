using MatTicket.Logic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Portal;

/// <summary>
/// The status page of a ticket for the customer (/t/{token}): the number, the state, the answers (never internal notes), a reply and
/// "Problem gelöst". The secret link is the only key; it is never listed anywhere.
/// </summary>
[AllowAnonymous]
public sealed class StatusModel(Biz biz) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public Guid Token { get; set; }

    [BindProperty]
    public string? Text { get; set; }

    public PortalLogic.CustomerView View { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await PortalLogic.CustomerReplyAsync(biz, Token, Text ?? "");
            Flash("Ihre Nachricht ist angekommen.");
            return RedirectToPage(new { token = Token.ToString("N") });
        }
        catch (BizException ex)
        {
            ModelState.AddModelError(nameof(Text), ex.Message);
            return await LoadAsync() ? Page() : NotFound();
        }
    }

    public async Task<IActionResult> OnPostResolveAsync()
    {
        await PortalLogic.CustomerResolveAsync(biz, Token);
        Flash("Danke! Das Ticket ist als gelöst markiert.");
        return RedirectToPage(new { token = Token.ToString("N") });
    }

    private async Task<bool> LoadAsync()
    {
        var view = await PortalLogic.CustomerViewAsync(biz.Db, Token);
        if (view is null)
        {
            return false;
        }

        View = view;
        ViewData["Title"] = $"Ticket {view.Ticket.Number}";
        return true;
    }
}
