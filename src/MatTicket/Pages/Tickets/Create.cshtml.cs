using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Tickets;

/// <summary>A ticket the service writes down itself: a call, a visit, a mail. Category, department and team can be chosen or follow the category.</summary>
public sealed class CreateModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Betreff ein.")]
        [Display(Name = "Betreff")]
        public string Subject { get; set; } = "";

        [Display(Name = "Beschreibung")]
        public string? Description { get; set; }

        public TicketPriority Priority { get; set; } = TicketPriority.Normal;

        public string? CategoryId { get; set; }

        public string? DepartmentId { get; set; }

        public string? TeamId { get; set; }

        public string? AssigneeUserId { get; set; }

        public string? ContactId { get; set; }

        public string? RequesterName { get; set; }

        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        public string? RequesterEmail { get; set; }

        public string? RequesterPhone { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public List<SelectListItem> Categories { get; private set; } = [];

    public List<SelectListItem> Departments { get; private set; } = [];

    public List<SelectListItem> Teams { get; private set; } = [];

    public List<SelectListItem> Agents { get; private set; } = [];

    public List<SelectListItem> Contacts { get; private set; } = [];

    public async Task OnGetAsync(long? contactId)
    {
        Input.ContactId = contactId?.ToString();
        Input.AssigneeUserId = biz.UserId.ToString();
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (ModelState.IsValid)
        {
            try
            {
                var ticket = await TicketLogic.CreateByAgentAsync(biz, new TicketLogic.NewTicket(Input.Subject, Core.Text.HtmlSafe.FromText(Input.Description), Input.Priority,
                    Id(Input.CategoryId), Id(Input.DepartmentId), Id(Input.TeamId), Id(Input.AssigneeUserId), Id(Input.ContactId), Input.RequesterName, Input.RequesterEmail, Input.RequesterPhone));
                Flash($"Ticket {ticket.Number} ist angelegt.");
                return RedirectToPage("/Tickets/Details", new { id = ticket.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        await LoadAsync();
        return Page();
    }

    private static long? Id(string? value) => IdList.Parse(value).Cast<long?>().FirstOrDefault();

    private async Task LoadAsync()
    {
        Head("Neues Ticket", "Für einen Anruf, einen Besuch oder eine E-Mail.", ("Tickets", "/Tickets"), ("Neues Ticket", null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Tickets/Create";
        Categories = Controls.Options.From(await CategoryLogic.ChoicesAsync(biz, onlyActive: true));
        Departments = Controls.Options.From(await OrganizationLogic.DepartmentChoicesAsync(biz));
        Teams = Controls.Options.From(await OrganizationLogic.TeamChoicesAsync(biz));
        Agents = Controls.Options.From(await OrganizationLogic.AgentChoicesAsync(biz));
        Contacts = Controls.Options.From(await CrmLogic.ContactChoicesAsync(biz));
    }
}
