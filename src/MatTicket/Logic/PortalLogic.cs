using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MatTicket.Core.Forms;
using MatTicket.Core.Text;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Services.Ai;
using MatTicket.Services.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>Where a visitor is in a form: the step, the answers so far and the way back. Travels signed and encrypted in the page, never trusted otherwise.</summary>
public sealed class FormSession
{
    public long FormId { get; set; }

    public int Version { get; set; }

    public bool Preview { get; set; }

    public string Step { get; set; } = "";

    public List<string> Path { get; set; } = [];

    public Dictionary<string, string> Answers { get; set; } = [];

    public string? AiHtml { get; set; }

    public long? AiRunId { get; set; }

    public bool? AiHelped { get; set; }

    public int Walked { get; set; }
}

/// <summary>Who looks at the portal: nobody (a guest), a requester, or service staff; with the data of the account and its Entra groups.</summary>
public sealed record Viewer(long? UserId, UserRole? Role, string Name, string Email, string? Phone, IReadOnlyList<string> Groups)
{
    public static readonly Viewer Guest = new(null, null, "", "", null, []);

    public bool SignedIn => UserId is not null;

    public bool IsStaff => Role is UserRole.Admin or UserRole.Agent;

    public FormViewer Kind => IsStaff ? FormViewer.Staff : SignedIn ? FormViewer.SignedIn : FormViewer.Guest;

    /// <summary>Whether the account is in one of <paramref name="groups"/>; an empty list means everybody.</summary>
    public bool InAny(IReadOnlyCollection<string> groups) => groups.Count == 0 || Groups.Any(g => groups.Contains(g, StringComparer.OrdinalIgnoreCase));
}

public enum FormAccessResult
{
    Allowed,
    NeedsSignIn,
    Denied,
}

/// <summary>
/// A published form as the public page needs it, for one viewer: whether they may use it, the blocks they see, the categories and customers
/// they may choose, and whether name and address come from their account.
/// </summary>
public sealed record PublicForm(TicketForm Form, FormDefinition Definition, int Version, bool Preview, Viewer Viewer, FormAccessResult Access, FormLists Lists)
{
    /// <summary>Name, address and phone come from the signed-in account (and the form does not ask for them).</summary>
    public bool UsesAccount => Viewer.SignedIn && (Form.AccountData == AccountData.AllSignedIn || (Form.AccountData == AccountData.Requesters && !Viewer.IsStaff));

    /// <summary>Whether the viewer sees (and so answers) a block.</summary>
    public bool Visible(FormBlock block)
        => Visibilities.Sees(block.Visibility, Viewer.Kind)
           && !(UsesAccount && (block.MapTo is "requester.name" or "requester.email" || (block.MapTo == "requester.phone" && !string.IsNullOrWhiteSpace(Viewer.Phone))));
}

/// <summary>The outcome of a posted step: the next state, the errors of the step, or the end (with the ticket when one was created).</summary>
public sealed record StepResult(FormSession Session, IReadOnlyDictionary<string, string> Errors, bool Finished, Ticket? Ticket, string? Message);

