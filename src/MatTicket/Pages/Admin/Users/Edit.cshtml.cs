using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Users;

/// <summary>One account: name, address, role, teams, whether it may sign in, a new password, the second factor and the sessions.</summary>
public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib den Namen ein.")]
        [Display(Name = "Name")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib die E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail-Adresse")]
        public string Email { get; set; } = "";

        [Display(Name = "Telefon")]
        public string? Phone { get; set; }

        [Display(Name = "Rolle")]
        public UserRole Role { get; set; } = UserRole.Agent;

        public bool IsActive { get; set; } = true;

        [Display(Name = "Passwort")]
        public string? Password { get; set; }

        [Display(Name = "Teams")]
        public string? Teams { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public UserLogic.UserDetails? Details { get; private set; }

    public User? Account => Details?.User;

    public bool IsMe => Account is not null && Account.Id == biz.User.UserId;

    public List<SelectListItem> TeamItems { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Details is { } d)
        {
            Input = new InputModel { DisplayName = d.User.DisplayName, Email = d.User.Email, Phone = d.User.Phone, Role = d.User.Role, IsActive = d.User.IsActive, Teams = IdList.Join(d.TeamIds) };
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
                var user = await UserLogic.SaveAsync(biz, Id, new UserLogic.UserInput(Input.DisplayName, Input.Email, Input.Phone, Input.Role, Input.IsActive, Input.Password, IdList.Parse(Input.Teams)));
                Flash(Id is null ? "Der Benutzer ist angelegt." : "Der Benutzer ist gespeichert.");
                return RedirectToPage("/Admin/Users/Edit", new { id = user.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostResetTotpAsync()
    {
        await UserLogic.ResetTotpAsync(biz, Id ?? 0);
        Flash("Die Zwei-Faktor-Anmeldung ist abgeschaltet. Die Person kann sie neu einrichten.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostEndSessionsAsync()
    {
        var keep = Guid.TryParse(User.FindFirst(Services.AppClaims.Session)?.Value, out var token) ? token : (Guid?)null;
        await UserLogic.EndSessionsAsync(biz, Id ?? 0, keep);
        Flash("Alle Sitzungen sind beendet. Die Person muss sich neu anmelden.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostUnlockAsync()
    {
        await UserLogic.UnlockAsync(biz, Id ?? 0);
        Flash("Die Sperre nach falschen Anmeldungen ist aufgehoben.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        try
        {
            await UserLogic.DeleteAsync(biz, Id ?? 0);
            Flash("Der Benutzer ist gelöscht. Seine Einträge bleiben erhalten.");
            return RedirectToPage("/Admin/Users/Index");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id });
        }
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Details = await UserLogic.DetailsAsync(biz, id);
            if (Details is null)
            {
                return false;
            }
        }

        TeamItems = Controls.Options.From(await OrganizationLogic.TeamChoicesAsync(biz));
        var title = Id is null ? "Neuer Benutzer" : Account!.DisplayName;
        Head(title, null, ("Einstellungen", null), ("Benutzer", "/Admin/Users"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Users";
        return true;
    }
}
