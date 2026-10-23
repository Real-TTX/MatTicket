using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Departments;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        public string Name { get; set; } = "";

        public string? Description { get; set; }

        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        public string? Email { get; set; }

        public string? Colour { get; set; }

        public int SortOrder { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Department? Department { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Department is { } d)
        {
            Input = new InputModel { Name = d.Name, Description = d.Description, Email = d.Email, Colour = d.Colour, SortOrder = d.SortOrder };
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
                var saved = await OrganizationLogic.SaveDepartmentAsync(biz, Id, Input.Name, Input.Description, Input.Email, Input.Colour, Input.SortOrder);
                Flash("Die Abteilung ist gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field is null ? string.Empty : "Input." + ex.Field, ex.Message);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        try
        {
            await OrganizationLogic.DeleteDepartmentAsync(biz, Id ?? 0);
            Flash("Die Abteilung ist gelöscht.");
            return RedirectToPage("/Admin/Departments/Index");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id });
        }
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Department = await OrganizationLogic.DepartmentAsync(biz, id)) is null)
        {
            return false;
        }

        var title = Department?.Name ?? "Neue Abteilung";
        Head(title, null, ("Einstellungen", null), ("Abteilungen", "/Admin/Departments"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Departments";
        return true;
    }
}
