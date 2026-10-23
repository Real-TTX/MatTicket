using System.Globalization;
using System.Text;
using System.Text.Json;
using MatTicket.Configuration;
using MatTicket.Core.Ai;
using MatTicket.Core.Forms;
using MatTicket.Core.Text;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>
/// What the AI does in MatTicket, and the boundary around it. Every call goes through <see cref="RunAsync"/>: limits first (before anything is
/// sent), then the transport (the active profile, else the operator's relay), the layered instructions, one JSON object as the answer, read
/// leniently and then checked in code (allowlists for choices, safe HTML, no scripts). Every step is written to the run; changes go into the
/// journal and can be taken back, the newest run first. Which data the AI sees is decided here, never by the model.
/// </summary>
public static class AiLogic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The outcome of a run for its caller: the run (with its status), the checked JSON object, or what went wrong.</summary>
    public sealed record Outcome(AiRun Run, JsonElement? Result, string? Error)
    {
        public bool Ok => Error is null && Result is not null;
    }

    private sealed record Call(AiAction Action, string Situation, string? StepInstruction, long? TicketId, long? FormId, long? UserId, string? VisitorHash, string? Address);

    // ================================================================================================ the run

    private static async Task<Outcome> RunAsync(MtDbContext db, IServiceProvider services, Call call, CancellationToken ct = default)
    {
        var store = services.GetRequiredService<SettingStore>();
        var settings = store.Get<AiSettings>(SettingStore.Ai);
        var site = store.Get<SiteSettings>(SettingStore.Site);
        var config = services.GetRequiredService<ConfigStore>();
        var clock = services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;
        var run = new AiRun
        {
            Action = call.Action,
            TicketId = call.TicketId,
            TicketFormId = call.FormId,
            UserId = call.UserId,
            VisitorHash = call.VisitorHash,
            StartDate = now,
            CreateUserId = call.UserId,
        };
        db.AiRun.Add(run);
        var stepNo = 0;
        void Step(AiStepKind kind, string text) => run.Steps.Add(new AiRunStep { Kind = kind, Text = Limit(text, 40_000), Number = ++stepNo, OccurredDate = clock.GetUtcNow().UtcDateTime, CreateUserId = call.UserId });

        async Task<Outcome> End(AiRunStatus status, string? error, JsonElement? result = null)
        {
            run.Status = status;
            run.Error = error;
            run.EndDate = clock.GetUtcNow().UtcDateTime;
            if (error is not null)
            {
                Step(status == AiRunStatus.LimitReached ? AiStepKind.Warning : AiStepKind.Result, error);
            }

            await db.SaveChangesAsync(ct);
            return new Outcome(run, result, error);
        }

        Step(AiStepKind.Action, $"Aktion: {UiKit.Labels.Of(call.Action)}");

        // ---- limits, before anything is sent ----
        if (!settings.Enabled)
        {
            return await End(AiRunStatus.Cancelled, "Die KI ist in den Einstellungen ausgeschaltet.");
        }

        if (!services.GetRequiredService<AiRateLimiter>().TryTake(call.Address, settings.PerMinutePerAddress))
        {
            return await End(AiRunStatus.LimitReached, "Zu viele Anfragen in kurzer Zeit. Bitte in einer Minute erneut versuchen.");
        }

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthTokens = await db.AiRun.Where(r => r.StartDate >= monthStart).SumAsync(r => r.PromptTokens + r.CompletionTokens, ct);
        if (settings.MonthlyTokens > 0 && monthTokens >= settings.MonthlyTokens)
        {
            return await End(AiRunStatus.LimitReached, "Das Monatsbudget der KI ist aufgebraucht.");
        }

        if (call.UserId is { } userId && settings.UserDailyTokens > 0)
        {
            var dayTokens = await db.AiRun.Where(r => r.UserId == userId && r.StartDate >= now.Date).SumAsync(r => r.PromptTokens + r.CompletionTokens, ct);
            if (dayTokens >= settings.UserDailyTokens)
            {
                return await End(AiRunStatus.LimitReached, "Dein Tagesbudget der KI ist aufgebraucht. Morgen geht es weiter.");
            }
        }

        if (call.VisitorHash is { } visitor && settings.VisitorDailyRuns > 0
            && await db.AiRun.CountAsync(r => r.VisitorHash == visitor && r.StartDate >= now.Date, ct) >= settings.VisitorDailyRuns)
        {
            return await End(AiRunStatus.LimitReached, "Für heute sind keine weiteren automatischen Antworten möglich.");
        }

        // ---- transport ----
        var transport = await TransportAsync(db, services, config, ct);
        if (transport is null)
        {
            return await End(AiRunStatus.Error, "Es ist keine KI eingerichtet (Einstellungen → KI → Profile).");
        }

        run.ProfileName = transport.Name;
        run.Model = transport.Model;
        run.Transport = transport.Kind;

        // ---- instructions ----
        var system = AiInstructions.SystemText(config.Current.Ai, settings, site, call.Action, call.StepInstruction);
        var messages = new List<AiMessage> { AiMessage.System(system), AiMessage.User(call.Situation) };
        Step(AiStepKind.Message, $"An {transport.Name} ({transport.Model}):\n\n{system}\n\n---\n\n{call.Situation}");

        var required = AiInstructions.RequiredField(call.Action);
        for (var attempt = 0; ; attempt++)
        {
            var response = await transport.CompleteAsync(new AiRequest { Messages = messages, Json = true, MaxTokens = settings.MaxTokens }, ct);
            var estimated = response.PromptTokens == 0 && response.CompletionTokens == 0;
            run.PromptTokens += estimated ? TokenEstimate.Of(messages) : response.PromptTokens;
            run.CompletionTokens += estimated ? TokenEstimate.Of(response.Text) : response.CompletionTokens;
            run.TokensEstimated |= estimated;
            if (!response.Ok)
            {
                return await End(AiRunStatus.Error, response.Error ?? "Die KI hat nicht geantwortet.");
            }

            Step(AiStepKind.Result, response.Text ?? "");
            if (response.Note is { Length: > 0 } note)
            {
                Step(AiStepKind.Warning, note);
            }

            var obj = LenientJson.FirstObject(response.Text);
            if (obj is { } o && o.TryGetProperty(required, out _))
            {
                run.ResultJson = o.GetRawText();
                return await End(AiRunStatus.Done, null, o);
            }

            // Only an announcement ("Ich schaue mir das an ...") or another shape: at most two nudges, then the run hangs.
            if (attempt >= 2)
            {
                return await End(AiRunStatus.Stuck, "Die KI hat nicht im verlangten Format geantwortet (zweimal angestoßen).");
            }

            run.Nudges++;
            messages.Add(AiMessage.Assistant(response.Text ?? ""));
            messages.Add(AiMessage.User(AiInstructions.Nudge(call.Action)));
            Step(AiStepKind.Warning, "Antwort ohne das verlangte JSON-Objekt: Anstoß zum Weitermachen.");
        }
    }

    // ================================================================================================ transport: own connection, else relay

    private sealed record Transport(string Kind, string Name, string Model, Func<AiRequest, CancellationToken, Task<AiResponse>> Complete)
    {
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct) => Complete(request, ct);
    }

    private static async Task<Transport?> TransportAsync(MtDbContext db, IServiceProvider services, ConfigStore config, CancellationToken ct)
    {
        var secrets = services.GetRequiredService<AiSecretStore>();
        await AiProfileLogic.EnsureLegacyAsync(db, secrets, config);
        var http = services.GetRequiredService<IHttpClientFactory>();
        var settings = services.GetRequiredService<SettingStore>().Get<AiSettings>(SettingStore.Ai);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Ai");
        var profile = await db.AiProfile.AsNoTracking().FirstOrDefaultAsync(p => p.IsActive, ct);
        if (profile is not null)
        {
            if (profile.Provider == AiProvider.ChatGpt)
            {
                var account = AiProfileLogic.Account(services, profile);
                if (await account.IsConnectedAsync(ct))
                {
                    return new Transport("profile", profile.Name, profile.Model, (r, c) => account.CompleteAsync(profile.Model, r.Messages, c));
                }
            }
            else if (await secrets.LoadAsync(profile.SecretToken, AiSecretStore.ApiKey, ct) is { Length: > 0 } key)
            {
                return new Transport("profile", profile.Name, profile.Model, (r, c) =>
                    ChatCompletionsClient.CompleteAsync(http.CreateClient("ai"), profile.BaseUrl, key, profile.Model, r, settings.MaxTokens, m => logger.LogWarning("{Message}", m), c));
            }
        }

        var op = config.Current.Ai;
        if (!string.IsNullOrWhiteSpace(op.RelayUrl))
        {
            return new Transport("relay", "Relay des Betreibers", op.RelayModel, (r, c) =>
                ChatCompletionsClient.CompleteAsync(http.CreateClient("ai"), op.RelayUrl, op.RelayKey, op.RelayModel, r, settings.MaxTokens, m => logger.LogWarning("{Message}", m), c));
        }

        return null;
    }

    /// <summary>Whether a call could go out at all (a usable profile or a relay): pages hide the AI buttons otherwise.</summary>
    public static async Task<bool> IsAvailableAsync(Biz biz)
    {
        var settings = SettingLogic.Get<AiSettings>(biz, SettingStore.Ai);
        if (!settings.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(biz.Config.Current.Ai.RelayUrl))
        {
            return true;
        }

        var profile = await biz.Db.AiProfile.AsNoTracking().FirstOrDefaultAsync(p => p.IsActive);
        return profile is not null && biz.Service<AiSecretStore>().Has(profile.SecretToken, profile.Provider == AiProvider.ChatGpt ? AiSecretStore.ChatGpt : AiSecretStore.ApiKey);
    }

    // ================================================================================================ the situation (what the model sees)

    private static async Task<string> TicketSituationAsync(MtDbContext db, SettingStore store, long ticketId, bool withNotes, bool forTriage)
    {
        var settings = store.Get<AiSettings>(SettingStore.Ai);
        var t = await db.Ticket.AsNoTracking().Include(x => x.Category).Include(x => x.Department).Include(x => x.Team)
            .Include(x => x.Messages.OrderBy(m => m.CreateDate)).AsSplitQuery().FirstAsync(x => x.Id == ticketId);
        var names = new[] { t.RequesterName, t.RequesterCompany ?? "" };
        string P(string? text) => settings.SendPersonalData ? text ?? "" : Pii.Mask(text, names);

        var sb = new StringBuilder();
        sb.AppendLine($"Ticket {t.Number}");
        sb.AppendLine($"Betreff: {P(t.Subject)}");
        sb.AppendLine($"Status: {UiKit.Labels.Of(t.Status)}; Priorität: {UiKit.Labels.Of(t.Priority)}");
        sb.AppendLine($"Kategorie: {t.Category?.Name ?? "–"}; Abteilung: {t.Department?.Name ?? "–"}; Team: {t.Team?.Name ?? "–"}");
        if (settings.SendPersonalData)
        {
            sb.AppendLine($"Kunde: {t.RequesterName}{(string.IsNullOrEmpty(t.RequesterCompany) ? "" : ", " + t.RequesterCompany)}");
        }
        else
        {
            sb.AppendLine("Kunde: [Name]");
        }

        var answers = TicketLogic.Answers(t).Where(a => !string.IsNullOrWhiteSpace(a.Value)).ToList();
        if (answers.Count > 0)
        {
            sb.AppendLine().AppendLine("Angaben im Formular:");
            foreach (var a in answers.Take(40))
            {
                sb.AppendLine($"- {a.Label}: {P(Limit(a.Value, 1500))}");
            }
        }

        sb.AppendLine().AppendLine("Beschreibung:").AppendLine(P(Limit(HtmlSafe.ToText(t.Description), 6000)));
        var conversation = t.Messages.Where(m => m.Kind != MessageKind.System && (withNotes || m.IsPublic)).TakeLast(20).ToList();
        if (conversation.Count > 0)
        {
            sb.AppendLine().AppendLine("Verlauf (älteste zuerst):");
            foreach (var m in conversation)
            {
                var who = m.Kind switch
                {
                    MessageKind.Customer => "Kunde",
                    MessageKind.Note => "Interne Notiz",
                    MessageKind.Ai => "KI-Antwort",
                    _ => "Service",
                };
                sb.AppendLine($"[{who}] {P(Limit(HtmlSafe.ToText(m.Body), 2500))}");
            }
        }

        if (forTriage)
        {
            var categories = await CategoryLogic.LoadChoicesAsync(db, onlyActive: true);
            var departments = await db.Department.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name, d.Description }).ToListAsync();
            var teams = await db.Team.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.DepartmentId, x.Description }).ToListAsync();
            sb.AppendLine().AppendLine("Kategorien (id: Name):");
            foreach (var c in categories)
            {
                sb.AppendLine($"{c.Id}: {c.Name}{(string.IsNullOrEmpty(c.Detail) ? "" : " – " + c.Detail)}");
            }

            sb.AppendLine().AppendLine("Abteilungen (id: Name):");
            foreach (var d in departments)
            {
                sb.AppendLine($"{d.Id}: {d.Name}{(string.IsNullOrEmpty(d.Description) ? "" : " – " + d.Description)}");
            }

            sb.AppendLine().AppendLine("Teams (id: Name, Abteilung):");
            foreach (var x in teams)
            {
                sb.AppendLine($"{x.Id}: {x.Name}, Abteilung {x.DepartmentId}{(string.IsNullOrEmpty(x.Description) ? "" : " – " + x.Description)}");
            }
        }

        return sb.ToString().Trim();
    }

    // ================================================================================================ actions for agents

    public sealed record Suggestion(long RunId, AiRunStatus Status, string? Html, string? Note, string? Error);

    /// <summary>A suggested reply for the agent. Nothing is sent: the text goes into the composer, the agent decides.</summary>
    public static async Task<Suggestion> SuggestReplyAsync(Biz biz, long ticketId, string? address)
    {
        if (await TicketLogic.GetAsync(biz, ticketId) is null)
        {
            throw new ForbiddenException("Dieses Ticket gibt es nicht, oder es ist nicht für dich.");
        }

        var situation = await TicketSituationAsync(biz.Db, biz.Service<SettingStore>(), ticketId, withNotes: true, forTriage: false);
        var outcome = await RunAsync(biz.Db, biz.Services, new Call(AiAction.Reply, situation, null, ticketId, null, biz.UserId, null, address));
        if (!outcome.Ok)
        {
            return new Suggestion(outcome.Run.Id, outcome.Run.Status, null, null, outcome.Error);
        }

        var raw = outcome.Result!.Value.Text("reply") ?? "";
        if (HtmlSafe.ContainsScript(raw))
        {
            await RejectAsync(biz.Db, outcome.Run, "Die Antwort der KI enthielt ein Skript und wurde verworfen.");
            return new Suggestion(outcome.Run.Id, AiRunStatus.Error, null, null, "Die Antwort der KI enthielt ein Skript und wurde verworfen.");
        }

        var html = HtmlSafe.Clean(raw);
        if (html.Length == 0)
        {
            await RejectAsync(biz.Db, outcome.Run, "Die KI hat eine leere Antwort geliefert.");
            return new Suggestion(outcome.Run.Id, AiRunStatus.Error, null, null, "Die KI hat eine leere Antwort geliefert.");
        }

        return new Suggestion(outcome.Run.Id, AiRunStatus.Done, html, outcome.Result.Value.Text("note"), null);
    }

    /// <summary>Lets the AI sort a ticket (category, priority, department, team). Applied at once, written into the journal, undoable.</summary>
    public static async Task<string> TriageAsync(Biz biz, long ticketId, string? address)
    {
        if (await TicketLogic.GetAsync(biz, ticketId) is null)
        {
            throw new ForbiddenException("Dieses Ticket gibt es nicht, oder es ist nicht für dich.");
        }

        return await TriageInternalAsync(biz.Db, biz.Services, ticketId, biz.UserId, address);
    }

    internal static async Task<string> TriageInternalAsync(MtDbContext db, IServiceProvider services, long ticketId, long? userId, string? address)
    {
        var situation = await TicketSituationAsync(db, services.GetRequiredService<SettingStore>(), ticketId, withNotes: false, forTriage: true);
        var outcome = await RunAsync(db, services, new Call(AiAction.Triage, situation, null, ticketId, null, userId, null, address));
        if (!outcome.Ok)
        {
            return outcome.Error!;
        }

        var o = outcome.Result!.Value;
        var ticket = await db.Ticket.FirstAsync(t => t.Id == ticketId);
        var run = outcome.Run;
        var changes = new List<string>();

        // The boundary is here: only ids of the lists, only the four priorities. Anything else is dropped with a warning.
        var categoryId = o.Long("categoryId");
        if (categoryId is { } cid && !await db.Category.AnyAsync(c => c.Id == cid && c.IsActive))
        {
            Warn(run, $"Kategorie {cid} gibt es nicht: ignoriert.");
            categoryId = null;
        }

        var departmentId = o.Long("departmentId");
        if (departmentId is { } did && !await db.Department.AnyAsync(d => d.Id == did))
        {
            Warn(run, $"Abteilung {did} gibt es nicht: ignoriert.");
            departmentId = null;
        }

        var teamId = o.Long("teamId");
        if (teamId is { } tid && !await db.Team.AnyAsync(x => x.Id == tid && (departmentId == null || x.DepartmentId == departmentId)))
        {
            Warn(run, $"Team {tid} passt nicht zur Abteilung: ignoriert.");
            teamId = null;
        }

        var priority = Enum.TryParse<TicketPriority>(o.Text("priority"), ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : (TicketPriority?)null;
        if (priority is null)
        {
            Warn(run, $"Priorität „{o.Text("priority")}“ gibt es nicht: ignoriert.");
        }

        void Change(string field, string? oldValue, string? newValue, string text)
        {
            if (oldValue == newValue)
            {
                return;
            }

            db.AiJournalEntry.Add(new AiJournalEntry { AiRunId = run.Id, EntityType = nameof(Ticket), EntityId = ticketId, Field = field, OldValue = oldValue, NewValue = newValue });
            changes.Add(text);
        }

        if (categoryId is not null)
        {
            Change(nameof(Ticket.CategoryId), Str(ticket.CategoryId), Str(categoryId), "Kategorie");
            ticket.CategoryId = categoryId;
        }

        if (priority is { } prio)
        {
            Change(nameof(Ticket.Priority), ticket.Priority.ToString(), prio.ToString(), "Priorität");
            ticket.Priority = prio;
        }

        if (departmentId is not null)
        {
            Change(nameof(Ticket.DepartmentId), Str(ticket.DepartmentId), Str(departmentId), "Abteilung");
            ticket.DepartmentId = departmentId;
            if (teamId is null && ticket.TeamId is { } current && !await db.Team.AnyAsync(x => x.Id == current && x.DepartmentId == departmentId))
            {
                Change(nameof(Ticket.TeamId), Str(ticket.TeamId), null, "Team");
                ticket.TeamId = null;
            }
        }

        if (teamId is not null)
        {
            Change(nameof(Ticket.TeamId), Str(ticket.TeamId), Str(teamId), "Team");
            ticket.TeamId = teamId;
        }

        Steps(run).Add(new AiRunStep { Kind = AiStepKind.Action, Number = Steps(run).Count + 1, OccurredDate = DateTime.UtcNow, Text = changes.Count == 0 ? "Nichts geändert." : "Geändert: " + string.Join(", ", changes) });
        await db.SaveChangesAsync();
        var reason = o.Text("reason");
        if (changes.Count > 0)
        {
            await TicketLogic.AddSystemAsync(db, ticketId, $"Die KI hat das Ticket eingeordnet ({string.Join(", ", changes)}). {reason}".Trim());
        }

        return changes.Count == 0 ? "Die KI sieht keinen Grund, etwas zu ändern." : $"Eingeordnet: {string.Join(", ", changes)}. {reason}".Trim();
    }

    /// <summary>The automatic first answer of a new ticket (only when the form asks for it and the AI is sure). Marked as AI, undoable.</summary>
    internal static async Task AutoReplyInternalAsync(MtDbContext db, IServiceProvider services, long ticketId)
    {
        var situation = await TicketSituationAsync(db, services.GetRequiredService<SettingStore>(), ticketId, withNotes: false, forTriage: false);
        var outcome = await RunAsync(db, services, new Call(AiAction.AutoReply, situation, null, ticketId, null, null, null, "system"));
        if (!outcome.Ok)
        {
            return;
        }

        var o = outcome.Result!.Value;
        var raw = o.Text("reply") ?? "";
        if (o.Bool("send") != true)
        {
            Warn(outcome.Run, "Die KI war sich nicht sicher und hat nichts gesendet. " + o.Text("reason"));
            await db.SaveChangesAsync();
            return;
        }

        if (HtmlSafe.ContainsScript(raw) || HtmlSafe.Clean(raw) is not { Length: > 0 } html)
        {
            await RejectAsync(db, outcome.Run, "Die Antwort der KI enthielt ein Skript oder war leer und wurde verworfen.");
            return;
        }

        var message = new TicketMessage
        {
            TicketId = ticketId,
            Kind = MessageKind.Ai,
            Body = html,
            AuthorName = "KI-Assistent",
            AiRunId = outcome.Run.Id,
        };
        db.TicketMessage.Add(message);
        await db.SaveChangesAsync();
        db.AiJournalEntry.Add(new AiJournalEntry { AiRunId = outcome.Run.Id, EntityType = nameof(TicketMessage), EntityId = message.Id, Field = "Created", NewValue = "1" });
        await db.SaveChangesAsync();
        services.GetRequiredService<Services.Mail.MailQueue>().Replied(ticketId, message.Id);
    }

    // ================================================================================================ the assistant of a public form

    public sealed record Assist(long? RunId, string? Html, bool Confident, string? Error);

    /// <summary>
    /// The AI step of a published form. It sees only what the visitor entered in this form (no customer data: there is no field to name one),
    /// with personal data masked unless the administrator allowed it, and its answer is cleaned before anybody sees it.
    /// </summary>
    internal static async Task<Assist> AssistAsync(MtDbContext db, IServiceProvider services, long formId, FormDefinition definition, FormStep step, FormAnswers answers, FormLists lists, string? address)
    {
        var store = services.GetRequiredService<SettingStore>();
        var settings = store.Get<AiSettings>(SettingStore.Ai);
        if (!settings.Enabled || !settings.PublicAssistant)
        {
            return new Assist(null, null, false, "Der KI-Assistent ist ausgeschaltet.");
        }

        var personal = definition.Questions.Where(q => q.MapTo is "requester.name" or "requester.company").Select(q => answers.Get(q.Key!)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Formular: {definition.Title}");
        sb.AppendLine("Angaben des Besuchers:");
        foreach (var block in definition.Questions)
        {
            var value = FormEngine.Display(block, answers.Get(block.Key!), lists);
            if (value.Length == 0 || block.Type == BlockTypes.Customer || block.MapTo is "requester.email" or "requester.phone" or "requester.name")
            {
                continue;
            }

            sb.AppendLine($"- {block.Label}: {(settings.SendPersonalData ? value : Pii.Mask(value, personal))}");
        }

        var limiter = services.GetRequiredService<AiRateLimiter>();
        var visitor = limiter.Hash(address ?? "unknown");
        var outcome = await RunAsync(db, services, new Call(AiAction.Assist, sb.ToString().Trim(), step.Instruction, null, formId, null, visitor, address));
        if (!outcome.Ok)
        {
            return new Assist(outcome.Run.Id, null, false, outcome.Run.Status == AiRunStatus.LimitReached ? outcome.Error : "Eine automatische Antwort ist gerade nicht möglich.");
        }

        var raw = outcome.Result!.Value.Text("answer") ?? "";
        if (HtmlSafe.ContainsScript(raw) || HtmlSafe.Clean(raw) is not { Length: > 0 } html)
        {
            await RejectAsync(db, outcome.Run, "Die Antwort der KI enthielt ein Skript oder war leer und wurde verworfen.");
            return new Assist(outcome.Run.Id, null, false, "Eine automatische Antwort ist gerade nicht möglich.");
        }

        return new Assist(outcome.Run.Id, StripLinks(html), outcome.Result.Value.Bool("confident") ?? false, null);
    }

    /// <summary>The assistant may not send visitors anywhere: links of its answer become their text.</summary>
    private static string StripLinks(string html) => System.Text.RegularExpressions.Regex.Replace(html, "</?a\\b[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ================================================================================================ settings page: test and the full instruction

    public static async Task<string> TestAsync(Biz biz, string? address)
    {
        biz.RequireAdmin();
        var outcome = await RunAsync(biz.Db, biz.Services, new Call(AiAction.Test, "Bitte bestätige kurz, dass du erreichbar bist.", null, null, null, biz.UserId, null, address));
        return outcome.Ok
            ? $"Verbindung steht ({outcome.Run.ProfileName}, {outcome.Run.Model}): {outcome.Result!.Value.Text("message")}"
            : $"Fehlgeschlagen: {outcome.Error}";
    }

    /// <summary>"Vollständige Anweisung anzeigen": exactly the text that would go to the model for this action (and ticket).</summary>
    public static async Task<string> FullInstructionAsync(Biz biz, AiAction action, long? ticketId)
    {
        biz.RequireAgent();
        var store = biz.Service<SettingStore>();
        var system = AiInstructions.SystemText(biz.Config.Current.Ai, store.Get<AiSettings>(SettingStore.Ai), store.Get<SiteSettings>(SettingStore.Site), action, null);
        var situation = "(die Lage: Ticket, Formular, Eingaben)";
        if (ticketId is { } id)
        {
            if (await TicketLogic.GetAsync(biz, id) is null)
            {
                throw new ForbiddenException();
            }

            situation = await TicketSituationAsync(biz.Db, store, id, withNotes: action == AiAction.Reply, forTriage: action == AiAction.Triage);
        }

        return $"[system]\n{system}\n\n[user]\n{situation}";
    }

    // ================================================================================================ runs, journal, undo

    public static IQueryable<AiRun> Runs(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.AiRun.AsNoTracking();
    }

    public sealed record RunDetails(AiRun Run, List<AiJournalEntry> Journal, bool CanUndo, string? UndoBlockedBy);

    public static async Task<RunDetails?> RunDetailsAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var run = await biz.Db.AiRun.AsNoTracking().Include(r => r.Steps.OrderBy(s => s.Number)).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null)
        {
            return null;
        }

        var journal = await biz.Db.AiJournalEntry.AsNoTracking().Where(j => j.AiRunId == id).OrderBy(j => j.Id).ToListAsync();
        var blocker = await NewerRunAsync(biz.Db, journal);
        return new RunDetails(run, journal, journal.Any(j => j.RevertedDate is null) && blocker is null, blocker is null ? null : $"Erst Lauf #{blocker} rückgängig machen (neuester zuerst).");
    }

    /// <summary>A newer run that changed the same records and is not undone: it has to be undone first.</summary>
    private static async Task<long?> NewerRunAsync(MtDbContext db, List<AiJournalEntry> journal)
    {
        var open = journal.Where(j => j.RevertedDate is null).ToList();
        if (open.Count == 0)
        {
            return null;
        }

        var runId = open[0].AiRunId;
        var keys = open.Select(j => j.EntityType + ":" + j.EntityId).Distinct().ToList();
        var newer = await db.AiJournalEntry.AsNoTracking()
            .Where(j => j.AiRunId > runId && j.RevertedDate == null)
            .Select(j => new { j.AiRunId, Key = j.EntityType + ":" + j.EntityId })
            .ToListAsync();
        return newer.Where(n => keys.Contains(n.Key)).OrderByDescending(n => n.AiRunId).Select(n => (long?)n.AiRunId).FirstOrDefault();
    }

    /// <summary>Takes back what a run changed. A value somebody changed since is left alone (and said so).</summary>
    public static async Task<string> UndoAsync(Biz biz, long runId)
    {
        biz.RequireAdmin();
        var journal = await biz.Db.AiJournalEntry.Where(j => j.AiRunId == runId && j.RevertedDate == null).OrderByDescending(j => j.Id).ToListAsync();
        if (journal.Count == 0)
        {
            throw new BizException("Dieser Lauf hat nichts geändert, oder es ist schon rückgängig gemacht.");
        }

        if (await NewerRunAsync(biz.Db, journal) is { } blocker)
        {
            throw new BizException($"Ein neuerer Lauf (#{blocker}) hat dieselben Daten geändert. Mach zuerst ihn rückgängig.");
        }

        var skipped = 0;
        foreach (var entry in journal)
        {
            var done = entry.EntityType switch
            {
                nameof(Ticket) => await RevertTicketAsync(biz.Db, entry),
                nameof(TicketMessage) => await RevertMessageAsync(biz.Db, entry),
                _ => false,
            };
            if (!done)
            {
                skipped++;
            }

            entry.RevertedDate = biz.Now;
        }

        await biz.Db.SaveChangesAsync();
        foreach (var ticketId in journal.Where(j => j.EntityType == nameof(Ticket)).Select(j => j.EntityId).Distinct())
        {
            await TicketLogic.AddSystemAsync(biz.Db, ticketId, $"{biz.User.Name} hat die Änderungen der KI (Lauf #{runId}) rückgängig gemacht.");
        }

        await biz.Log.LogAsync("KI-Lauf rückgängig gemacht", nameof(AiRun), runId);
        return skipped == 0 ? "Rückgängig gemacht." : $"Rückgängig gemacht. {skipped} Wert(e) waren inzwischen von Hand geändert und blieben, wie sie sind.";
    }

    private static async Task<bool> RevertTicketAsync(MtDbContext db, AiJournalEntry entry)
    {
        var ticket = await db.Ticket.FirstOrDefaultAsync(t => t.Id == entry.EntityId);
        if (ticket is null)
        {
            return false;
        }

        switch (entry.Field)
        {
            case nameof(Ticket.CategoryId) when Str(ticket.CategoryId) == entry.NewValue:
                ticket.CategoryId = Long(entry.OldValue);
                return true;
            case nameof(Ticket.DepartmentId) when Str(ticket.DepartmentId) == entry.NewValue:
                ticket.DepartmentId = Long(entry.OldValue);
                return true;
            case nameof(Ticket.TeamId) when Str(ticket.TeamId) == entry.NewValue:
                ticket.TeamId = Long(entry.OldValue);
                return true;
            case nameof(Ticket.Priority) when ticket.Priority.ToString() == entry.NewValue && Enum.TryParse<TicketPriority>(entry.OldValue, out var p):
                ticket.Priority = p;
                return true;
            default:
                return false;
        }
    }

    private static async Task<bool> RevertMessageAsync(MtDbContext db, AiJournalEntry entry)
    {
        var message = await db.TicketMessage.FirstOrDefaultAsync(m => m.Id == entry.EntityId);
        if (message is null)
        {
            return false;
        }

        db.TicketMessage.Remove(message);
        return true;
    }

    /// <summary>"Weitermachen": a run that hung or failed is started again with the same action.</summary>
    public static async Task<string> ContinueAsync(Biz biz, long runId, string? address)
    {
        biz.RequireAdmin();
        var run = await biz.Db.AiRun.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId) ?? throw new BizException("Den Lauf gibt es nicht.");
        if (run.Status is not (AiRunStatus.Stuck or AiRunStatus.Error or AiRunStatus.LimitReached or AiRunStatus.Waiting))
        {
            throw new BizException("Nur ein Lauf, der hängt, wartet oder fehlgeschlagen ist, kann weitermachen.");
        }

        return run.Action switch
        {
            AiAction.Triage when run.TicketId is { } t => await TriageInternalAsync(biz.Db, biz.Services, t, biz.UserId, address),
            AiAction.AutoReply when run.TicketId is { } t => await AutoReplyAndSayAsync(biz, t),
            AiAction.Test => await TestAsync(biz, address),
            _ => throw new BizException("Diesen Lauf startet man an seiner Stelle neu (im Ticket oder im Formular)."),
        };
    }

    private static async Task<string> AutoReplyAndSayAsync(Biz biz, long ticketId)
    {
        await AutoReplyInternalAsync(biz.Db, biz.Services, ticketId);
        return "Neuer Lauf gestartet.";
    }

    public sealed record Usage(int TodayTokens, int MonthTokens, int TodayRuns, int MonthRuns);

    public static async Task<Usage> UsageAsync(Biz biz)
    {
        biz.RequireAdmin();
        var now = biz.Now;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var runs = biz.Db.AiRun.AsNoTracking();
        return new Usage(
            await runs.Where(r => r.StartDate >= now.Date).SumAsync(r => r.PromptTokens + r.CompletionTokens),
            await runs.Where(r => r.StartDate >= month).SumAsync(r => r.PromptTokens + r.CompletionTokens),
            await runs.CountAsync(r => r.StartDate >= now.Date),
            await runs.CountAsync(r => r.StartDate >= month));
    }

    // ================================================================================================ helpers

    private static async Task RejectAsync(MtDbContext db, AiRun run, string reason)
    {
        run.Status = AiRunStatus.Error;
        run.Error = reason;
        Warn(run, reason);
        await db.SaveChangesAsync();
    }

    private static List<AiRunStep> Steps(AiRun run) => run.Steps;

    private static void Warn(AiRun run, string text) => run.Steps.Add(new AiRunStep { Kind = AiStepKind.Warning, Text = text, Number = run.Steps.Count + 1, OccurredDate = DateTime.UtcNow });

    private static string? Str(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Limit(string? text, int max)
    {
        var t = text ?? "";
        return t.Length <= max ? t : t[..max] + " …";
    }
}
