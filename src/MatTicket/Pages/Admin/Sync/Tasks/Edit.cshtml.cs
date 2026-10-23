using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Sync.Tasks;

/// <summary>
/// A sync task: the connection and the query, the preview of its first rows (which shows the columns), which column fills which field,
/// how often it runs, and its runs.
/// </summary>
public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        public string Name { get; set; } = "";

        [Required(ErrorMessage = "Bitte wähle eine Verbindung.")]
        public string? DataConnectionId { get; set; }

        public CrmEntity Target { get; set; } = CrmEntity.Company;

        [Required(ErrorMessage = "Bitte gib die Abfrage ein.")]
        public string Query { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib die Schlüsselspalte an.")]
        public string KeyColumn { get; set; } = "";

        public string? CompanyKeyColumn { get; set; }

        public int IntervalMinutes { get; set; } = 60;

        public bool IsEnabled { get; set; } = true;

        public bool DeleteMissing { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Field → column.</summary>
    [BindProperty]
    public Dictionary<string, string?> Map { get; set; } = [];

    public SyncTask? Task { get; private set; }

    public List<SelectListItem> Connections { get; private set; } = [];

    public List<CrmField> Fields { get; private set; } = [];

    public List<SyncRun> Runs { get; private set; } = [];

    public SyncPreview? Preview { get; private set; }

    public string? Tab { get; private set; }

    public async Task<IActionResult> OnGetAsync(CrmEntity? target)
    {
        if (!await LoadAsync(target))
        {
            return NotFound();
        }

        if (Task is { } t)
        {
            Input = new InputModel
            {
                Name = t.Name, DataConnectionId = t.DataConnectionId.ToString(), Target = t.Target, Query = t.Query, KeyColumn = t.KeyColumn, CompanyKeyColumn = t.CompanyKeyColumn,
                IntervalMinutes = t.IntervalMinutes, IsEnabled = t.IsEnabled, DeleteMissing = t.DeleteMissing,
            };
            Map = SyncLogic.Mappings(t).ToDictionary(m => m.Field, m => (string?)m.Column);
        }
        else
        {
            Input.Target = target ?? CrmEntity.Company;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync(Input.Target))
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var saved = await SyncLogic.SaveTaskAsync(biz, Id, ToTask(), Mappings());
                Flash("Die Aufgabe ist gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        return Page();
    }

    /// <summary>Runs the query of the form (saved or not) and shows the first rows: the columns to choose from.</summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!await LoadAsync(Input.Target))
        {
            return NotFound();
        }

        ModelState.Clear();
        try
        {
            Preview = await SyncLogic.PreviewAsync(biz, IdList.Parse(Input.DataConnectionId).FirstOrDefault(), Input.Query);
        }
        catch (BizException ex)
        {
            Preview = new SyncPreview([], [], ex.Message);
        }

        Tab = "query";
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        var run = await SyncLogic.RunNowAsync(biz, Id ?? 0);
        if (run.Ok)
        {
            Flash("Fertig: " + run.Message);
        }
        else
        {
            FlashError("Fehlgeschlagen: " + run.Message);
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await SyncLogic.DeleteTaskAsync(biz, Id ?? 0);
        Flash("Die Aufgabe ist gelöscht. Die übernommenen Datensätze bleiben.");
        return RedirectToPage("/Admin/Sync/Tasks/Index");
    }

    private SyncTask ToTask() => new()
    {
        Name = Input.Name, DataConnectionId = IdList.Parse(Input.DataConnectionId).FirstOrDefault(), Target = Input.Target, Query = Input.Query, KeyColumn = Input.KeyColumn,
        CompanyKeyColumn = Input.CompanyKeyColumn, IntervalMinutes = Input.IntervalMinutes, IsEnabled = Input.IsEnabled, DeleteMissing = Input.DeleteMissing,
    };

    private List<SyncMapping> Mappings() => [.. Map.Where(m => !string.IsNullOrWhiteSpace(m.Value)).Select(m => new SyncMapping(m.Value!.Trim(), m.Key))];

    private async Task<bool> LoadAsync(CrmEntity? target)
    {
        if (Id is { } id && (Task = await SyncLogic.TaskAsync(biz, id)) is null)
        {
            return false;
        }

        Connections = Controls.Options.From(await SyncLogic.ConnectionChoicesAsync(biz));
        Fields = await SyncLogic.TargetFieldsAsync(biz, Task?.Target ?? target ?? Input.Target);
        if (Task is not null)
        {
            Runs = await SyncLogic.RunsAsync(biz, Task.Id);
        }

        var title = Task?.Name ?? "Neue Sync-Aufgabe";
        Head(title, Task?.DataConnection?.Name, ("Einstellungen", null), ("Sync-Aufgaben", "/Admin/Sync/Tasks"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Sync/Tasks";
        return true;
    }
}
