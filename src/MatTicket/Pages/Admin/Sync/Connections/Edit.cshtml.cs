using System.ComponentModel.DataAnnotations;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Sync.Connections;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        public string Name { get; set; } = "";

        public ConnectionKind Kind { get; set; } = ConnectionKind.SqlServer;

        public string? ConnectionString { get; set; }

        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public DataConnection? Connection { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Connection is { } c)
        {
            Input = new InputModel { Name = c.Name, Kind = c.Kind, Note = c.Note };
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
                var saved = await SyncLogic.SaveConnectionAsync(biz, Id, Input.Name, Input.Kind, Input.ConnectionString, Input.Note);
                Flash("Die Verbindung ist gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        var message = await SyncLogic.TestConnectionAsync(biz, Id ?? 0);
        if (message.StartsWith("Verbunden", StringComparison.Ordinal))
        {
            Flash(message);
        }
        else
        {
            FlashError(message);
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        try
        {
            await SyncLogic.DeleteConnectionAsync(biz, Id ?? 0);
            Flash("Die Verbindung ist gelöscht.");
            return RedirectToPage("/Admin/Sync/Connections/Index");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id });
        }
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Connection = await SyncLogic.ConnectionAsync(biz, id)) is null)
        {
            return false;
        }

        var title = Connection?.Name ?? "Neue Verbindung";
        Head(title, null, ("Einstellungen", null), ("Verbindungen", "/Admin/Sync/Connections"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Sync/Connections";
        return true;
    }
}
