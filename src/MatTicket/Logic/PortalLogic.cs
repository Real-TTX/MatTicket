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

/// <summary>A published form as the public page needs it.</summary>
public sealed record PublicForm(TicketForm Form, FormDefinition Definition, int Version, bool Preview, IReadOnlyList<FormOption> Categories);

/// <summary>The outcome of a posted step: the next state, the errors of the step, or the end (with the ticket when one was created).</summary>
public sealed record StepResult(FormSession Session, IReadOnlyDictionary<string, string> Errors, bool Finished, Ticket? Ticket, string? Message);

/// <summary>
/// The public side: running a published form step by step and the status page of a ticket for the customer. Nothing here needs a sign-in;
/// the server decides every step, and the only things a visitor can name are the form (its address) and their ticket (its secret link).
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

    /// <summary>The published form at /{slug}; with <paramref name="preview"/> (administrators only) the draft.</summary>
    public static async Task<PublicForm?> LoadAsync(Biz biz, string slug, bool preview)
    {
        var form = await biz.Db.TicketForm.AsNoTracking().FirstOrDefaultAsync(f => f.Slug == slug.ToLower());
        if (form is null)
        {
            return null;
        }

        var categories = await CategoryLogic.FormOptionsAsync(biz.Db);
        if (preview)
        {
            biz.RequireAdmin();
            return new PublicForm(form, FormEngine.Clean(FormDefinition.Parse(form.DraftJson)), 0, true, categories);
        }

        if (form.State != FormState.Published || form.PublishedVersion == 0)
        {
            return null;
        }

        var version = await biz.Db.TicketFormVersion.AsNoTracking().FirstOrDefaultAsync(v => v.TicketFormId == form.Id && v.Version == form.PublishedVersion);
        return version is null ? null : new PublicForm(form, FormDefinition.Parse(version.DefinitionJson), version.Version, false, categories);
    }

    public static FormSession Start(PublicForm form) => new()
    {
        FormId = form.Form.Id,
        Version = form.Version,
        Preview = form.Preview,
        Step = form.Definition.Start,
    };

    /// <summary>The forms that are published, for the start page of the portal, robots.txt, the sitemap and llms.txt.</summary>
    public static async Task<List<TicketForm>> PublishedAsync(MtDbContext db, bool onlyIndexable)
        => await db.TicketForm.AsNoTracking().Where(f => f.State == FormState.Published && f.PublishedVersion > 0 && (!onlyIndexable || f.Indexable)).OrderBy(f => f.Name).ToListAsync();

    // ================================================================================================ one step

    /// <summary>
    /// Handles what a visitor sent for the current step: "back", "next" (answers of the step), "helped" / "open" (the AI step), "submit" (the
    /// ticket step). Only the answers of the current step are taken from the post; everything else comes from the protected session.
    /// </summary>
    public static async Task<StepResult> PostAsync(Biz biz, PublicForm form, FormSession session, string action, Func<string, IReadOnlyList<string>> posted, string? address)
    {
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
            var stepAnswers = FormEngine.Read(step, posted);
            foreach (var (key, value) in stepAnswers)
            {
                answers[key] = value;
            }

            session.Answers = new Dictionary<string, string>(answers);
            var errors = FormEngine.Validate(step, stepAnswers, form.Categories);
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
        if (step?.Kind != StepKinds.Ai || session.FormId != form.Form.Id)
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

        var result = await AiLogic.AssistAsync(biz.Db, biz.Services, form.Form.Id, form.Definition, step, new FormAnswers(session.Answers), form.Categories, address);
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
        string Mapped(string target) => def.Questions.Where(q => q.MapTo == target).Select(q => FormEngine.Display(q, answers.Get(q.Key!), form.Categories)).FirstOrDefault(v => v.Length > 0) ?? "";

        var list = new List<TicketAnswer>();
        foreach (var block in def.Questions)
        {
            var value = answers.Get(block.Key!);
            if (value.Length > 0)
            {
                list.Add(new TicketAnswer(block.Label ?? block.Key!, block.Key!, FormEngine.Display(block, value, form.Categories)));
            }
        }

        var subject = Mapped("ticket.subject");
        if (subject.Length == 0 && !string.IsNullOrWhiteSpace(step.SubjectTemplate))
        {
            subject = FormEngine.FillTemplate(def, step.SubjectTemplate, answers, form.Categories);
        }

        if (subject.Length == 0)
        {
            subject = $"{form.Form.Name}: Anfrage";
        }

        var descriptionText = def.Questions.Where(q => q.MapTo == "ticket.description").Select(q => answers.Get(q.Key!)).FirstOrDefault(v => v.Length > 0);
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

        var categoryFromAnswer = def.Questions.Where(q => q.Type == BlockTypes.Category)
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

        await TicketLogic.ApplyPlacementAsync(biz.Db, ticket, categoryFromAnswer ?? step.CategoryId ?? form.Form.CategoryId, step.DepartmentId ?? form.Form.DepartmentId, step.TeamId ?? form.Form.TeamId);

        // A known address links the ticket to the contact of the CRM (and its company).
        if (ticket.RequesterEmail.Length > 0)
        {
            var contact = await biz.Db.Contact.AsNoTracking().Where(c => c.Email != null && c.Email.ToLower() == ticket.RequesterEmail).OrderBy(c => c.Id).FirstOrDefaultAsync();
            if (contact is not null)
            {
                ticket.ContactId = contact.Id;
                ticket.CompanyId = contact.CompanyId;
            }
        }

        biz.Db.Ticket.Add(ticket);
        await biz.Db.SaveChangesAsync();
        await TicketLogic.NumberAsync(biz.Db, ticket);
        await TicketLogic.AddSystemAsync(biz.Db, ticket.Id, $"Ticket über das Formular „{form.Form.Name}“ (Version {form.Version}) angelegt.");
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
