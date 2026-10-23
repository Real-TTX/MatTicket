using System.Text.Json;
using MatTicket.Core.Forms;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Forms;

/// <summary>
/// The designer of a form: the steps as they will look (WYSIWYG, texts edited in place), the palette of blocks and the catalogue of questions,
/// the properties of what is selected, and the flow editor (steps as nodes, routes as arrows with their conditions). The browser edits the
/// definition; saving sends it here, where it is cleaned and checked (<see cref="FormLogic.SaveDraftAsync"/>).
/// </summary>
public sealed class DesignerModel(Biz biz) : PageModelBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public TicketForm Form { get; private set; } = null!;

    /// <summary>Everything the script needs, as one JSON document in the page (never a script: the content security policy forbids inline ones).</summary>
    public string BootJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync()
    {
        var form = await FormLogic.GetAsync(biz, Id);
        if (form is null)
        {
            return NotFound();
        }

        Form = form;
        var definition = FormEngine.Clean(FormDefinition.Parse(form.DraftJson));
        if (string.IsNullOrWhiteSpace(definition.Title))
        {
            definition.Title = form.Name;
        }

        BootJson = JsonSerializer.Serialize(new
        {
            id = form.Id,
            name = form.Name,
            slug = form.Slug,
            definition,
            check = FormEngine.Check(definition),
            types = BlockTypes.All.Select(t => new { type = t.Type, label = t.Label, icon = t.Icon, question = BlockTypes.IsQuestion(t.Type), options = BlockTypes.HasOptions(t.Type) }),
            stepKinds = new[]
            {
                new { kind = StepKinds.Page, label = "Seite", icon = "file" },
                new { kind = StepKinds.Ai, label = "KI-Antwort", icon = "sparkles" },
                new { kind = StepKinds.Ticket, label = "Ticket erstellen", icon = "ticket" },
                new { kind = StepKinds.End, label = "Ende ohne Ticket", icon = "flag" },
            },
            operators = FormEngine.Operators.Select(o => new { value = o.Value, label = o.Label }),
            mapTargets = MapTargets.All.Select(m => new { value = m.Value, label = m.Label }),
            priorities = Enum.GetValues<TicketPriority>().Select(p => new { value = p.ToString(), label = UiKit.Labels.Of(p) }),
            questions = await CategoryLogic.QuestionBlocksAsync(biz),
            categories = (await CategoryLogic.ChoicesAsync(biz)).Select(c => new { id = c.Id, name = c.Name }),
            departments = (await OrganizationLogic.DepartmentChoicesAsync(biz)).Select(d => new { id = d.Id, name = d.Name }),
            teams = (await OrganizationLogic.TeamChoicesAsync(biz)).Select(t => new { id = t.Id, name = $"{t.Name} ({t.Detail})" }),
        }, Json);

        ViewData["OwnHead"] = true;
        ViewData["Title"] = $"Designer: {form.Name}";
        ViewData["Crumbs"] = UiKit.Crumbs.Of(("Einstellungen", null), ("Formulare", "/Admin/Forms"), (form.Name, $"/Admin/Forms/Edit?id={form.Id}"), ("Designer", null));
        ViewData["ActiveHref"] = "/Admin/Forms";
        ViewData["Width"] = "wide";
        return Page();
    }

    /// <summary>Saves the definition the designer sends (JSON in the field "definition"); answers with the cleaned definition and the check.</summary>
    public async Task<IActionResult> OnPostSaveAsync([FromForm] string definition)
    {
        FormDefinition posted;
        try
        {
            posted = JsonSerializer.Deserialize<FormDefinition>(definition, FormDefinition.Json) ?? new FormDefinition();
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Der Entwurf ist kein gültiges Formular." });
        }

        try
        {
            var (clean, check) = await FormLogic.SaveDraftAsync(biz, Id, posted);
            return new JsonResult(new { ok = true, definition = clean, check }, Json);
        }
        catch (BizException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
