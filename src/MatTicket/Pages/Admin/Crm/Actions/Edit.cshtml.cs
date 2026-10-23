using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Crm.Actions;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        public CrmEntity Entity { get; set; } = CrmEntity.Company;

        public ViewActionKind Kind { get; set; } = ViewActionKind.Link;

        [Required(ErrorMessage = "Bitte gib die Beschriftung ein.")]
        public string Label { get; set; } = "";

        public string? Icon { get; set; }

        [Required(ErrorMessage = "Bitte gib die Vorlage ein.")]
        public string Template { get; set; } = "";

        public bool NewWindow { get; set; } = true;

        public bool IsActive { get; set; } = true;

        public int SortOrder { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ViewAction? Action { get; private set; }

    public List<CrmField> CompanyFields { get; private set; } = [];

    public List<CrmField> ContactFields { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Action is { } a)
        {
            Input = new InputModel { Entity = a.Entity, Kind = a.Kind, Label = a.Label, Icon = a.Icon, Template = a.Template, NewWindow = a.NewWindow, IsActive = a.IsActive, SortOrder = a.SortOrder };
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
                var saved = await CrmLogic.SaveViewActionAsync(biz, Id, new ViewAction
                {
                    Entity = Input.Entity, Kind = Input.Kind, Label = Input.Label, Icon = Input.Icon ?? "", Template = Input.Template, NewWindow = Input.NewWindow,
                    IsActive = Input.IsActive, SortOrder = Input.SortOrder,
                });
                Flash("Der Knopf ist gespeichert.");
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
        await CrmLogic.DeleteViewActionAsync(biz, Id ?? 0);
        Flash("Der Knopf ist gelöscht.");
        return RedirectToPage("/Admin/Crm/Actions/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Action = await CrmLogic.ViewActionAsync(biz, id)) is null)
        {
            return false;
        }

        CompanyFields = await CrmLogic.FieldsAsync(biz, CrmEntity.Company);
        ContactFields = await CrmLogic.FieldsAsync(biz, CrmEntity.Contact);
        var title = Action?.Label ?? "Neuer Knopf";
        Head(title, null, ("Einstellungen", null), ("Knöpfe", "/Admin/Crm/Actions"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Crm/Actions";
        return true;
    }
}
