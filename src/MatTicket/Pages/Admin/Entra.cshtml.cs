using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin;

/// <summary>The sign-in with Microsoft Entra ID: tenant, client and secret (stored encrypted, never shown), the groups that may sign in, a short guide.</summary>
public sealed class EntraModel(Biz biz, PublicUrl publicUrl) : PageModelBase
{
    public sealed class InputModel
    {
        public bool Enabled { get; set; }

        public string TenantId { get; set; } = "";

        public string ClientId { get; set; } = "";

        public string? ClientSecret { get; set; }

        public bool RemoveSecret { get; set; }

        public string CallbackPath { get; set; } = "/signin-entra";

        public bool UnmappedAsRequesters { get; set; }
    }

    public sealed class GroupInput
    {
        public string? ObjectId { get; set; }

        public UserRole Role { get; set; } = UserRole.Agent;

        public string? Note { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public GroupInput Group { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public EntraSettings Settings { get; private set; } = new();

    public List<EntraGroupRole> Groups { get; private set; } = [];

    public string CallbackUrl => publicUrl.Absolute(Settings.CallbackPath);

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Input = new InputModel { Enabled = Settings.Enabled, TenantId = Settings.TenantId, ClientId = Settings.ClientId, CallbackPath = Settings.CallbackPath, UnmappedAsRequesters = Settings.UnmappedAsRequesters };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await EntraLogic.SaveSettingsAsync(biz, Input.Enabled, Input.TenantId, Input.ClientId, Input.ClientSecret, Input.RemoveSecret, Input.CallbackPath, Input.UnmappedAsRequesters);
            Flash(Input.Enabled ? "Gespeichert. Die Anmeldung mit Microsoft gilt sofort." : "Gespeichert. Die Anmeldung mit Microsoft ist aus.");
            return RedirectToPage();
        }
        catch (BizException ex)
        {
            ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAddGroupAsync()
    {
        try
        {
            await EntraLogic.AddGroupAsync(biz, Group.ObjectId ?? "", Group.Role, Group.Note);
            Flash("Die Gruppe ist eingetragen.");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { tab = "groups" });
    }

    public async Task<IActionResult> OnPostRemoveGroupAsync(long id)
    {
        await EntraLogic.RemoveGroupAsync(biz, id);
        Flash("Die Gruppe ist entfernt. Ihre Mitglieder kommen beim nächsten Anmelden nicht mehr hinein.");
        return RedirectToPage(new { tab = "groups" });
    }

    private async Task LoadAsync()
    {
        Settings = EntraLogic.Settings(biz);
        Groups = await EntraLogic.GroupsAsync(biz);
        Head("Anmeldung mit Microsoft", "Microsoft Entra ID als Haupt-Anmeldung; das Passwort bleibt als Notzugang.", ("Einstellungen", null), ("Anmeldung", null));
    }
}
