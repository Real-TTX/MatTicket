using MatTicket.Logic;
using MatTicket.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Account;

/// <summary>
/// Microsoft confirmed who the person is: the groups decide whether they get in and with which role. A session of the app is opened, the
/// short-lived cookie of the answer is removed. Whoever is in no mapped group sees "Kein Zugriff" with the reason.
/// </summary>
[AllowAnonymous]
public sealed class EntraCallbackModel(Biz biz, SignInService signIn) : PageModelBase
{
    public string? Reason { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        var result = await HttpContext.AuthenticateAsync(EntraLogic.ExternalScheme);
        await HttpContext.SignOutAsync(EntraLogic.ExternalScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return RedirectToPage("/Account/Login", new { entra = "Die Antwort von Microsoft ist abgelaufen oder fehlt. Bitte noch einmal anmelden." });
        }

        var outcome = await EntraLogic.SignInAsync(biz, result.Principal);
        if (outcome.User is null)
        {
            await biz.Log.LogAsync("Anmeldung mit Entra abgelehnt", "User", null, outcome.Denied);
            Reason = outcome.Denied;
            ViewData["Title"] = "Kein Zugriff";
            return Page();
        }

        await signIn.StartSessionAsync(HttpContext, outcome.User);
        await biz.Log.LogAsync("Anmeldung mit Entra", "User", outcome.User.Id, outcome.User.Email);
        return LocalRedirect(SafeReturn(returnUrl, "/"));
    }
}
