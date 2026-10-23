using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Crm;

/// <summary>
/// The layout of the CRM pages, per kind: which fields are the columns of the list and in which order (drag the rows), and which built-in fields
/// are hidden on the edit page. Custom fields have their own tab and order on their own page.
/// </summary>
public sealed class LayoutModel(Biz biz) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public CrmEntity Entity { get; set; } = CrmEntity.Company;

    [BindProperty]
    public List<string> Columns { get; set; } = [];

    [BindProperty]
    public List<string> Hidden { get; set; } = [];

    public List<CrmField> Fields { get; private set; } = [];

    public CrmLayout Layout { get; private set; } = new();

    public LinkTabsModel Tabs { get; private set; } = null!;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await CrmLogic.SaveLayoutAsync(biz, Entity, Columns, Hidden);
            Flash("Die Ansicht ist gespeichert.");
            return RedirectToPage(new { entity = Entity });
        }
        catch (BizException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync();
            return Page();
        }
    }

    /// <summary>The fields in the order of the list: the chosen columns first (in their order), then the others.</summary>
    public IEnumerable<CrmField> Ordered => Layout.ListColumns.Select(k => Fields.FirstOrDefault(f => f.Key == k)).Where(f => f is not null).Select(f => f!)
        .Concat(Fields.Where(f => !Layout.ListColumns.Contains(f.Key)));

    private async Task LoadAsync()
    {
        Fields = await CrmLogic.FieldsAsync(biz, Entity);
        Layout = CrmLogic.Layout(biz, Entity);
        Tabs = new LinkTabsModel("Art der Datensätze",
        [
            new("Company", "Firmen", "/Admin/Crm/Layout?entity=Company", "building"),
            new("Contact", "Kontakte", "/Admin/Crm/Layout?entity=Contact", "users"),
        ], Entity.ToString());
        Head("Ansicht", "Wie die Seiten von Firmen und Kontakten aussehen.", ("Einstellungen", null), ("Ansicht", null));
        ViewData["Width"] = "narrow";
    }
}
