using MatTicket.Logic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Account;

/// <summary>
/// Starts the sign-in with Microsoft; Microsoft comes back to the callback path, then to <see cref="EntraCallbackModel"/>. The challenge runs
/// here (not as a result of the page), so that a configuration Microsoft does not know (a wrong tenant, Microsoft not reachable) ends on the
/// sign-in page with an explanation instead of an error page.
/// </summary>
[AllowAnonymous]
public sealed class EntraModel(SettingStore settings, ILogger<EntraModel> logger) : PageModelBase
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        if (!EntraLogic.IsUsable(settings))
        {
            return RedirectToPage("/Account/Login", new { entra = "Die Anmeldung mit Microsoft ist nicht eingerichtet." });
        }

        var back = SafeReturn(returnUrl);
        try
        {
            await HttpContext.ChallengeAsync(EntraLogic.Scheme, new AuthenticationProperties { RedirectUri = "/Account/EntraCallback?returnUrl=" + Uri.EscapeDataString(back) });
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !HttpContext.Response.HasStarted)
        {
            logger.LogWarning(ex, "The sign-in with Microsoft could not start.");
            HttpContext.Response.Clear();
            var reason = ex.Message.Contains("IDX20803", StringComparison.Ordinal) || ex is HttpRequestException or TaskCanceledException
                ? "Microsoft kennt diesen Tenant nicht oder ist gerade nicht erreichbar (die Anmeldedaten von Microsoft konnten nicht geladen werden). Bitte die Tenant-ID prüfen."
                : "Die Anmeldung mit Microsoft konnte nicht starten: " + ex.Message;
            return RedirectToPage("/Account/Login", new { entra = reason });
        }
    }
}
