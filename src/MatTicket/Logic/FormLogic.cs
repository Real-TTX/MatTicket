using MatTicket.Core.Forms;
using MatTicket.Core.Text;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>The forms of the designer: drafts, checks, publishing, versions.</summary>
public static class FormLogic
{
    /// <summary>Addresses a form can never have: they belong to the app itself.</summary>
    public static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "account", "admin", "tickets", "crm", "api", "t", "f", "s", "health", "ui", "css", "js", "img", "offline", "error", "about", "hilfe",
        "robots.txt", "llms.txt", "sitemap.xml", "manifest.webmanifest", "sw.js", "favicon.svg", "logo.svg", "signin-entra", "signout-entra", "index", "files",
    };

    public static IQueryable<TicketForm> Query(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.TicketForm.AsNoTracking().Include(f => f.Department).Include(f => f.Category);
    }

    public static async Task<TicketForm?> GetAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.TicketForm.AsNoTracking().Include(f => f.Versions.OrderByDescending(v => v.Version)).FirstOrDefaultAsync(f => f.Id == id);
    }

    public sealed record FormSettings(string Name, string Slug, string? Description, long? DepartmentId, long? TeamId, long? CategoryId, bool Indexable, bool AiAutoReply, bool AiTriage);

    /// <summary>Creates a form (with a small starting flow) or changes its settings. The flow itself is saved by <see cref="SaveDraftAsync"/>.</summary>
    public static async Task<TicketForm> SaveSettingsAsync(Biz biz, long? id, FormSettings input)
    {
        biz.RequireAdmin();
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new BizException("Bitte gib einen Namen ein.", "Input.Name");
        }

        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slug.From(name) : input.Slug.Trim().ToLowerInvariant();
        if (!Slug.IsValid(slug))
        {
            throw new BizException("Die Adresse besteht aus Kleinbuchstaben, Ziffern und Bindestrichen.", "Input.Slug");
        }

        if (ReservedSlugs.Contains(slug))
        {
            throw new BizException($"Die Adresse /{slug} gehört der App selbst. Bitte wähle eine andere.", "Input.Slug");
        }

        if (await biz.Db.TicketForm.AnyAsync(f => f.Slug == slug && f.Id != id))
        {
            throw new BizException($"Die Adresse /{slug} hat schon ein anderes Formular.", "Input.Slug");
        }

        if (input.TeamId is { } teamId && input.DepartmentId is { } departmentId && !await biz.Db.Team.AnyAsync(t => t.Id == teamId && t.DepartmentId == departmentId))
        {
            throw new BizException("Das Team gehört nicht zu der gewählten Abteilung.", "Input.TeamId");
        }

        var form = id is { } existing ? await biz.Db.TicketForm.FirstOrDefaultAsync(f => f.Id == existing) ?? throw new BizException("Das Formular gibt es nicht mehr.") : new TicketForm();
        form.Name = name;
        form.Slug = slug;
        form.Description = OrganizationLogic.Clean(input.Description);
        form.DepartmentId = input.DepartmentId;
        form.TeamId = input.TeamId;
        form.CategoryId = input.CategoryId;
        form.Indexable = input.Indexable;
        form.AiAutoReply = input.AiAutoReply;
        form.AiTriage = input.AiTriage;
        if (id is null)
        {
            form.DraftJson = Starter(name).ToJson();
            biz.Db.TicketForm.Add(form);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Formular angelegt" : "Formular geändert", nameof(TicketForm), form.Id, $"{name} (/{slug})");
        return form;
    }

    /// <summary>Saves the flow of the designer: cleaned (safe HTML, known types, limits), then checked. Returns what the check found.</summary>
    public static async Task<(FormDefinition Definition, FormCheck Check)> SaveDraftAsync(Biz biz, long id, FormDefinition posted)
    {
        biz.RequireAdmin();
        var form = await biz.Db.TicketForm.FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Formular gibt es nicht mehr.");
        var clean = FormEngine.Clean(posted);
        if (string.IsNullOrWhiteSpace(clean.Title))
        {
            clean.Title = form.Name;
        }

        form.DraftJson = clean.ToJson();
        await biz.Db.SaveChangesAsync();
        return (clean, FormEngine.Check(clean));
    }

    /// <summary>Publishes the draft as a new version: the public address shows it from now on.</summary>
    public static async Task<TicketFormVersion> PublishAsync(Biz biz, long id, string? note)
    {
        biz.RequireAdmin();
        var form = await biz.Db.TicketForm.FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Formular gibt es nicht mehr.");
        var definition = FormEngine.Clean(FormDefinition.Parse(form.DraftJson));
        var check = FormEngine.Check(definition);
        if (!check.CanPublish)
        {
            throw new BizException("Das Formular kann so nicht veröffentlicht werden: " + string.Join(" ", check.Errors));
        }

        var next = await biz.Db.TicketFormVersion.Where(v => v.TicketFormId == id).Select(v => (int?)v.Version).MaxAsync() ?? 0;
        var version = new TicketFormVersion
        {
            TicketFormId = id,
            Version = next + 1,
            DefinitionJson = definition.ToJson(),
            PublishedDate = biz.Now,
            Note = OrganizationLogic.Clean(note),
        };
        biz.Db.TicketFormVersion.Add(version);
        form.PublishedVersion = version.Version;
        form.PublishedDate = biz.Now;
        form.State = FormState.Published;
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Formular veröffentlicht", nameof(TicketForm), id, $"{form.Name}, Version {version.Version}");
        return version;
    }

    /// <summary>Takes the form offline: the address answers "not found" until it is published again.</summary>
    public static async Task UnpublishAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var form = await biz.Db.TicketForm.FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Formular gibt es nicht mehr.");
        form.State = FormState.Archived;
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Formular offline genommen", nameof(TicketForm), id, form.Name);
    }

    /// <summary>Makes a published version the draft again (to go back to it, or to start from it).</summary>
    public static async Task RestoreVersionAsync(Biz biz, long id, int version)
    {
        biz.RequireAdmin();
        var form = await biz.Db.TicketForm.FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Formular gibt es nicht mehr.");
        var stored = await biz.Db.TicketFormVersion.AsNoTracking().FirstOrDefaultAsync(v => v.TicketFormId == id && v.Version == version)
            ?? throw new BizException("Diese Version gibt es nicht.");
        form.DraftJson = stored.DefinitionJson;
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Formular-Version als Entwurf übernommen", nameof(TicketForm), id, $"{form.Name}, Version {version}");
    }

    public static async Task<TicketForm> CopyAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var form = await biz.Db.TicketForm.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Formular gibt es nicht mehr.");
        var slug = form.Slug + "-kopie";
        for (var i = 2; await biz.Db.TicketForm.AnyAsync(f => f.Slug == slug); i++)
        {
            slug = $"{form.Slug}-kopie-{i}";
        }

        var copy = new TicketForm
        {
            Name = form.Name + " (Kopie)",
            Slug = slug,
            Description = form.Description,
            DraftJson = form.DraftJson,
            DepartmentId = form.DepartmentId,
            TeamId = form.TeamId,
            CategoryId = form.CategoryId,
            Indexable = form.Indexable,
            AiAutoReply = form.AiAutoReply,
            AiTriage = form.AiTriage,
        };
        biz.Db.TicketForm.Add(copy);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Formular kopiert", nameof(TicketForm), copy.Id, copy.Name);
        return copy;
    }

    public static async Task DeleteAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var form = await biz.Db.TicketForm.FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Formular gibt es nicht mehr.");
        biz.Db.TicketForm.Remove(form);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Formular gelöscht", nameof(TicketForm), id, form.Name);
    }

    /// <summary>A new form starts with something that works: the request, then the ticket with the contact details.</summary>
    public static FormDefinition Starter(string title) => new()
    {
        Title = title,
        Intro = "<p>Beschreiben Sie Ihr Anliegen, wir melden uns so schnell wie möglich.</p>",
        Start = "anliegen",
        Steps =
        [
            new FormStep
            {
                Id = "anliegen",
                Kind = StepKinds.Page,
                Title = "Ihr Anliegen",
                X = 40,
                Y = 60,
                Blocks =
                [
                    new FormBlock { Id = "anliegen-b1", Type = BlockTypes.Text, Key = "betreff", Label = "Worum geht es?", Required = true, MapTo = "ticket.subject" },
                    new FormBlock { Id = "anliegen-b2", Type = BlockTypes.Textarea, Key = "beschreibung", Label = "Beschreibung", Required = true, MapTo = "ticket.description", Help = "Je genauer, desto schneller können wir helfen." },
                ],
                Next = [new FormRoute { To = "kontakt" }],
            },
            new FormStep
            {
                Id = "kontakt",
                Kind = StepKinds.Ticket,
                Title = "Ihre Kontaktdaten",
                X = 340,
                Y = 60,
                ButtonText = "Anfrage senden",
                Blocks =
                [
                    new FormBlock { Id = "kontakt-b1", Type = BlockTypes.Text, Key = "name", Label = "Name", Required = true, MapTo = "requester.name", Width = "half" },
                    new FormBlock { Id = "kontakt-b2", Type = BlockTypes.Email, Key = "email", Label = "E-Mail-Adresse", Required = true, MapTo = "requester.email", Width = "half" },
                    new FormBlock { Id = "kontakt-b3", Type = BlockTypes.Phone, Key = "telefon", Label = "Telefon", MapTo = "requester.phone", Width = "half" },
                    new FormBlock { Id = "kontakt-b4", Type = BlockTypes.Text, Key = "firma", Label = "Firma", MapTo = "requester.company", Width = "half" },
                ],
                DoneHtml = "<p>Vielen Dank! Ihre Anfrage ist bei uns angekommen.</p>",
            },
        ],
    };
}
