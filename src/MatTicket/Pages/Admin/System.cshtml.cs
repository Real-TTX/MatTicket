using MatTicket.Logic;
using MatTicket.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin;

/// <summary>The facts of the installation, and the sample data to try everything with.</summary>
public sealed class SystemModel(Biz biz) : PageModelBase
{
    public SystemLogic.Facts Facts { get; private set; } = null!;

    public bool HasSampleData { get; private set; }

    public string DataDir => biz.Config.DataDir;

    public async Task OnGetAsync()
    {
        Head("System", "Version, Daten und Beispieldaten.", ("Einstellungen", null), ("System", null));
        Facts = await SystemLogic.FactsAsync(biz);
        HasSampleData = await SystemLogic.HasSampleDataAsync(biz);
    }

    public async Task<IActionResult> OnPostSampleAsync()
    {
        try
        {
            await SystemLogic.CreateSampleDataAsync(biz);
            Flash("Die Beispieldaten sind angelegt: Abteilungen, Teams, Kategorien, zwei Formulare (/it-support, /service), CRM und Tickets.");
        }
        catch (BizException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage();
    }

    public string Version => AppVersion.Current;
}
