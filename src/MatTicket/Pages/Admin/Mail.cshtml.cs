using MatTicket.Logic;
using MatTicket.Services.Mail;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin;

/// <summary>The mail server for the mails of the tickets (received, answered, new for the department). The password is stored encrypted.</summary>
public sealed class MailModel(Biz biz, MailQueue queue) : PageModelBase
{
    public sealed class InputModel
    {
        public bool Enabled { get; set; }

        public string Host { get; set; } = "";

        public int Port { get; set; } = 587;

        public string Security { get; set; } = "starttls";

        public string? UserName { get; set; }

        public string? Password { get; set; }

        public string FromAddress { get; set; } = "";

        public string FromName { get; set; } = "MatTicket";

        public bool NotifyAgents { get; set; } = true;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? TestTo { get; set; }

    public bool HasPassword { get; private set; }

    public void OnGet()
    {
        var s = SettingLogic.Get<MailSettings>(biz, SettingStore.Mail);
        Input = new InputModel { Enabled = s.Enabled, Host = s.Host, Port = s.Port, Security = s.Security, UserName = s.UserName, FromAddress = s.FromAddress, FromName = s.FromName, NotifyAgents = s.NotifyAgents };
        Load(s);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        biz.RequireAdmin();
        var current = SettingLogic.Get<MailSettings>(biz, SettingStore.Mail);
        if (Input.Enabled && (string.IsNullOrWhiteSpace(Input.Host) || !Core.Forms.FormEngine.IsEmail(Input.FromAddress)))
        {
            ModelState.AddModelError(string.Empty, "Zum Einschalten braucht es den Server und eine gültige Absenderadresse.");
            Load(current);
            return Page();
        }

        await SettingLogic.SaveAsync(biz, SettingStore.Mail, new MailSettings
        {
            Enabled = Input.Enabled,
            Host = Input.Host.Trim(),
            Port = Math.Clamp(Input.Port, 1, 65535),
            Security = Input.Security is "ssl" or "none" ? Input.Security : "starttls",
            UserName = (Input.UserName ?? "").Trim(),
            PasswordProtected = SettingLogic.KeepOrProtect(biz, Input.Password, current.PasswordProtected),
            FromAddress = Input.FromAddress.Trim(),
            FromName = string.IsNullOrWhiteSpace(Input.FromName) ? "MatTicket" : Input.FromName.Trim(),
            NotifyAgents = Input.NotifyAgents,
        }, "Mail-Einstellungen geändert");
        Flash("Die Mail-Einstellungen sind gespeichert.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        biz.RequireAdmin();
        if (!Core.Forms.FormEngine.IsEmail(TestTo))
        {
            FlashError("Bitte gib eine gültige Adresse für den Test ein.");
            return RedirectToPage();
        }

        var error = await queue.TestAsync(TestTo!);
        if (error is null)
        {
            Flash($"Die Testnachricht an {TestTo} ist verschickt.");
        }
        else
        {
            FlashError("Der Test ist fehlgeschlagen: " + error);
        }

        return RedirectToPage();
    }

    private void Load(MailSettings s)
    {
        HasPassword = s.PasswordProtected.Length > 0;
        Head("E-Mail", "Bestätigungen und Antworten an Kunden, Hinweise an Teams.", ("Einstellungen", null), ("E-Mail", null));
        ViewData["Width"] = "narrow";
    }
}
