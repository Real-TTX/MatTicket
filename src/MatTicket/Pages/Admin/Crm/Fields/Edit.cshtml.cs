using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Crm.Fields;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        public CrmEntity Entity { get; set; } = CrmEntity.Company;

        [Required(ErrorMessage = "Bitte gib eine Beschriftung ein.")]
        public string Label { get; set; } = "";

        public string? Key { get; set; }

        public CustomFieldType Type { get; set; } = CustomFieldType.Text;

        public string? Options { get; set; }

        public string? Hint { get; set; }

        public string? Section { get; set; }

        public bool Required { get; set; }

        public int SortOrder { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public CustomField? Field { get; private set; }

    public async Task<IActionResult> OnGetAsync(CrmEntity? entity)
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input = Field is { } f
            ? new InputModel { Entity = f.Entity, Label = f.Label, Key = f.Key, Type = f.Type, Options = f.Options, Hint = f.Hint, Section = f.Section, Required = f.Required, SortOrder = f.SortOrder }
            : new InputModel { Entity = entity ?? CrmEntity.Company };
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
                var saved = await CrmLogic.SaveCustomFieldAsync(biz, Id, new CustomField
                {
                    Entity = Input.Entity, Label = Input.Label, Key = Input.Key ?? "", Type = Input.Type, Options = Input.Options, Hint = Input.Hint,
                    Section = Input.Section, Required = Input.Required, SortOrder = Input.SortOrder,
                });
                Flash("Das Feld ist gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await CrmLogic.DeleteCustomFieldAsync(biz, Id ?? 0);
        Flash("Das Feld ist gelöscht. Gespeicherte Werte bleiben in den Datensätzen, werden aber nicht mehr gezeigt.");
        return RedirectToPage("/Admin/Crm/Fields/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Field = await CrmLogic.CustomFieldAsync(biz, id)) is null)
        {
            return false;
        }

        var title = Field?.Label ?? "Neues Feld";
        Head(title, null, ("Einstellungen", null), ("Felder", "/Admin/Crm/Fields"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Crm/Fields";
        return true;
    }
}
