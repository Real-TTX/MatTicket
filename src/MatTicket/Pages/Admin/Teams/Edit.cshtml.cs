using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Teams;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        public string Name { get; set; } = "";

        public string? Description { get; set; }

        [Required(ErrorMessage = "Bitte wähle eine Abteilung.")]
        public string? DepartmentId { get; set; }

        public string? Members { get; set; }

        public string? Leads { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Team? Team { get; private set; }

    public List<SelectListItem> Departments { get; private set; } = [];

    public List<SelectListItem> Agents { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long? departmentId)
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input = Team is { } t
            ? new InputModel
            {
                Name = t.Name,
                Description = t.Description,
                DepartmentId = t.DepartmentId.ToString(),
                Members = IdList.Join(t.Members.Select(m => m.UserId)),
                Leads = IdList.Join(t.Members.Where(m => m.IsLead).Select(m => m.UserId)),
            }
            : new InputModel { DepartmentId = departmentId?.ToString() };
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
                var members = IdList.Parse(Input.Members);
                var leads = IdList.Parse(Input.Leads);
                var saved = await OrganizationLogic.SaveTeamAsync(biz, Id, IdList.Parse(Input.DepartmentId).FirstOrDefault(), Input.Name, Input.Description, [.. members.Union(leads)], leads);
                Flash("Das Team ist gespeichert.");
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
            await OrganizationLogic.DeleteTeamAsync(biz, Id ?? 0);
            Flash("Das Team ist gelöscht. Seine Tickets bleiben in der Abteilung.");
            return RedirectToPage("/Admin/Teams/Index");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id });
        }
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Team = await OrganizationLogic.TeamAsync(biz, id)) is null)
        {
            return false;
        }

        Departments = Controls.Options.From(await OrganizationLogic.DepartmentChoicesAsync(biz));
        Agents = Controls.Options.From(await OrganizationLogic.AgentChoicesAsync(biz));
        var title = Team?.Name ?? "Neues Team";
        Head(title, Team?.Department?.Name, ("Einstellungen", null), ("Teams", "/Admin/Teams"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Teams";
        return true;
    }
}
