using MatTicket.Data.Entities;
using Microsoft.AspNetCore.Html;

namespace MatTicket.UiKit;

/// <summary>The German words for the enums of the data model, and the tone of a status pill (the colour is never alone: the pill carries the word and an icon).</summary>
public static class Labels
{
    public static string Of(UserRole r) => r == UserRole.Admin ? "Administrator" : "Service-Mitarbeiter";

    public static string Of(AuthSource s) => s == AuthSource.Entra ? "Microsoft Entra ID" : "Lokales Passwort";

    public static string Of(TicketStatus s) => s switch
    {
        TicketStatus.New => "Neu",
        TicketStatus.Open => "In Bearbeitung",
        TicketStatus.Waiting => "Wartet auf Kunde",
        TicketStatus.Resolved => "Gelöst",
        _ => "Geschlossen",
    };

    /// <summary>The status as the customer reads it on their page.</summary>
    public static string ForCustomer(TicketStatus s) => s switch
    {
        TicketStatus.New => "Eingegangen",
        TicketStatus.Open => "In Bearbeitung",
        TicketStatus.Waiting => "Wartet auf Ihre Antwort",
        TicketStatus.Resolved => "Gelöst",
        _ => "Geschlossen",
    };

    public static IHtmlContent Pill(TicketStatus s) => s switch
    {
        TicketStatus.New => Ui.Pill(Of(s), "info", "inbox"),
        TicketStatus.Open => Ui.Pill(Of(s), "accent", "progress-play"),
        TicketStatus.Waiting => Ui.Pill(Of(s), "warn", "clock"),
        TicketStatus.Resolved => Ui.Pill(Of(s), "ok", "check"),
        _ => Ui.Pill(Of(s), "plain", "archive"),
    };

    public static string Of(TicketPriority p) => p switch
    {
        TicketPriority.Low => "Niedrig",
        TicketPriority.Normal => "Normal",
        TicketPriority.High => "Hoch",
        _ => "Dringend",
    };

    public static IHtmlContent Pill(TicketPriority p) => p switch
    {
        TicketPriority.Low => Ui.Pill(Of(p), "plain", "arrow-down"),
        TicketPriority.Normal => Ui.Pill(Of(p), "", null),
        TicketPriority.High => Ui.Pill(Of(p), "warn", "arrow-up"),
        _ => Ui.Pill(Of(p), "error", "alert"),
    };

    public static string Of(TicketSource s) => s switch
    {
        TicketSource.Form => "Formular",
        TicketSource.Agent => "Vom Service angelegt",
        _ => "Portal",
    };

    public static string Of(MessageKind k) => k switch
    {
        MessageKind.Customer => "Kunde",
        MessageKind.Reply => "Antwort",
        MessageKind.Note => "Interne Notiz",
        MessageKind.System => "Verlauf",
        _ => "KI-Antwort",
    };

    public static string Of(FormState s) => s switch
    {
        FormState.Draft => "Entwurf",
        FormState.Published => "Veröffentlicht",
        _ => "Offline",
    };

    public static IHtmlContent Pill(FormState s) => s switch
    {
        FormState.Draft => Ui.Pill("Entwurf", "info", "edit"),
        FormState.Published => Ui.Pill("Veröffentlicht", "ok", "globe"),
        _ => Ui.Pill("Offline", "plain", "eye-off"),
    };

    public static string Of(CrmEntity e) => e == CrmEntity.Company ? "Firmen" : "Kontakte";

    public static string Of(CustomFieldType t) => t switch
    {
        CustomFieldType.Text => "Text",
        CustomFieldType.Textarea => "Mehrzeiliger Text",
        CustomFieldType.Number => "Zahl",
        CustomFieldType.Date => "Datum",
        CustomFieldType.Bool => "Ja / Nein",
        CustomFieldType.Select => "Auswahl",
        CustomFieldType.Url => "Adresse (URL)",
        CustomFieldType.Email => "E-Mail",
        _ => "Telefon",
    };

    public static string Of(ViewActionKind k) => k switch
    {
        ViewActionKind.Link => "Link (anderes System)",
        ViewActionKind.Map => "Karte (Google Maps)",
        ViewActionKind.Call => "Anrufen",
        _ => "E-Mail schreiben",
    };

    public static string Of(ConnectionKind k) => k switch
    {
        ConnectionKind.SqlServer => "Microsoft SQL Server",
        ConnectionKind.PostgreSql => "PostgreSQL",
        _ => "MySQL / MariaDB",
    };

    public static string Of(AiProvider p) => p == AiProvider.ChatGpt ? "ChatGPT-Konto" : "OpenAI-kompatibel (API-Schlüssel)";

    public static string Of(AiAction a) => a switch
    {
        AiAction.Reply => "Antwort vorschlagen",
        AiAction.Triage => "Ticket einordnen",
        AiAction.Assist => "Assistent im Formular",
        AiAction.AutoReply => "Automatische Erstantwort",
        _ => "Verbindungstest",
    };

    public static string Of(AiRunStatus s) => s switch
    {
        AiRunStatus.Running => "Läuft",
        AiRunStatus.Done => "Fertig",
        AiRunStatus.Waiting => "Wartet",
        AiRunStatus.LimitReached => "Grenze erreicht",
        AiRunStatus.Stuck => "Hängt",
        AiRunStatus.Error => "Fehler",
        _ => "Abgebrochen",
    };

    public static IHtmlContent Pill(AiRunStatus s) => s switch
    {
        AiRunStatus.Running => Ui.Pill(Of(s), "busy", "refresh"),
        AiRunStatus.Done => Ui.Pill(Of(s), "ok", "check"),
        AiRunStatus.Waiting => Ui.Pill(Of(s), "info", "clock"),
        AiRunStatus.LimitReached => Ui.Pill(Of(s), "warn", "alert"),
        AiRunStatus.Stuck => Ui.Pill(Of(s), "warn", "pause"),
        AiRunStatus.Error => Ui.Pill(Of(s), "error", "x-circle"),
        _ => Ui.Pill(Of(s), "plain", "stop"),
    };

    public static string Of(AiStepKind k) => k switch
    {
        AiStepKind.Message => "Nachricht",
        AiStepKind.Action => "Aktion",
        AiStepKind.Result => "Ergebnis",
        _ => "Warnung",
    };
}
