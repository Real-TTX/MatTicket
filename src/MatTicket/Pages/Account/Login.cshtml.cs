using System.ComponentModel.DataAnnotations;
using MatTicket.Services;
using MatTicket.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(SignInService signIn, TwoFactorTicket ticket, ActivityLogger log, MatTicket.Logic.SettingStore settings) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail-Adresse")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib dein Passwort ein.")]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Why the sign-in with Microsoft did not work (set by the handler of Entra or the callback).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Entra { get; set; }

    /// <summary>Microsoft is the main sign-in: its button comes first, the password form is the emergency access behind it.</summary>
    public bool EntraAvailable => MatTicket.Logic.EntraLogic.IsUsable(settings);

    /// <summary>The password form is open when Entra is off, when it failed, or when the password form was just sent.</summary>
    public bool PasswordOpen { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(Landing());
        }

        ViewData["Title"] = "Anmelden";
        ViewData["ClearKept"] = true;
        PasswordOpen = !EntraAvailable || !string.IsNullOrEmpty(Entra);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Anmelden";
        ViewData["ClearKept"] = true;
        PasswordOpen = true;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.CheckPasswordAsync(Input.Email, Input.Password, HttpContext.RequestAborted);
        switch (result.Outcome)
        {
            case SignInOutcome.SecondFactor:
                ticket.Issue(HttpContext, result.User!.Id, ReturnUrl);
                return RedirectToPage("/Account/TwoFactor");

            case SignInOutcome.Success:
                await signIn.StartSessionAsync(HttpContext, result.User!, HttpContext.RequestAborted);
                await log.LogAsync("Anmeldung", "User", result.User!.Id, result.User.Email);
                return LocalRedirect(Landing());

            case SignInOutcome.LockedOut:
                ModelState.AddModelError(string.Empty, "Zu viele Fehlversuche. Das Konto ist für einige Minuten gesperrt.");
                return Page();

            case SignInOutcome.Inactive:
                ModelState.AddModelError(string.Empty, "Dieses Konto ist deaktiviert. Bitte wende dich an die Administration.");
                return Page();

            default:
                ModelState.AddModelError(string.Empty, "E-Mail-Adresse oder Passwort stimmen nicht.");
                return Page();
        }
    }

    private string Landing() => SafeReturn(ReturnUrl, "/");
}
