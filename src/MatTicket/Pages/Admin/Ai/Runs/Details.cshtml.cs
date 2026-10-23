using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Ai.Runs;

/// <summary>One run: every step (message, action, result, warning), what it changed, and "Rückgängig" or "Weitermachen".</summary>
public sealed class DetailsModel(Biz biz) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public AiLogic.RunDetails Details { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var details = await AiLogic.RunDetailsAsync(biz, Id);
        if (details is null)
        {
            return NotFound();
        }

        Details = details;
        Head($"Lauf #{Id}", UiKit.Labels.Of(details.Run.Action), ("Einstellungen", null), ("KI-Läufe", "/Admin/Ai/Runs"), ($"#{Id}", null));
        ViewData["ActiveHref"] = "/Admin/Ai/Runs";
        return Page();
    }

    public async Task<IActionResult> OnPostUndoAsync()
    {
        try
        {
            Flash(await AiLogic.UndoAsync(biz, Id));
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostContinueAsync()
    {
        try
        {
            Flash(await AiLogic.ContinueAsync(biz, Id, HttpContext.Connection.RemoteIpAddress?.ToString()));
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage("/Admin/Ai/Runs/Index");
    }
}
