using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Crm.Contacts;

/// <summary>A contact: the person, the address (the one of the company when it has none), custom fields, buttons, map and tickets.</summary>
public sealed class EditModel(Biz biz) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public Contact Input { get; set; } = new();

    [BindProperty]
    public string? CompanyId { get; set; }

    public Contact? Contact { get; private set; }

    public List<CrmField> Custom { get; private set; } = [];

    public HashSet<string> Hidden { get; private set; } = [];

    public Dictionary<string, string> Extras { get; private set; } = [];

    public List<CrmButton> Buttons { get; private set; } = [];

    public List<Ticket> Tickets { get; private set; } = [];

    public List<SelectListItem> Companies { get; private set; } = [];

    public string? MapUrl { get; private set; }

    public bool IsAdmin => biz.IsAdmin;

    public async Task<IActionResult> OnGetAsync(long? companyId)
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input = Contact ?? new Contact();
        CompanyId = (Contact?.CompanyId ?? companyId)?.ToString();
        Extras = CrmLogic.Extras(Input.ExtraJson);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var posted = CrmPage.PostedExtras(Request.Form);
        try
        {
            Input.CompanyId = IdList.Parse(CompanyId).Cast<long?>().FirstOrDefault();
            var saved = await CrmLogic.SaveContactAsync(biz, Id, Input, posted);
            Flash("Der Kontakt ist gespeichert.");
            return RedirectToPage(new { id = saved.Id });
        }
        catch (BizException ex)
        {
            ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            Extras = posted.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await CrmLogic.DeleteContactAsync(biz, Id ?? 0);
        Flash("Der Kontakt ist gelöscht.");
        return RedirectToPage("/Crm/Contacts/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Contact = await CrmLogic.ContactAsync(biz, id)) is null)
        {
            return false;
        }

        Custom = [.. (await CrmLogic.FieldsAsync(biz, CrmEntity.Contact)).Where(f => !f.BuiltIn)];
        Hidden = [.. CrmLogic.Layout(biz, CrmEntity.Contact).Hidden];
        Companies = Controls.Options.From(await CrmLogic.CompanyChoicesAsync(biz));
        if (Contact is { } c)
        {
            Buttons = await CrmLogic.ButtonsAsync(biz, CrmEntity.Contact, k => CrmLogic.ValueOf(c, k));
            Tickets = await CrmLogic.TicketsOfAsync(biz, null, c.Id);
            var address = CrmLogic.ValueOf(c, "Address");
            MapUrl = string.IsNullOrWhiteSpace(address) ? null : CrmLogic.MapEmbedUrl(address);
        }

        var title = Contact?.FullName is { Length: > 0 } name ? name : "Neuer Kontakt";
        Head(title, Contact?.Company?.Name, ("Kunden", null), ("Kontakte", "/Crm/Contacts"), (title, null));
        ViewData["ActiveHref"] = "/Crm/Contacts";
        return true;
    }
}
