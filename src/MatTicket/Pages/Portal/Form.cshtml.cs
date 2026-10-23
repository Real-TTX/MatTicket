using MatTicket.Core.Forms;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Portal;

/// <summary>
/// A published form at /{slug}: one step at a time. The state (step, answers, the way back) travels signed and encrypted in the page; every
/// decision (where to go, whether it is valid, what is created) is made here. ?preview=1 shows the draft to administrators, without a ticket.
/// </summary>
[AllowAnonymous]
public sealed class FormModel(Biz biz, AiRateLimiter limiter) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public string Slug { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public bool Preview { get; set; }

    [BindProperty]
    public string? State { get; set; }

    /// <summary>A field people do not see: a bot that fills it is ignored.</summary>
    [BindProperty]
    public string? Website { get; set; }

    public PublicForm Form { get; private set; } = null!;

    public FormSession Session { get; private set; } = null!;

    public FormStep Step { get; private set; } = null!;

    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public string? Message { get; private set; }

    public bool Finished { get; private set; }

    public Ticket? Ticket { get; private set; }

    public string Token => PortalLogic.Protect(biz, Session);

    /// <summary>The address of the sign-in that comes back to this form: Microsoft directly when it is set up.</summary>
    public string SignInUrl => (EntraLogic.IsUsable(biz.Service<SettingStore>()) ? "/Account/Entra?returnUrl=" : "/Account/Login?returnUrl=") + Uri.EscapeDataString("/" + Slug);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadFormAsync() is { } fail)
        {
            return fail;
        }

        Session = PortalLogic.Start(Form);
        if (Form.Access != FormAccessResult.Allowed)
        {
            Step = Form.Definition.Steps.First();
            return Page();
        }

        Step = Form.Definition.Step(Session.Step) ?? Form.Definition.Steps.First();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string action)
    {
        if (await LoadFormAsync() is { } fail)
        {
            return fail;
        }

        if (Form.Access != FormAccessResult.Allowed)
        {
            return RedirectToPage(new { slug = Slug });
        }

        var session = PortalLogic.Unprotect(biz, State);
        if (session is null)
        {
            Session = PortalLogic.Start(Form);
            Step = Form.Definition.Step(Session.Step)!;
            Message = "Die Sitzung ist abgelaufen. Bitte beginnen Sie noch einmal.";
            return Page();
        }

        if (!string.IsNullOrEmpty(Website))
        {
            return RedirectToPage(new { slug = Slug });
        }

        var current = Form.Definition.Step(session.Step);
        if (current?.Kind == StepKinds.Ticket && action == "next" && !Form.Preview
            && !limiter.TryTake("form:" + HttpContext.Connection.RemoteIpAddress, 10))
        {
            Session = session;
            Step = current;
            Message = "Zu viele Anfragen in kurzer Zeit. Bitte versuchen Sie es in einer Minute noch einmal.";
            return Page();
        }

        var result = await PortalLogic.PostAsync(biz, Form, session, action ?? "next", name => Request.Form[name].Select(v => v ?? "").ToList(), HttpContext.Connection.RemoteIpAddress?.ToString());
        Session = result.Session;
        Errors = result.Errors;
        Message = result.Message ?? (result.Errors.TryGetValue("", out var general) ? general : null);
        Finished = result.Finished;
        Ticket = result.Ticket;
        Step = Form.Definition.Step(Session.Step) ?? Form.Definition.Steps.First();
        return Page();
    }

    /// <summary>The answer of the AI step, fetched by the page once the step is shown (so the visitor sees that something happens).</summary>
    public async Task<IActionResult> OnPostAssistAsync()
    {
        if (await LoadFormAsync() is { } fail)
        {
            return fail;
        }

        if (Form.Access != FormAccessResult.Allowed)
        {
            return new JsonResult(new { error = "Dieses Formular ist nicht für Sie freigegeben." });
        }

        var session = PortalLogic.Unprotect(biz, State);
        if (session is null)
        {
            return new JsonResult(new { error = "Die Sitzung ist abgelaufen. Bitte laden Sie die Seite neu." });
        }

        var (updated, html, error) = await PortalLogic.AssistAsync(biz, Form, session, HttpContext.Connection.RemoteIpAddress?.ToString());
        Session = updated;
        return new JsonResult(new { html, error, state = Token });
    }

    private async Task<IActionResult?> LoadFormAsync()
    {
        if (Preview && !User.IsInRole(nameof(UserRole.Admin)))
        {
            return Challenge();
        }

        var form = await PortalLogic.LoadAsync(biz, Slug, Preview);
        if (form is null || form.Definition.Steps.Count == 0)
        {
            return NotFound();
        }

        Form = form;
        ViewData["Title"] = form.Definition.Title is { Length: > 0 } t ? t : form.Form.Name;
        ViewData["Robots"] = form.Form.Indexable && form.Form.Access == FormAccess.Public && !Preview ? "index, follow" : "noindex, nofollow";
        return null;
    }

    /// <summary>The value a field shows: what was posted (after an error) or what the session holds.</summary>
    public string ValueOf(FormBlock block) => block.Key is { } key && Session.Answers.TryGetValue(key, out var v) ? v : "";

    /// <summary>How many steps the visitor has done; for the bar above the form.</summary>
    public int Done => Session.Path.Count;
}
