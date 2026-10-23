using System.ComponentModel.DataAnnotations;
using MatTicket.Logic;
using MatTicket.Data.Entities;
using MatTicket.Services;
using MatTicket.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Account;

/// <summary>The first start: creates the first administrator. The page exists only while there is none.</summary>
[AllowAnonymous]
public sealed class SetupModel(Biz biz, SignInService signIn, SetupState setup) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deinen Namen ein.")]
        [Display(Name = "Name")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib eine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail-Adresse")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte wähle ein Passwort.")]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Bitte wiederhole das Passwort.")]
        [Display(Name = "Passwort wiederholen")]
        public string PasswordRepeat { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Title"] = "Einrichtung";
        return await UserLogic.HasAdminAsync(biz.Db) ? RedirectToPage("/Account/Login") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Einrichtung";
        if (await UserLogic.HasAdminAsync(biz.Db))
        {
            return RedirectToPage("/Account/Login");
        }

        if (ModelState.IsValid)
        {
            if (CheckedPassword() is { } problem)
            {
                ModelState.AddModelError("Input.Password", problem);
            }
            else if (Input.Password != Input.PasswordRepeat)
            {
                ModelState.AddModelError("Input.PasswordRepeat", "Die Passwörter stimmen nicht überein.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var admin = await UserLogic.CreateFirstAdminAsync(biz, Input.Email, Input.DisplayName, Input.Password);
        setup.MarkDone();
        await signIn.StartSessionAsync(HttpContext, admin);
        await biz.Log.LogAsync("Einrichtung", "User", admin.Id, "Erster Administrator angelegt");
        Flash("Willkommen! Lege unter Einstellungen > System Beispieldaten an, oder baue dein erstes Formular.");
        return RedirectToPage("/Index");
    }

    private string? CheckedPassword() => UserLogic.CheckPassword(Input.Password, Input.Email);
}