/// <summary>
/// The public side: running a published form step by step and the status page of a ticket for the customer. A form decides who may use it
/// (everybody, signed-in accounts, service staff, members of Entra groups); a signed-in requester sends with their account. The server decides
/// every step, and the only things a visitor can name are the form (its address) and their ticket (its secret link).
/// </summary>
public static class PortalLogic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ITimeLimitedDataProtector Protector(Biz biz)
        => biz.Service<IDataProtectionProvider>().CreateProtector("MatTicket.FormSession.v1").ToTimeLimitedDataProtector();

    public static string Protect(Biz biz, FormSession session)
        => Protector(biz).Protect(JsonSerializer.Serialize(session, Json), TimeSpan.FromHours(12));

    public static FormSession? Unprotect(Biz biz, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FormSession>(Protector(biz).Unprotect(token), Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    // ================================================================================================ loading a form

    /// <summary>The person behind the request, with the groups of their account. A guest when nobody is signed in.</summary>
    public static async Task<Viewer> ViewerAsync(Biz biz)
    {
        if (biz.User.UserId is not { } id)
        {
            return Viewer.Guest;
        }

        var user = await biz.Db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive);
        return user is null ? Viewer.Guest : new Viewer(user.Id, user.Role, user.DisplayName, user.Email, user.Phone, Groups(user.EntraGroupsJson));
    }

    /// <summary>A list of Entra group ids as it is stored (jsonb).</summary>
    public static List<string> Groups(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string GroupsToJson(IEnumerable<string> groups)
        => JsonSerializer.Serialize(groups.Select(g => g.Trim().ToLowerInvariant()).Where(g => Guid.TryParse(g, out _)).Distinct().ToList(), Json);

    /// <summary>Whether a viewer may use a form; service staff and administrators may use every form.</summary>
    public static FormAccessResult AccessOf(TicketForm form, Viewer viewer)
    {
        if (form.Access == FormAccess.Public)
        {
            return FormAccessResult.Allowed;
        }

        if (!viewer.SignedIn)
        {
            return FormAccessResult.NeedsSignIn;
        }

        if (form.Access == FormAccess.SignedIn || viewer.IsStaff)
        {
            return FormAccessResult.Allowed;
        }

        var groups = Groups(form.AccessGroupsJson);
        return form.Access == FormAccess.Groups && groups.Count > 0 && viewer.InAny(groups) ? FormAccessResult.Allowed : FormAccessResult.Denied;
    }

    /// <summary>The published form at /{slug} for the person who asks; with <paramref name="preview"/> (administrators only) the draft.</summary>
    public static async Task<PublicForm?> LoadAsync(Biz biz, string slug, bool preview)
    {
        var form = await biz.Db.TicketForm.AsNoTracking().FirstOrDefaultAsync(f => f.Slug == slug.ToLower());
        if (form is null)
        {
            return null;
        }

        var viewer = await ViewerAsync(biz);
        if (preview)
        {
            biz.RequireAdmin();
            return new PublicForm(form, FormEngine.Clean(FormDefinition.Parse(form.DraftJson)), 0, true, viewer, FormAccessResult.Allowed, await ListsAsync(biz, viewer));
        }

        if (form.State != FormState.Published || form.PublishedVersion == 0)
        {
            return null;
        }

        var access = AccessOf(form, viewer);
        var lists = access == FormAccessResult.Allowed ? await ListsAsync(biz, viewer) : FormLists.Empty;
        var version = await biz.Db.TicketFormVersion.AsNoTracking().FirstOrDefaultAsync(v => v.TicketFormId == form.Id && v.Version == form.PublishedVersion);
        return version is null ? null : new PublicForm(form, FormDefinition.Parse(version.DefinitionJson), version.Version, false, viewer, access, lists);
    }

    /// <summary>
    /// What a viewer may choose: the active categories whose groups they are in (a subcategory also needs its parent), and the customers of the
    /// CRM – for service staff only; nobody else ever gets the list, and nobody else can name a customer.
    /// </summary>
    private static async Task<FormLists> ListsAsync(Biz biz, Viewer viewer)
    {
        var all = await biz.Db.Category.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        bool Allowed(Category c) => viewer.IsStaff || viewer.InAny(Groups(c.AccessGroupsJson));
        var categories = new List<FormOption>();
        foreach (var parent in all.Where(c => c.ParentId is null && Allowed(c)))
        {
            categories.Add(new FormOption { Value = parent.Id.ToString(CultureInfo.InvariantCulture), Label = parent.Name });
            categories.AddRange(all.Where(c => c.ParentId == parent.Id && Allowed(c))
                .Select(c => new FormOption { Value = c.Id.ToString(CultureInfo.InvariantCulture), Label = $"{parent.Name} › {c.Name}" }));
        }

        var customers = new List<FormOption>();
        if (viewer.IsStaff)
        {
            var companies = await biz.Db.Company.AsNoTracking().OrderBy(c => c.Name).Take(5000).Select(c => new { c.Id, c.Name, c.CustomerNumber, c.City }).ToListAsync();
            customers.AddRange(companies.Select(c => new FormOption
            {
                Value = c.Id.ToString(CultureInfo.InvariantCulture),
                Label = c.Name + (c.CustomerNumber is null ? "" : $" ({c.CustomerNumber})") + (c.City is null ? "" : $", {c.City}"),
            }));
        }

        return new FormLists(categories, customers);
    }

    public static FormSession Start(PublicForm form) => new()
    {
        FormId = form.Form.Id,
        Version = form.Version,
        Preview = form.Preview,
        Step = form.Definition.Start,
    };

    /// <summary>The forms that are published and open to everybody (and may be found): robots.txt, the sitemap and llms.txt.</summary>
    public static async Task<List<TicketForm>> PublishedAsync(MtDbContext db, bool onlyIndexable)
        => await db.TicketForm.AsNoTracking()
            .Where(f => f.State == FormState.Published && f.PublishedVersion > 0 && f.Access == FormAccess.Public && (!onlyIndexable || f.Indexable))
            .OrderBy(f => f.Name).ToListAsync();

    /// <summary>The forms of the help portal for a viewer: the ones they may use, and (for guests) the ones that need a sign-in, except staff forms.</summary>
    public static async Task<List<(TicketForm Form, FormAccessResult Access)>> PortalFormsAsync(Biz biz, Viewer viewer)
    {
        var forms = await biz.Db.TicketForm.AsNoTracking().Where(f => f.State == FormState.Published && f.PublishedVersion > 0).OrderBy(f => f.Name).ToListAsync();
        return [.. forms.Select(f => (Form: f, Access: AccessOf(f, viewer)))
            .Where(x => x.Access == FormAccessResult.Allowed || (x.Access == FormAccessResult.NeedsSignIn && x.Form.Access != FormAccess.Staff))];
    }

    /// <summary>The requests the signed-in person sent with their account, newest first.</summary>
    public static async Task<List<Ticket>> MyTicketsAsync(Biz biz)
    {
        if (biz.User.UserId is not { } id)
        {
            return [];
        }

        return await biz.Db.Ticket.AsNoTracking().Where(t => t.RequesterUserId == id).OrderByDescending(t => t.LastActivityDate).Take(50).ToListAsync();
    }

    // ================================================================================================ one step

    /// <summary>
    /// Handles what a visitor sent for the current step: "back", "next" (answers of the step), "helped" / "open" (the AI step), "submit" (the
    /// ticket step). Only the answers of the current step are taken from the post; everything else comes from the protected session.
    /// </summary>
    public static async Task<StepResult> PostAsync(Biz biz, PublicForm form, FormSession session, string action, Func<string, IReadOnlyList<string>> posted, string? address)
    {
        if (form.Access != FormAccessResult.Allowed)
        {
            throw new ForbiddenException("Dieses Formular ist nicht für Sie freigegeben.");
        }

        if (session.FormId != form.Form.Id || session.Version != form.Version || session.Preview != form.Preview)
        {
            // The form was published again in between: start over rather than mix two versions.
            return new StepResult(Start(form), new Dictionary<string, string>(), false, null, "Das Formular wurde inzwischen geändert. Bitte beginnen Sie noch einmal.");
        }

        var def = form.Definition;
        var step = def.Step(session.Step) ?? def.Step(def.Start)!;
        var none = new Dictionary<string, string>();

        if (action == "back")
        {
            if (session.Path.Count > 0)
            {
                session.Step = session.Path[^1];
                session.Path.RemoveAt(session.Path.Count - 1);
                if (def.Step(session.Step)?.Kind == StepKinds.Ai)
                {
                    session.AiHelped = null;
                }
            }

            return new StepResult(session, none, false, null, null);
        }

        var answers = new FormAnswers(session.Answers);
        if (step.Kind == StepKinds.Ai)
        {
            if (action is not ("helped" or "open"))
            {
                return new StepResult(session, none, false, null, null);
            }

            session.AiHelped = action == "helped";
            answers[FormEngine.AiOutcomeKey] = action == "helped" ? "solved" : "open";
        }
        else if (step.Kind == StepKinds.End)
        {
            return new StepResult(session, none, true, null, null);
        }
        else
        {
            var stepAnswers = FormEngine.Read(step, posted, form.Visible);
            foreach (var (key, value) in stepAnswers)
            {
                answers[key] = value;
            }

            session.Answers = new Dictionary<string, string>(answers);
            var errors = FormEngine.Validate(step, stepAnswers, form.Lists, form.Visible);
            if (errors.Count > 0)
            {
                return new StepResult(session, errors, false, null, null);
            }

            if (step.Kind == StepKinds.Ticket)
            {
                if (form.Preview)
                {
                    return new StepResult(session, none, true, null, "Vorschau: hier würde jetzt das Ticket angelegt.");
                }

                var ticket = await CreateTicketAsync(biz, form, session, answers);
                return new StepResult(session, none, true, ticket, null);
            }
        }

        session.Answers = new Dictionary<string, string>(answers);
        var next = FormEngine.Next(step, answers);
        if (next is null || def.Step(next) is null)
        {
            return new StepResult(session, new Dictionary<string, string> { [""] = "Hier geht es nicht weiter. Bitte wenden Sie sich direkt an uns." }, false, null, null);
        }

        if (++session.Walked > FormEngine.MaxSteps)
        {
            return new StepResult(session, new Dictionary<string, string> { [""] = "Das Formular dreht sich im Kreis. Bitte wenden Sie sich direkt an uns." }, false, null, null);
        }

        session.Path.Add(step.Id);
        session.Step = next;
        if (def.Step(next)!.Kind == StepKinds.Ai)
        {
            session.AiHtml = null;
            session.AiRunId = null;
            session.AiHelped = null;
        }

        return new StepResult(session, none, def.Step(next)!.Kind == StepKinds.End, null, null);
    }

    /// <summary>Asks the AI for the answer of the current AI step (the page fetches it after it is shown, so the visitor sees that something happens).</summary>
    public static async Task<(FormSession Session, string? Html, string? Error)> AssistAsync(Biz biz, PublicForm form, FormSession session, string? address)
    {
        var step = form.Definition.Step(session.Step);
        if (step?.Kind != StepKinds.Ai || session.FormId != form.Form.Id || form.Access != FormAccessResult.Allowed)
        {
            return (session, null, "Kein KI-Schritt.");
        }

        if (session.AiHtml is not null)
        {
            return (session, session.AiHtml, null);
        }

        if (form.Preview)
        {
            biz.RequireAdmin();
        }

        var result = await AiLogic.AssistAsync(biz.Db, biz.Services, form.Form.Id, form.Definition, step, new FormAnswers(session.Answers), form.Lists, address);
        session.AiRunId = result.RunId;
        if (result.Html is not null)
        {
            session.AiHtml = result.Html;
        }

        return (session, result.Html, result.Error);
    }

    // ================================================================================================ the ticket of a form

    private static async Task<Ticket> CreateTicketAsync(Biz biz, PublicForm form, FormSession session, FormAnswers answers)
    {
        var def = form.Definition;
        var step = def.Step(session.Step)!;
        string Mapped(string target) => def.Questions.Where(q => q.MapTo == target && form.Visible(q)).Select(q => FormEngine.Display(q, answers.Get(q.Key!), form.Lists)).FirstOrDefault(v => v.Length > 0) ?? "";

        var list = new List<TicketAnswer>();
        foreach (var block in def.Questions.Where(form.Visible))
        {
            var value = answers.Get(block.Key!);
            if (value.Length > 0)
            {
                list.Add(new TicketAnswer(block.Label ?? block.Key!, block.Key!, FormEngine.Display(block, value, form.Lists)));
            }
        }

        var subject = Mapped("ticket.subject");
        if (subject.Length == 0 && !string.IsNullOrWhiteSpace(step.SubjectTemplate))
        {
            subject = FormEngine.FillTemplate(def, step.SubjectTemplate, answers, form.Lists);
        }

        if (subject.Length == 0)
        {
            subject = $"{form.Form.Name}: Anfrage";
        }

        var descriptionText = def.Questions.Where(q => q.MapTo == "ticket.description" && form.Visible(q)).Select(q => answers.Get(q.Key!)).FirstOrDefault(v => v.Length > 0);
        var description = descriptionText is not null
            ? HtmlSafe.FromText(descriptionText)
            : "<ul>" + string.Concat(list.Select(a => $"<li><strong>{System.Net.WebUtility.HtmlEncode(a.Label)}:</strong> {System.Net.WebUtility.HtmlEncode(a.Value)}</li>")) + "</ul>";

        var priorityText = Mapped("ticket.priority");
        var priority = Enum.TryParse<TicketPriority>(step.Priority, out var stepPriority) ? stepPriority : TicketPriority.Normal;
        priority = priorityText.ToLowerInvariant() switch
        {
            "niedrig" or "low" => TicketPriority.Low,
            "hoch" or "high" => TicketPriority.High,
            "dringend" or "urgent" or "kritisch" => TicketPriority.Urgent,
            "normal" or "mittel" => TicketPriority.Normal,
            _ => priority,
        };

        var categoryFromAnswer = def.Questions.Where(q => q.Type == BlockTypes.Category && form.Visible(q))
            .Select(q => long.TryParse(answers.Get(q.Key!), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
            .FirstOrDefault(id => id is not null);

        var email = Mapped("requester.email");
        var ticket = new Ticket
        {
            Subject = TicketLogic.Limit(subject, 300),
            Description = description,
            Priority = priority,
            Source = TicketSource.Form,
            Status = TicketStatus.New,
            TicketFormId = form.Form.Id,
            FormVersion = form.Version,
            AnswersJson = TicketLogic.AnswersToJson(list),
            RequesterName = TicketLogic.Limit(Mapped("requester.name"), 200),
            RequesterEmail = TicketLogic.Limit(FormEngine.IsEmail(email) ? email.Trim().ToLowerInvariant() : "", 254),
            RequesterPhone = OrganizationLogic.Clean(Mapped("requester.phone")),
            RequesterCompany = OrganizationLogic.Clean(Mapped("requester.company")),
            LastActivityDate = biz.Now,
        };

        // A signed-in requester sends with their account: name and address come from it (the form did not even ask), and the ticket is theirs.
        var viewer = form.Viewer;
        if (form.UsesAccount)
        {
            ticket.RequesterName = TicketLogic.Limit(viewer.Name, 200);
            ticket.RequesterEmail = FormEngine.IsEmail(viewer.Email) ? viewer.Email : "";
            ticket.RequesterPhone = string.IsNullOrWhiteSpace(viewer.Phone) ? ticket.RequesterPhone : viewer.Phone;
        }

        if (viewer.SignedIn && (form.UsesAccount || !viewer.IsStaff))
        {
            ticket.RequesterUserId = viewer.UserId;
        }

        await TicketLogic.ApplyPlacementAsync(biz.Db, ticket, categoryFromAnswer ?? step.CategoryId ?? form.Form.CategoryId, step.DepartmentId ?? form.Form.DepartmentId, step.TeamId ?? form.Form.TeamId);

        // The customer a member of the service chose: the list exists only for them, and the answer was checked against it.
        var customerId = viewer.IsStaff
            ? def.Questions.Where(q => q.Type == BlockTypes.Customer && form.Visible(q))
                .Select(q => long.TryParse(answers.Get(q.Key!), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
                .FirstOrDefault(id => id is not null)
            : null;
        if (customerId is { } company && await biz.Db.Company.AnyAsync(c => c.Id == company))
        {
            ticket.CompanyId = company;
        }

        // A known address links the ticket to the contact of the CRM (and its company, unless a customer was chosen).
        if (ticket.RequesterEmail.Length > 0)
        {
            var known = ticket.RequesterEmail.ToLowerInvariant();
            var contact = await biz.Db.Contact.AsNoTracking()
                .Where(c => c.Email != null && c.Email.ToLower() == known && (ticket.CompanyId == null || c.CompanyId == ticket.CompanyId))
                .OrderBy(c => c.Id).FirstOrDefaultAsync();
            if (contact is not null)
            {
                ticket.ContactId = contact.Id;
                ticket.CompanyId ??= contact.CompanyId;
            }
        }

        biz.Db.Ticket.Add(ticket);
        await biz.Db.SaveChangesAsync();
        await TicketLogic.NumberAsync(biz.Db, ticket);
        var origin = ticket.RequesterUserId is not null ? $", angemeldet als {viewer.Name} ({viewer.Email})" : viewer.IsStaff ? $", erfasst von {viewer.Name}" : "";
        await TicketLogic.AddSystemAsync(biz.Db, ticket.Id, $"Ticket über das Formular „{form.Form.Name}“ (Version {form.Version}) angelegt{origin}.");
        if (session.AiHtml is not null)
        {
            await TicketLogic.AddSystemAsync(biz.Db, ticket.Id, $"Im Formular hat die KI geantwortet (Lauf #{session.AiRunId}); der Kunde fand das {(session.AiHelped == true ? "hilfreich" : "nicht ausreichend")}:\n\n{HtmlSafe.ToText(session.AiHtml)}");
        }

        biz.Service<MailQueue>().TicketCreated(ticket.Id, toCustomer: true);
        biz.Service<AiQueue>().Enqueue(new AiTicketJob(ticket.Id, form.Form.AiTriage, form.Form.AiAutoReply));
        await biz.Log.LogAsync("Ticket über Formular", nameof(Ticket), ticket.Id, ticket.Number);
        return ticket;
    }

    // ================================================================================================ the status page of the customer

    public sealed record CustomerView(Ticket Ticket, IReadOnlyList<TicketMessage> Messages);

    /// <summary>The ticket of the secret link, with what the customer may see (never internal notes or the history of the team).</summary>
    public static async Task<CustomerView?> CustomerViewAsync(MtDbContext db, Guid token)
    {
        var ticket = await db.Ticket.AsNoTracking().Include(t => t.Department).FirstOrDefaultAsync(t => t.Token == token);
        if (ticket is null)
        {
            return null;
        }

        var messages = await db.TicketMessage.AsNoTracking()
            .Where(m => m.TicketId == ticket.Id && (m.Kind == MessageKind.Customer || m.Kind == MessageKind.Reply || m.Kind == MessageKind.Ai))
            .OrderBy(m => m.CreateDate).ToListAsync();
        return new CustomerView(ticket, messages);
    }

    /// <summary>The customer writes on their ticket: it opens again when it was waiting for them or solved.</summary>
    public static async Task CustomerReplyAsync(Biz biz, Guid token, string text)
    {
        var ticket = await biz.Db.Ticket.FirstOrDefaultAsync(t => t.Token == token) ?? throw new ForbiddenException("Dieses Ticket gibt es nicht.");
        if (ticket.Status == TicketStatus.Closed)
        {
            throw new BizException("Dieses Ticket ist geschlossen. Bitte stellen Sie eine neue Anfrage.");
        }

        var body = HtmlSafe.FromText(TicketLogic.Limit(text, 8000));
        if (body.Length == 0)
        {
            throw new BizException("Bitte schreiben Sie eine Nachricht.", "Text");
        }

        biz.Db.TicketMessage.Add(new TicketMessage { TicketId = ticket.Id, Kind = MessageKind.Customer, Body = body, AuthorName = ticket.RequesterName.Length > 0 ? ticket.RequesterName : "Kunde" });
        if (ticket.Status is TicketStatus.Waiting or TicketStatus.Resolved)
        {
            ticket.Status = TicketStatus.Open;
            ticket.ResolvedDate = null;
        }

        ticket.LastActivityDate = biz.Now;
        await biz.Db.SaveChangesAsync();
        biz.Service<MailQueue>().CustomerWrote(ticket.Id);
    }

    /// <summary>The customer says the problem is solved.</summary>
    public static async Task CustomerResolveAsync(Biz biz, Guid token)
    {
        var ticket = await biz.Db.Ticket.FirstOrDefaultAsync(t => t.Token == token) ?? throw new ForbiddenException("Dieses Ticket gibt es nicht.");
        if (ticket.Status is TicketStatus.Resolved or TicketStatus.Closed)
        {
            return;
        }

        ticket.Status = TicketStatus.Resolved;
        ticket.ResolvedDate = biz.Now;
        ticket.LastActivityDate = biz.Now;
        await biz.Db.SaveChangesAsync();
        await TicketLogic.AddSystemAsync(biz.Db, ticket.Id, "Der Kunde hat das Ticket als gelöst markiert.");
    }
}
