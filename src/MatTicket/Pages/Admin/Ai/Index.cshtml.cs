using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.AspNetCore.Mvc;

namespace MatTicket.Pages.Admin.Ai;

/// <summary>
/// The AI of the site: on or off, the base instruction (the second layer; empty means the built-in text), personal data, the limits, and what
/// the layers look like together ("Vollständige Anweisung anzeigen").
/// </summary>
public sealed class IndexModel(Biz biz) : PageModelBase
{
    [BindProperty]
    public AiSettings Input { get; set; } = new();

    public AiLogic.Usage Usage { get; private set; } = null!;

    public string OperatorInstruction { get; private set; } = "";

    public bool RelayConfigured { get; private set; }

    public Dictionary<AiAction, string> Preview { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Input = SettingLogic.Get<AiSettings>(biz, SettingStore.Ai);
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.UserDailyTokens < 0 || Input.MonthlyTokens < 0 || Input.VisitorDailyRuns < 0 || Input.PerMinutePerAddress < 0)
        {
            ModelState.AddModelError(string.Empty, "Grenzen können nicht negativ sein (0 heißt: keine Grenze).");
        }

        Input.MaxTokens = Math.Clamp(Input.MaxTokens, 1000, 64_000);
        Input.TimeoutSeconds = Math.Clamp(Input.TimeoutSeconds, 30, 600);
        Input.BaseInstruction = (Input.BaseInstruction ?? "").Trim();
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        await SettingLogic.SaveAsync(biz, SettingStore.Ai, Input, "KI-Einstellungen geändert");
        Flash("Die KI-Einstellungen sind gespeichert.");
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Head("KI: Anweisungen und Grenzen", "Was die KI darf, was sie bekommt und wie viel sie kosten darf.", ("Einstellungen", null), ("KI", null));
        Usage = await AiLogic.UsageAsync(biz);
        OperatorInstruction = biz.Config.Current.Ai.OperatorInstruction;
        RelayConfigured = !string.IsNullOrWhiteSpace(biz.Config.Current.Ai.RelayUrl);
        foreach (var action in new[] { AiAction.Reply, AiAction.Triage, AiAction.Assist, AiAction.AutoReply })
        {
            Preview[action] = await AiLogic.FullInstructionAsync(biz, action, null);
        }
    }
}
