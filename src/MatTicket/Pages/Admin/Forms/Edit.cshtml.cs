using System.ComponentModel.DataAnnotations;
using MatTicket.Core.Forms;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Forms;

/// <summary>A form's settings (name, address, where its tickets go, the AI), its check, publishing and its versions.</summary>
public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        public string Name { get; set; } = "";

        public string? Slug { get; set; }

        public string? Description { get; set; }

        public string? DepartmentId { get; set; }

        public string? TeamId { get; set; }

        public string? CategoryId { get; set; }

        public bool Indexable { get; set; } = true;

        public bool AiAutoReply { get; set; }

        public bool AiTriage { get; set; }

        public FormAccess Access { get; set; } = FormAccess.Public;

        public string? AccessGroups { get; set; }

        public AccountData AccountData { get; set; } = AccountData.Requesters;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? Note { get; set; }

    public TicketForm? Form { get; private set; }

    public FormCheck? Check { get; private set; }

    public List<SelectListItem> Departments { get; private set; } = [];

    public List<SelectListItem> Teams { get; private set; } = [];

    public List<SelectListItem> Categories { get; private set; } = [];

    public List<SelectListItem> Groups { get; private set; } = [];

    private List<Choice> _groups = [];

    /// <summary>The picker posts the ids of the known groups; the form keeps their object ids.</summary>
    private List<string> GroupObjects(string? ids) => [.. IdList.Parse(ids).Select(id => _groups.FirstOrDefault(g => g.Id == id)?.Detail).Where(g => g is not null).Select(g => g!)];

    private string GroupIds(IEnumerable<string> objectIds) => IdList.Join(_groups.Where(g => objectIds.Contains(g.Detail, StringComparer.OrdinalIgnoreCase)).Select(g => g.Id));

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Form is { } f)
        {
            Input = new InputModel
            {
                Name = f.Name, Slug = f.Slug, Description = f.Description, DepartmentId = f.DepartmentId?.ToString(), TeamId = f.TeamId?.ToString(),
                CategoryId = f.CategoryId?.ToString(), Indexable = f.Indexable, AiAutoReply = f.AiAutoReply, AiTriage = f.AiTriage,
                Access = f.Access, AccountData = f.AccountData, AccessGroups = GroupIds(PortalLogic.Groups(f.AccessGroupsJson)),
            };
        }

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
                var saved = await FormLogic.SaveSettingsAsync(biz, Id, new FormLogic.FormSettings(Input.Name, Input.Slug ?? "", Input.Description,
                    IdOf(Input.DepartmentId), IdOf(Input.TeamId), IdOf(Input.CategoryId), Input.Indexable, Input.AiAutoReply, Input.AiTriage,
                    Input.Access, GroupObjects(Input.AccessGroups), Input.AccountData));
                if (Id is null)
                {
                    Flash("Das Formular ist angelegt. Bau jetzt den Ablauf im Designer.");
                    return RedirectToPage("/Admin/Forms/Designer", new { id = saved.Id });
                }

                Flash("Die Einstellungen sind gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        return Page();
    }

    public Task<IActionResult> OnPostPublishAsync() => RunAsync(async () =>
    {
        var version = await FormLogic.PublishAsync(biz, Id ?? 0, Note);
        return $"Version {version.Version} ist veröffentlicht.";
    });

    public Task<IActionResult> OnPostUnpublishAsync() => RunAsync(async () =>
    {
        await FormLogic.UnpublishAsync(biz, Id ?? 0);
        return "Das Formular ist offline. Die Adresse zeigt nichts mehr an.";
    });

    public Task<IActionResult> OnPostRestoreAsync(int version) => RunAsync(async () =>
    {
        await FormLogic.RestoreVersionAsync(biz, Id ?? 0, version);
        return $"Version {version} ist jetzt der Entwurf. Veröffentliche ihn, damit er gilt.";
    });

    public async Task<IActionResult> OnPostCopyAsync()
    {
        var copy = await FormLogic.CopyAsync(biz, Id ?? 0);
        Flash("Die Kopie ist angelegt (als Entwurf).");
        return RedirectToPage(new { id = copy.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await FormLogic.DeleteAsync(biz, Id ?? 0);
        Flash("Das Formular ist gelöscht. Seine Tickets bleiben.");
        return RedirectToPage("/Admin/Forms/Index");
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action)
    {
        try
        {
            Flash(await action());
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { id = Id });
    }

    private static long? IdOf(string? value) => IdList.Parse(value).Cast<long?>().FirstOrDefault();

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Form = await FormLogic.GetAsync(biz, id)) is null)
        {
            return false;
        }

        if (Form is not null)
        {
            Check = FormEngine.Check(FormEngine.Clean(FormDefinition.Parse(Form.DraftJson)));
        }

        Departments = Controls.Options.From(await OrganizationLogic.DepartmentChoicesAsync(biz));
        Teams = Controls.Options.From(await OrganizationLogic.TeamChoicesAsync(biz));
        Categories = Controls.Options.From(await CategoryLogic.ChoicesAsync(biz));
        _groups = await EntraLogic.GroupChoicesAsync(biz);
        Groups = Controls.Options.From(_groups);
        var title = Form?.Name ?? "Neues Formular";
        Head(title, Form is null ? null : "/" + Form.Slug, ("Einstellungen", null), ("Formulare", "/Admin/Forms"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Forms";
        return true;
    }
}
