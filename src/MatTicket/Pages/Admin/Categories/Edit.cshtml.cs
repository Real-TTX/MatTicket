using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Categories;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        public string Name { get; set; } = "";

        public string? ParentId { get; set; }

        public string? Description { get; set; }

        public string? Icon { get; set; }

        public string? DepartmentId { get; set; }

        public string? TeamId { get; set; }

        public TicketPriority DefaultPriority { get; set; } = TicketPriority.Normal;

        public bool IsActive { get; set; } = true;

        public int SortOrder { get; set; }

        public string? AccessGroups { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Category? Category { get; private set; }

    public List<SelectListItem> Parents { get; private set; } = [];

    public List<SelectListItem> Departments { get; private set; } = [];

    public List<SelectListItem> Teams { get; private set; } = [];

    public List<SelectListItem> Groups { get; private set; } = [];

    private List<Choice> _groups = [];

    public async Task<IActionResult> OnGetAsync(long? parentId)
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input = Category is { } c
            ? new InputModel
            {
                Name = c.Name, ParentId = c.ParentId?.ToString(), Description = c.Description, Icon = c.Icon, DepartmentId = c.DepartmentId?.ToString(),
                TeamId = c.TeamId?.ToString(), DefaultPriority = c.DefaultPriority, IsActive = c.IsActive, SortOrder = c.SortOrder,
                AccessGroups = IdList.Join(_groups.Where(g => PortalLogic.Groups(c.AccessGroupsJson).Contains(g.Detail, StringComparer.OrdinalIgnoreCase)).Select(g => g.Id)),
            }
            : new InputModel { ParentId = parentId?.ToString() };
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
                var saved = await CategoryLogic.SaveAsync(biz, Id, new Category
                {
                    Name = Input.Name,
                    ParentId = IdOf(Input.ParentId),
                    Description = Input.Description,
                    Icon = Input.Icon,
                    DepartmentId = IdOf(Input.DepartmentId),
                    TeamId = IdOf(Input.TeamId),
                    DefaultPriority = Input.DefaultPriority,
                    IsActive = Input.IsActive,
                    SortOrder = Input.SortOrder,
                    AccessGroupsJson = PortalLogic.GroupsToJson(IdList.Parse(Input.AccessGroups).Select(id => _groups.FirstOrDefault(g => g.Id == id)?.Detail ?? "")),
                });
                Flash("Die Kategorie ist gespeichert.");
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
            await CategoryLogic.DeleteAsync(biz, Id ?? 0);
            Flash("Die Kategorie ist gelöscht.");
            return RedirectToPage("/Admin/Categories/Index");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id });
        }
    }

    private static long? IdOf(string? value) => IdList.Parse(value).Cast<long?>().FirstOrDefault();

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Category = await CategoryLogic.GetAsync(biz, id)) is null)
        {
            return false;
        }

        Parents = Controls.Options.From(await CategoryLogic.ParentChoicesAsync(biz, Id));
        Departments = Controls.Options.From(await OrganizationLogic.DepartmentChoicesAsync(biz));
        Teams = Controls.Options.From(await OrganizationLogic.TeamChoicesAsync(biz));
        _groups = await EntraLogic.GroupChoicesAsync(biz);
        Groups = Controls.Options.From(_groups);
        var title = Category?.Name ?? "Neue Kategorie";
        Head(title, null, ("Einstellungen", null), ("Kategorien", "/Admin/Categories"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Categories";
        return true;
    }
}
