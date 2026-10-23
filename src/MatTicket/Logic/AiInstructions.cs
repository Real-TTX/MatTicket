using System.Text;
using MatTicket.Configuration;
using MatTicket.Data.Entities;

namespace MatTicket.Logic;

/// <summary>
/// The instructions to the model, in layers from the outside in: the operator's (with an addition per action), the base instruction of the
/// site (editable, empty = built-in), a fixed part nobody edits (answer format, rules), and the situation (ticket, form, what was entered).
/// What goes to the model is exactly <see cref="SystemText"/> plus the situation, and "Vollständige Anweisung anzeigen" shows exactly that.
/// </summary>
public static class AiInstructions
{
    public const string DefaultBase =
        "Du bist der Service-Assistent eines Unternehmens. Du hilfst freundlich, knapp und sachlich. Du schreibst in der Sprache des Kunden, " +
        "auf Deutsch in der Sie-Form. Du erfindest nichts: keine Fakten, Zusagen, Preise, Termine, Rabatte oder Ansprechpartner, die nicht in den " +
        "Angaben stehen. Wenn etwas fehlt, fragst du gezielt nach. Du gibst keine rechtliche, medizinische oder finanzielle Beratung.";

    /// <summary>The fixed part per action: the answer format (one JSON object) and the rules. Not editable on purpose.</summary>
    public static string Fixed(AiAction action) => action switch
    {
        AiAction.Reply =>
            """
            AUFGABE: Schlage dem Service-Mitarbeiter eine Antwort an den Kunden vor. Der Mitarbeiter prüft und sendet sie selbst.
            ANTWORTFORMAT: Genau ein JSON-Objekt, nichts davor oder danach:
            {"reply": "<HTML der Antwort>", "note": "kurzer Hinweis an den Mitarbeiter oder leer"}
            REGELN:
            - reply ist HTML mit nur diesen Elementen: p, br, strong, em, ul, ol, li, a (nur https- oder mailto-Links). Keine Skripte, keine Stile, keine Bilder.
            - Keine Anrede-Floskel ohne Inhalt, keine Signatur (die fügt das System an).
            - Interne Notizen des Teams sind Hintergrund, zitiere sie nie gegenüber dem Kunden.
            """,
        AiAction.Triage =>
            """
            AUFGABE: Ordne das Ticket ein. Wähle nur aus den aufgeführten Listen; passt nichts, nimm null.
            ANTWORTFORMAT: Genau ein JSON-Objekt, nichts davor oder danach:
            {"categoryId": <Zahl oder null>, "priority": "Low" | "Normal" | "High" | "Urgent", "departmentId": <Zahl oder null>, "teamId": <Zahl oder null>, "reason": "ein Satz, warum"}
            REGELN:
            - Urgent nur bei Stillstand, Sicherheitsvorfall oder Gefahr; High bei starker Einschränkung vieler; sonst Normal oder Low.
            - Das Team muss zur gewählten Abteilung gehören.
            """,
        AiAction.Assist =>
            """
            AUFGABE: Ein Besucher hat im Hilfe-Formular sein Problem beschrieben. Gib eine kurze Hilfe zur Selbsthilfe, wenn du sicher etwas Sinnvolles sagen kannst.
            ANTWORTFORMAT: Genau ein JSON-Objekt, nichts davor oder danach:
            {"answer": "<HTML>", "confident": true | false}
            REGELN:
            - answer ist HTML mit nur diesen Elementen: p, br, strong, em, ul, ol, li. Keine Links, keine Skripte.
            - Höchstens fünf konkrete Schritte. Frage nie nach Passwörtern, Zahlungsdaten oder anderen persönlichen Daten.
            - Bist du nicht sicher, sag ehrlich, dass ein Mitarbeiter helfen wird, und setze confident auf false.
            """,
        AiAction.AutoReply =>
            """
            AUFGABE: Schreibe die erste Antwort auf ein neues Ticket. Sie wird automatisch und als KI-Antwort gekennzeichnet an den Kunden geschickt.
            ANTWORTFORMAT: Genau ein JSON-Objekt, nichts davor oder danach:
            {"reply": "<HTML>", "send": true | false, "reason": "ein Satz"}
            REGELN:
            - send ist nur true, wenn die Antwort dem Kunden sicher weiterhilft (eine Anleitung, eine nötige Rückfrage). Sonst false.
            - reply ist HTML mit nur diesen Elementen: p, br, strong, em, ul, ol, li. Keine Links, keine Skripte.
            - Sage, dass sich zusätzlich ein Mitarbeiter meldet. Keine Zusagen zu Zeiten, Kosten oder Ergebnissen.
            """,
        _ =>
            """
            AUFGABE: Bestätige, dass du erreichbar bist.
            ANTWORTFORMAT: Genau ein JSON-Objekt: {"ok": true, "message": "ein kurzer Satz auf Deutsch"}
            """,
    };

    /// <summary>The field the JSON answer of an action must have.</summary>
    public static string RequiredField(AiAction action) => action switch
    {
        AiAction.Reply or AiAction.AutoReply => "reply",
        AiAction.Triage => "priority",
        AiAction.Assist => "answer",
        _ => "ok",
    };

    /// <summary>The system message: operator, action addition of the operator, the base instruction of the site, the fixed part.</summary>
    public static string SystemText(OperatorAiConfig op, AiSettings settings, SiteSettings site, AiAction action, string? stepInstruction)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(op.OperatorInstruction))
        {
            sb.AppendLine("## Vorgabe des Betreibers").AppendLine(op.OperatorInstruction.Trim()).AppendLine();
        }

        if (op.ActionInstructions.TryGetValue(action.ToString(), out var extra) && !string.IsNullOrWhiteSpace(extra))
        {
            sb.AppendLine("## Ergänzung des Betreibers für diese Aktion").AppendLine(extra.Trim()).AppendLine();
        }

        sb.AppendLine("## Grundanweisung").AppendLine(string.IsNullOrWhiteSpace(settings.BaseInstruction) ? DefaultBase : settings.BaseInstruction.Trim());
        if (!string.IsNullOrWhiteSpace(site.Company) || !string.IsNullOrWhiteSpace(site.ServiceName))
        {
            sb.AppendLine($"Du sprichst für: {string.Join(", ", new[] { site.Company, site.ServiceName }.Where(s => !string.IsNullOrWhiteSpace(s)))}.");
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(stepInstruction))
        {
            sb.AppendLine("## Anweisung für diesen Schritt").AppendLine(stepInstruction.Trim()).AppendLine();
        }

        sb.AppendLine("## Format und Regeln (fest)").AppendLine(Fixed(action).Trim());
        return sb.ToString().Trim();
    }

    public static string Nudge(AiAction action)
        => $"Du hast noch nicht im verlangten Format geantwortet. Antworte jetzt ausschließlich mit dem JSON-Objekt (mit dem Feld \"{RequiredField(action)}\"), ohne Ankündigung und ohne Text davor oder danach.";
}
