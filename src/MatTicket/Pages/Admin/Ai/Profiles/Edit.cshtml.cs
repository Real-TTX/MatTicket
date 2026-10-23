using System.ComponentModel.DataAnnotations;
using MatTicket.Core.Ai;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Ai.Profiles;

/// <summary>
/// One AI profile: provider, address (a template fills it), model ("Modelle laden" asks the provider), the key (stored encrypted, never shown),
/// or for ChatGPT the sign-in with a code.
/// </summary>
public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Profil einen Namen.")]
        public string Name { get; set; } = "";

        public AiProvider Provider { get; set; } = AiProvider.OpenAiCompatible;

        public string? Preset { get; set; } = "mistral";

        public string? BaseUrl { get; set; } = "https://api.mistral.ai/v1";

        public string? Model { get; set; } = "mistral-small-latest";

        public string? ApiKey { get; set; }

        public bool RemoveKey { get; set; }

        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public AiProfile? Profile { get; private set; }

    public bool HasKey { get; private set; }

    public ChatGptAccount.Status? ChatGpt { get; private set; }

    public List<SelectListItem> Presets { get; } = [.. AiPresets.All.Select(p => new SelectListItem($"{p.Label} ({p.Region})", p.Id))];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Profile is { } p)
        {
            Input = new InputModel { Name = p.Name, Provider = p.Provider, Preset = p.Preset, BaseUrl = p.BaseUrl, Model = p.Model, Note = p.Note };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var saved = await AiProfileLogic.SaveAsync(biz, Id, new AiProfileLogic.ProfileInput(Input.Name, Input.Provider, Input.Preset, Input.BaseUrl ?? "", Input.Model ?? "", Input.ApiKey, Input.RemoveKey, Input.Note));
                Flash("Das Profil ist gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostChatGptStartAsync()
    {
        var (_, error) = await AiProfileLogic.StartChatGptLoginAsync(biz, Id ?? 0);
        if (error is not null)
        {
            FlashError(error);
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostChatGptLogoutAsync()
    {
        await AiProfileLogic.ChatGptLogoutAsync(biz, Id ?? 0);
        Flash("Von ChatGPT abgemeldet.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostActivateAsync()
    {
        await AiProfileLogic.ActivateAsync(biz, Id ?? 0);
        Flash("Das Profil ist jetzt aktiv.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await AiProfileLogic.DeleteAsync(biz, Id ?? 0);
        Flash("Das Profil ist gelöscht, mit seinem Schlüssel und seiner Anmeldung.");
        return RedirectToPage("/Admin/Ai/Profiles/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Profile = await AiProfileLogic.GetAsync(biz, id)) is null)
        {
            return false;
        }

        if (Profile is { } p)
        {
            HasKey = AiProfileLogic.HasKey(biz, p);
            if (p.Provider == AiProvider.ChatGpt)
            {
                ChatGpt = await AiProfileLogic.ChatGptStatusAsync(biz, p.Id);
            }
        }

        var title = Profile?.Name ?? "Neues KI-Profil";
        Head(title, Profile is { IsActive: true } ? "Dieses Profil ist aktiv." : null, ("Einstellungen", null), ("KI-Profile", "/Admin/Ai/Profiles"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Ai/Profiles";
        return true;
    }
}
