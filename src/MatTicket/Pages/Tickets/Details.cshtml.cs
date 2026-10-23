using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Tickets;

/// <summary>
/// One ticket: the conversation with the customer (answers, internal notes, the history), the answers of the form, the customer from the CRM,
/// and on the side what it is (status, priority, category) and where it is (department, team, who works on it).
/// </summary>
public sealed class DetailsModel(Biz biz) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public Ticket Ticket { get; private set; } = null!;

    public IReadOnlyList<TicketAnswer> Answers { get; private set; } = [];

    public List<CrmButton> ContactButtons { get; private set; } = [];

    public List<CrmButton> CompanyButtons { get; private set; } = [];

    public List<Choice> Suggestions { get; private set; } = [];

    public bool AiAvailable { get; private set; }

    public string? MapUrl { get; private set; }

    public List<SelectListItem> Categories { get; private set; } = [];

    public List<SelectListItem> Departments { get; private set; } = [];

    public List<SelectListItem> Teams { get; private set; } = [];

    public List<SelectListItem> Agents { get; private set; } = [];

    public List<SelectListItem> Contacts { get; private set; } = [];

    // ---- what the forms post ----

    [BindProperty]
    public TicketStatus Status { get; set; }

    [BindProperty]
    public TicketPriority Priority { get; set; }

    [BindProperty]
    public string? CategoryId { get; set; }

    [BindProperty]
    public string? DepartmentId { get; set; }

    [BindProperty]
    public string? TeamId { get; set; }

    [BindProperty]
    public string? AssigneeUserId { get; set; }

    [BindProperty]
    public string? DueDate { get; set; }

    [BindProperty]
    public string? Body { get; set; }

    [BindProperty]
    public string Kind { get; set; } = "reply";

    [BindProperty]
    public string? StatusAfter { get; set; }

    [BindProperty]
    public string? AiRunId { get; set; }

    [BindProperty]
    public string? Note { get; set; }

    [BindProperty]
    public string? ContactId { get; set; }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostPropertiesAsync()
        => await RunAsync(async () =>
        {
            DateOnly? due = DateOnly.TryParse(DueDate, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            await TicketLogic.SavePropertiesAsync(biz, Id, new TicketLogic.Properties(Status, Priority, IdOf(CategoryId), IdOf(DepartmentId), IdOf(TeamId), IdOf(AssigneeUserId), due));
            return "Gespeichert.";
        });

    public async Task<IActionResult> OnPostReplyAsync()
        => await RunAsync(async () =>
        {
            var note = Kind == "note";
            TicketStatus? after = Enum.TryParse<TicketStatus>(StatusAfter, out var s) ? s : null;
            await TicketLogic.ReplyAsync(biz, Id, Body ?? "", note, after, IdOf(AiRunId));
            return note ? "Die Notiz ist gespeichert." : "Die Antwort ist gesendet.";
        }, "verlauf");

    public async Task<IActionResult> OnPostMoveAsync()
        => await RunAsync(async () =>
        {
            await TicketLogic.MoveAsync(biz, Id, IdOf(DepartmentId), IdOf(TeamId), Note);
            return "Das Ticket ist verschoben.";
        });

    public async Task<IActionResult> OnPostTakeAsync()
        => await RunAsync(async () =>
        {
            await TicketLogic.TakeAsync(biz, Id);
            return "Du bearbeitest das Ticket jetzt.";
        });

    public async Task<IActionResult> OnPostTriageAsync()
        => await RunAsync(() => AiLogic.TriageAsync(biz, Id, HttpContext.Connection.RemoteIpAddress?.ToString()));

    public async Task<IActionResult> OnPostLinkContactAsync()
        => await RunAsync(async () =>
        {
            await TicketLogic.LinkContactAsync(biz, Id, IdOf(ContactId));
            return ContactId is null ? "Die Verknüpfung ist gelöst." : "Der Kontakt ist verknüpft.";
        }, "kunde");

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        try
        {
            await TicketLogic.DeleteAsync(biz, Id);
            Flash("Das Ticket ist gelöscht.");
            return RedirectToPage("/Tickets/Index");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id });
        }
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action, string? tab = null)
    {
        try
        {
            Flash(await action());
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { id = Id, tab });
    }

    private static long? IdOf(string? value) => IdList.Parse(value).Cast<long?>().FirstOrDefault();

    private async Task<bool> LoadAsync()
    {
        var ticket = await TicketLogic.GetAsync(biz, Id);
        if (ticket is null)
        {
            return false;
        }

        Ticket = ticket;
        Answers = TicketLogic.Answers(ticket);
        Status = ticket.Status;
        Priority = ticket.Priority;
        AiAvailable = await AiLogic.IsAvailableAsync(biz);
        if (ticket.Contact is { } contact)
        {
            ContactButtons = await CrmLogic.ButtonsAsync(biz, CrmEntity.Contact, k => CrmLogic.ValueOf(contact, k));
            var address = CrmLogic.ValueOf(contact, "Address");
            MapUrl = string.IsNullOrWhiteSpace(address) ? null : CrmLogic.MapEmbedUrl(address);
        }

        if (ticket.Company is { } company)
        {
            CompanyButtons = await CrmLogic.ButtonsAsync(biz, CrmEntity.Company, k => CrmLogic.ValueOf(company, k));
            var address = CrmLogic.ValueOf(company, "Address");
            MapUrl ??= string.IsNullOrWhiteSpace(address) ? null : CrmLogic.MapEmbedUrl(address);
        }
        else if (ticket.ContactId is null)
        {
            Suggestions = await TicketLogic.ContactSuggestionsAsync(biz, ticket);
        }

        Categories = Controls.Options.From(await CategoryLogic.ChoicesAsync(biz));
        Departments = Controls.Options.From(await OrganizationLogic.DepartmentChoicesAsync(biz));
        Teams = Controls.Options.From(await OrganizationLogic.TeamChoicesAsync(biz));
        Agents = Controls.Options.From(await OrganizationLogic.AgentChoicesAsync(biz));
        Contacts = Controls.Options.From(await CrmLogic.ContactChoicesAsync(biz));

        ViewData["OwnHead"] = true;
        ViewData["Title"] = $"{ticket.Number} · {ticket.Subject}";
        ViewData["Crumbs"] = UiKit.Crumbs.Of(("Tickets", "/Tickets"), (ticket.Number, null));
        ViewData["ActiveHref"] = "/Tickets";
        return true;
    }
}
