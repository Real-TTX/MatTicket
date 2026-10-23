using System.Globalization;
using System.Net.Mail;
using MatTicket.Core.Text;

namespace MatTicket.Core.Forms;

/// <summary>The answers of a visitor so far: key → value (several choices joined by <see cref="FormEngine.Separator"/>).</summary>
public sealed class FormAnswers : Dictionary<string, string>
{
    public FormAnswers()
        : base(StringComparer.Ordinal)
    {
    }

    public FormAnswers(IDictionary<string, string> values)
        : base(values, StringComparer.Ordinal)
    {
    }

    public string Get(string key) => TryGetValue(key, out var v) ? v : "";
}

/// <summary>What is wrong with a definition: errors keep it from being published, warnings do not.</summary>
public sealed record FormCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool CanPublish => Errors.Count == 0;
}

/// <summary>
/// Runs a form: reads and checks the answers of a step, decides the next step, and checks and cleans a definition before it is saved. The
/// server runs every step: what a browser sends is only ever an answer, never a decision about where to go or what to create.
/// </summary>
public static class FormEngine
{
    public const char Separator = '|';

    /// <summary>The answer key of the outcome of an AI step: "solved" or "open".</summary>
    public const string AiOutcomeKey = "$ai";

    /// <summary>A visitor never walks more steps than this (a loop in a flow ends here).</summary>
    public const int MaxSteps = 60;

    public static readonly IReadOnlyList<(string Value, string Label)> Operators =
    [
        ("eq", "ist gleich"),
        ("neq", "ist nicht"),
        ("contains", "enthält"),
        ("in", "ist eins von (Komma)"),
        ("empty", "ist leer"),
        ("notempty", "ist ausgefüllt"),
        ("gt", "größer als"),
        ("lt", "kleiner als"),
    ];

    /// <summary>The name a block's input has in the posted form.</summary>
    public static string InputName(FormBlock block) => "a_" + block.Key;

    /// <summary>The choices of a block: its own options, the categories or customers the viewer may choose, or yes and no.</summary>
    public static IReadOnlyList<FormOption> OptionsOf(FormBlock block, FormLists lists)
        => block.Type switch
        {
            BlockTypes.Category => lists.Categories,
            BlockTypes.Customer => lists.Customers,
            BlockTypes.YesNo => [new FormOption { Value = "ja", Label = "Ja" }, new FormOption { Value = "nein", Label = "Nein" }],
            _ => block.Options,
        };

    /// <summary>
    /// Reads the answers of one step from what was posted. Only the blocks the viewer sees (<paramref name="visible"/>) are read: a value posted
    /// for a block the viewer does not see is ignored.
    /// </summary>
    public static FormAnswers Read(FormStep step, Func<string, IReadOnlyList<string>> posted, Func<FormBlock, bool>? visible = null)
    {
        var answers = new FormAnswers();
        foreach (var block in step.Blocks.Where(b => BlockTypes.IsQuestion(b.Type) && !string.IsNullOrEmpty(b.Key) && (visible is null || visible(b))))
        {
            var values = posted(InputName(block)).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).ToList();
            answers[block.Key!] = block.Type switch
            {
                BlockTypes.Checkboxes => string.Join(Separator, values.Select(v => v.Replace(Separator, '/')).Distinct()),
                BlockTypes.Consent => values.Contains("ja") ? "ja" : "",
                _ => Limit(values.LastOrDefault() ?? "", block.Type == BlockTypes.Textarea ? 8000 : 500),
            };
        }

