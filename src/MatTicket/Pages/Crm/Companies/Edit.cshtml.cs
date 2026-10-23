using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Crm.Companies;

/// <summary>A company: its data (built-in and custom fields on tabs), the buttons to other systems, the map, its contacts and its tickets.</summary>
public sealed class EditModel(Biz biz) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public Company Input { get; set; } = new();

    public Company? Company { get; private set; }

    public List<CrmField> Custom { get; private set; } = [];

    public HashSet<string> Hidden { get; private set; } = [];

    public Dictionary<string, string> Extras { get; private set; } = [];

    public List<CrmButton> Buttons { get; private set; } = [];

    public List<Ticket> Tickets { get; private set; } = [];

    public string? MapUrl { get; private set; }

    public bool IsAdmin => biz.IsAdmin;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input = Company ?? new Company();
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
            var saved = await CrmLogic.SaveCompanyAsync(biz, Id, Input, posted);
            Flash("Die Firma ist gespeichert.");
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
        await CrmLogic.DeleteCompanyAsync(biz, Id ?? 0);
        Flash("Die Firma ist gelöscht.");
        return RedirectToPage("/Crm/Companies/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Company = await CrmLogic.CompanyAsync(biz, id)) is null)
        {
            return false;
        }

        Custom = [.. (await CrmLogic.FieldsAsync(biz, CrmEntity.Company)).Where(f => !f.BuiltIn)];
        Hidden = [.. CrmLogic.Layout(biz, CrmEntity.Company).Hidden];
        if (Company is { } c)
        {
            Buttons = await CrmLogic.ButtonsAsync(biz, CrmEntity.Company, k => CrmLogic.ValueOf(c, k));
            Tickets = await CrmLogic.TicketsOfAsync(biz, c.Id, null);
            var address = CrmLogic.ValueOf(c, "Address");
            MapUrl = string.IsNullOrWhiteSpace(address) ? null : CrmLogic.MapEmbedUrl(address);
        }

        var title = Company?.Name ?? "Neue Firma";
        Head(title, Company?.CustomerNumber is { } n ? "Kundennummer " + n : null, ("Kunden", null), ("Firmen", "/Crm/Companies"), (title, null));
        ViewData["ActiveHref"] = "/Crm/Companies";
        return true;
    }
}
