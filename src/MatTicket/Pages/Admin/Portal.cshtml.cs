using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin;

/// <summary>What the public pages say about who runs them: the name of the service, the company, privacy and imprint, the text of the portal.</summary>
public sealed class PortalModel(Biz biz) : PageModelBase
{
    [BindProperty]
    public SiteSettings Input { get; set; } = new();

    public void OnGet()
    {
        Input = SettingLogic.Get<SiteSettings>(biz, SettingStore.Site);
        Load();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        foreach (var (name, url) in new[] { ("Input.PrivacyUrl", Input.PrivacyUrl), ("Input.ImprintUrl", Input.ImprintUrl) })
        {
            if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")))
            {
                ModelState.AddModelError(name, "Bitte eine vollständige Adresse (https://…) eingeben.");
            }
        }

        if (string.IsNullOrWhiteSpace(Input.ServiceName))
        {
            ModelState.AddModelError("Input.ServiceName", "Bitte gib einen Namen ein.");
        }

        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        Input.PortalIntro = (Input.PortalIntro ?? "").Trim();
        await SettingLogic.SaveAsync(biz, SettingStore.Site, Input, "Portal-Einstellungen geändert");
        Flash("Gespeichert.");
        return RedirectToPage();
    }

    private void Load()
    {
        Head("Portal", "Die öffentlichen Seiten: das Hilfe-Portal, die Formulare und die Statusseiten der Kunden.", ("Einstellungen", null), ("Portal", null));
        ViewData["Width"] = "narrow";
    }
}