        return answers;
    }

    /// <summary>The problems of the answers of a step: input name → message. Empty when everything is fine.</summary>
    public static Dictionary<string, string> Validate(FormStep step, FormAnswers answers, FormLists lists, Func<FormBlock, bool>? visible = null)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in step.Blocks.Where(b => BlockTypes.IsQuestion(b.Type) && !string.IsNullOrEmpty(b.Key) && (visible is null || visible(b))))
        {
            var value = answers.Get(block.Key!);
            var name = InputName(block);
            if (value.Length == 0)
            {
                if (block.Required)
                {
                    errors[name] = block.Type switch
                    {
                        BlockTypes.Consent => "Bitte stimme zu, um fortzufahren.",
                        BlockTypes.Select or BlockTypes.Radio or BlockTypes.Category or BlockTypes.Customer or BlockTypes.YesNo or BlockTypes.Checkboxes => "Bitte wähle etwas aus.",
                        _ => "Bitte fülle dieses Feld aus.",
                    };
                }

                continue;
            }

            switch (block.Type)
            {
                case BlockTypes.Email when !IsEmail(value):
                    errors[name] = "Bitte gib eine gültige E-Mail-Adresse ein.";
                    break;
                case BlockTypes.Number when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                                            && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out _):
                    errors[name] = "Bitte gib eine Zahl ein.";
                    break;
                case BlockTypes.Date when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors[name] = "Bitte wähle ein Datum.";
                    break;
                case BlockTypes.Phone when value.Count(char.IsDigit) < 5:
                    errors[name] = "Bitte gib eine Telefonnummer ein.";
                    break;
                case BlockTypes.Select or BlockTypes.Radio or BlockTypes.Category or BlockTypes.Customer or BlockTypes.YesNo
                    when !OptionsOf(block, lists).Any(o => o.Value == value):
                    errors[name] = "Diese Auswahl gibt es nicht.";
                    break;
                case BlockTypes.Checkboxes when value.Split(Separator).Any(v => !block.Options.Any(o => o.Value == v)):
                    errors[name] = "Diese Auswahl gibt es nicht.";
                    break;
            }
        }

        return errors;
    }

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || !value.Contains('@'))
        {
            return false;
        }

        try
        {
            return new MailAddress(value).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The id of the step after <paramref name="step"/>: the target of the first route whose condition holds; null when none does.</summary>
    public static string? Next(FormStep step, FormAnswers answers)
        => step.Next.FirstOrDefault(r => r.When is null || string.IsNullOrEmpty(r.When.Field) || Holds(r.When, answers))?.To;

    public static bool Holds(FormCondition condition, FormAnswers answers)
    {
        var value = answers.Get(condition.Field);
        var expected = condition.Value ?? "";
        var values = value.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        return condition.Op switch
        {
            "eq" => values.Length > 1 ? values.Contains(expected, StringComparer.OrdinalIgnoreCase) : string.Equals(value, expected, StringComparison.OrdinalIgnoreCase),
            "neq" => values.Length > 1 ? !values.Contains(expected, StringComparer.OrdinalIgnoreCase) : !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => expected.Length > 0 && value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "in" => expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(e => values.Contains(e, StringComparer.OrdinalIgnoreCase) || string.Equals(value, e, StringComparison.OrdinalIgnoreCase)),
            "empty" => value.Length == 0,
            "notempty" => value.Length > 0,
            "gt" => Compare(value, expected) > 0,
            "lt" => Compare(value, expected) < 0,
            _ => false,
        };
    }

    /// <summary>Numbers as numbers, dates (ISO) and everything else as text; a value that is not there is never greater or smaller.</summary>
    private static int? Compare(string a, string b)
    {
        if (a.Length == 0)
        {
            return null;
        }

        if (decimal.TryParse(a.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var y))
        {
            return x.CompareTo(y);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An answer as a person reads it: the labels of the choices, a date in German, ja/nein.</summary>
    public static string Display(FormBlock block, string value, FormLists lists)
    {
        if (value.Length == 0)
        {
            return "";
        }

        switch (block.Type)
        {
            case BlockTypes.Checkboxes:
                return string.Join(", ", value.Split(Separator).Select(v => block.Options.FirstOrDefault(o => o.Value == v)?.Label ?? v));
            case BlockTypes.Select or BlockTypes.Radio or BlockTypes.Category or BlockTypes.Customer or BlockTypes.YesNo:
                return OptionsOf(block, lists).FirstOrDefault(o => o.Value == value)?.Label ?? value;
            case BlockTypes.Consent:
                return value == "ja" ? "zugestimmt" : "";
            case BlockTypes.Date when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d):
                return d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }

    /// <summary>The subject of a ticket from a template with the answers ("{bereich}: {kurzbeschreibung}"), as a person reads them.</summary>
    public static string FillTemplate(FormDefinition definition, string template, FormAnswers answers, FormLists lists)
        => Placeholders.Fill(template, key =>
        {
            var block = definition.Question(key);
            return block is null ? answers.Get(key) : Display(block, answers.Get(key), lists);
        }, urlEncode: false).Trim();

    // ================================================================================================ checking and cleaning a definition

    /// <summary>What keeps a definition from being published (errors) and what looks odd (warnings).</summary>
    public static FormCheck Check(FormDefinition def)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (def.Steps.Count == 0)
        {
            errors.Add("Das Formular hat noch keinen Schritt.");
            return new FormCheck(errors, warnings);
        }

        if (def.Step(def.Start) is null)
        {
            errors.Add("Der Startschritt fehlt.");
        }

        foreach (var dup in def.Steps.GroupBy(s => s.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"Die Schritt-Id „{dup.Key}“ kommt mehrfach vor.");
        }

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in def.Steps)
        {
            var name = string.IsNullOrWhiteSpace(step.Title) ? step.Id : step.Title;
            if (!StepKinds.All.Contains(step.Kind))
            {
                errors.Add($"Schritt „{name}“: unbekannte Art „{step.Kind}“.");
            }

            foreach (var route in step.Next)
            {
                if (def.Step(route.To) is null)
                {
                    errors.Add($"Schritt „{name}“ führt zu einem Schritt, den es nicht gibt.");
                }

                if (route.When is { Field.Length: > 0 } when && when.Field != AiOutcomeKey && def.Question(when.Field) is null)
                {
                    errors.Add($"Schritt „{name}“: die Bedingung fragt nach „{when.Field}“, aber keine Frage hat diesen Schlüssel.");
                }
            }

            if (!StepKinds.IsFinal(step.Kind) && step.Next.Count == 0)
            {
                errors.Add($"Schritt „{name}“ führt nirgendwohin. Lege einen Weg zum nächsten Schritt an.");
            }

            if (!StepKinds.IsFinal(step.Kind) && step.Next.Count > 0 && step.Next.All(r => r.When is { Field.Length: > 0 }))
            {
                warnings.Add($"Schritt „{name}“ hat keinen Standardweg: passt keine Bedingung, bleibt der Besucher stehen.");
            }

            if (StepKinds.IsFinal(step.Kind) && step.Next.Count > 0)
            {
                warnings.Add($"Schritt „{name}“ ist ein Endschritt; seine Wege werden nie benutzt.");
            }

            foreach (var block in step.Blocks.Where(b => BlockTypes.IsQuestion(b.Type)))
            {
                if (!Slug.IsValidKey(block.Key))
                {
                    errors.Add($"Schritt „{name}“: die Frage „{block.Label}“ braucht einen Schlüssel (Kleinbuchstaben, Ziffern, _).");
                    continue;
                }

                if (!keys.TryAdd(block.Key!, name))
                {
                    errors.Add($"Der Schlüssel „{block.Key}“ wird von mehreren Fragen benutzt.");
                }

                if (string.IsNullOrWhiteSpace(block.Label))
                {
                    errors.Add($"Schritt „{name}“: eine Frage hat keine Beschriftung.");
                }

                if (BlockTypes.HasOptions(block.Type) && block.Options.Count == 0)
                {
                    errors.Add($"Schritt „{name}“: die Frage „{block.Label}“ hat keine Auswahlmöglichkeiten.");
                }
            }
        }

        if (!def.Steps.Any(s => s.Kind == StepKinds.Ticket))
        {
            warnings.Add("Kein Schritt erstellt ein Ticket: das Formular kann nur informieren.");
        }

        var ticketSteps = def.Steps.Where(s => s.Kind == StepKinds.Ticket).ToList();
        var questions = def.Questions.ToList();
        if (ticketSteps.Count > 0 && !questions.Any(q => q.MapTo == "requester.email" || q.Type == BlockTypes.Email))
        {
            warnings.Add("Keine Frage nach der E-Mail-Adresse: der Kunde bekommt dann keine Nachricht zu seinem Ticket.");
        }

        foreach (var unreachable in Unreachable(def))
        {
            warnings.Add($"Schritt „{(string.IsNullOrWhiteSpace(unreachable.Title) ? unreachable.Id : unreachable.Title)}“ wird nie erreicht.");
        }

        return new FormCheck(errors.Distinct().ToList(), warnings.Distinct().ToList());
    }

    private static IEnumerable<FormStep> Unreachable(FormDefinition def)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (def.Step(def.Start) is not null)
        {
            queue.Enqueue(def.Start);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id))
            {
                continue;
            }

            foreach (var route in def.Step(id)?.Next ?? [])
            {
                queue.Enqueue(route.To);
            }
        }

        return def.Steps.Where(s => !seen.Contains(s.Id));
    }

    /// <summary>
    /// The definition as it may be stored: texts cleaned of everything that is not safe HTML, unknown kinds and types dropped, missing ids made,
    /// sizes limited. Whatever the browser of the designer sends goes through here.
    /// </summary>
    public static FormDefinition Clean(FormDefinition def)
    {
        var clean = new FormDefinition
        {
            Schema = 1,
            Title = Limit(def.Title, 200),
            Intro = NullIfEmpty(HtmlSafe.Clean(def.Intro)),
            Start = Limit(def.Start, 40),
        };

        var n = 0;
        foreach (var step in def.Steps.Take(80))
        {
            n++;
            var s = new FormStep
            {
                Id = Slug.IsValid(step.Id) ? step.Id : "s" + n,
                Kind = StepKinds.All.Contains(step.Kind) ? step.Kind : StepKinds.Page,
                Title = Limit(step.Title, 200),
                X = Math.Clamp(double.IsFinite(step.X) ? step.X : 0, -5000, 20000),
                Y = Math.Clamp(double.IsFinite(step.Y) ? step.Y : 0, -5000, 20000),
                ButtonText = NullIfEmpty(Limit(step.ButtonText, 60)),
                Instruction = NullIfEmpty(Limit(step.Instruction, 4000)),
                HelpedText = NullIfEmpty(Limit(step.HelpedText, 80)),
                NotHelpedText = NullIfEmpty(Limit(step.NotHelpedText, 80)),
                SubjectTemplate = NullIfEmpty(Limit(step.SubjectTemplate, 300)),
                Priority = step.Priority is "Low" or "Normal" or "High" or "Urgent" ? step.Priority : null,
                CategoryId = step.CategoryId,
                DepartmentId = step.DepartmentId,
                TeamId = step.TeamId,
                DoneHtml = NullIfEmpty(HtmlSafe.Clean(step.DoneHtml)),
            };

            var b = 0;
            foreach (var block in step.Blocks.Where(x => BlockTypes.IsKnown(x.Type)).Take(60))
            {
                b++;
                var question = BlockTypes.IsQuestion(block.Type);
                s.Blocks.Add(new FormBlock
                {
                    Id = Slug.IsValid(block.Id) ? block.Id : $"{s.Id}-b{b}",
                    Type = block.Type,
                    Key = question ? Limit(block.Key, 40) : null,
                    Label = question ? Limit(block.Label, 300) : null,
                    Help = question ? NullIfEmpty(Limit(block.Help, 600)) : null,
                    Placeholder = question ? NullIfEmpty(Limit(block.Placeholder, 120)) : null,
                    Required = question && block.Required,
                    Html = block.Type == BlockTypes.RichText ? HtmlSafe.Clean(block.Html) : null,
                    Options = BlockTypes.HasOptions(block.Type)
                        ? [.. block.Options.Where(o => !string.IsNullOrWhiteSpace(o.Label)).Take(100).Select(o => new FormOption
                        {
                            Value = Slug.Key(string.IsNullOrWhiteSpace(o.Value) ? o.Label : o.Value) is { Length: > 0 } v ? v : "wert",
                            Label = Limit(o.Label, 200),
                        }).DistinctBy(o => o.Value)]
                        : [],
                    QuestionId = block.QuestionId,
                    MapTo = question && block.Type != BlockTypes.Customer && MapTargets.IsKnown(block.MapTo) ? NullIfEmpty(block.MapTo) : null,
                    Width = block.Width == "half" ? "half" : null,

                    // The list of customers is data of the CRM: only service staff ever see it, whatever the designer says.
                    Visibility = block.Type == BlockTypes.Customer ? Visibilities.Staff : Visibilities.IsKnown(block.Visibility) ? NullIfEmpty(block.Visibility) : null,
                });
            }

            foreach (var route in step.Next.Take(30))
            {
                s.Next.Add(new FormRoute
                {
                    To = Limit(route.To, 40),
                    When = route.When is { Field.Length: > 0 } w
                        ? new FormCondition { Field = Limit(w.Field, 40), Op = Operators.Any(o => o.Value == w.Op) ? w.Op : "eq", Value = NullIfEmpty(Limit(w.Value, 300)) }
                        : null,
                });
            }

            clean.Steps.Add(s);
        }

        if (clean.Step(clean.Start) is null && clean.Steps.Count > 0)
        {
            clean.Start = clean.Steps[0].Id;
        }

        return clean;
    }

    private static string Limit(string? text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
