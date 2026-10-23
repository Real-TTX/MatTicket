using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Ai.Profiles;

/// <summary>The named connections to an AI; exactly one is active, and switching applies at once for the whole app.</summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    public sealed record Row(AiProfile Profile, bool HasKey);

    public List<Row> Rows { get; private set; } = [];

    public bool RelayConfigured { get; private set; }

    public async Task OnGetAsync()
    {
        Head("KI-Profile", "Verbindungen zu einem KI-Anbieter. Genau eine ist aktiv.", ("Einstellungen", null), ("KI-Profile", null));
        Rows = [.. (await AiProfileLogic.ListAsync(biz)).Select(p => new Row(p, AiProfileLogic.HasKey(biz, p)))];
        RelayConfigured = !string.IsNullOrWhiteSpace(biz.Config.Current.Ai.RelayUrl);
    }

    public async Task<IActionResult> OnPostActivateAsync(long id)
    {
        await AiProfileLogic.ActivateAsync(biz, id);
        Flash("Das Profil ist jetzt aktiv. Die ganze App nutzt es ab sofort.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDuplicateAsync(long id)
    {
        var copy = await AiProfileLogic.DuplicateAsync(biz, id);
        Flash("Die Kopie ist angelegt – ohne Schlüssel und ohne Anmeldung.");
        return RedirectToPage("/Admin/Ai/Profiles/Edit", new { id = copy.Id });
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        var result = await AiLogic.TestAsync(biz, HttpContext.Connection.RemoteIpAddress?.ToString());
        if (result.StartsWith("Verbindung steht", StringComparison.Ordinal))
        {
            Flash(result);
        }
        else
        {
            FlashError(result);
        }

        return RedirectToPage();
    }
}
