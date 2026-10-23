using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;

namespace MatTicket.Core.Text;

/// <summary>
/// The one gate for HTML that is shown to someone: what an administrator writes in the WYSIWYG editor, what a customer or an agent writes,
/// and what the AI answers. An allowlist of tags and attributes; links only to http, https, mailto and tel, and they open safely. A script is
/// never let through: <see cref="ContainsScript"/> lets the caller refuse the whole text and say why.
/// </summary>
public static partial class HtmlSafe
{
    private static readonly HtmlSanitizer Sanitizer = Create();

    private static HtmlSanitizer Create()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "strong", "b", "em", "i", "u", "s", "ul", "ol", "li", "a", "h2", "h3", "h4", "blockquote", "code", "pre", "hr", "span", "div", "table", "thead", "tbody", "tr", "th", "td" })
        {
            s.AllowedTags.Add(tag);
        }

        s.AllowedAttributes.Clear();
        s.AllowedAttributes.Add("href");
        s.AllowedAttributes.Add("title");
        s.AllowedAttributes.Add("colspan");
        s.AllowedAttributes.Add("rowspan");
        s.AllowedCssProperties.Clear();
        s.AllowedClasses.Clear();
        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");
        s.AllowedSchemes.Add("tel");
        s.AllowedAtRules.Clear();
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement a)
            {
                a.SetAttribute("rel", "noopener noreferrer nofollow");
                if (a.GetAttribute("href") is { } href && href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    a.SetAttribute("target", "_blank");
                }
            }
        };
        s.AllowedAttributes.Add("rel");
        s.AllowedAttributes.Add("target");
        return s;
    }

    /// <summary>The HTML with everything that is not allowed taken out.</summary>
    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var cleaned = Sanitizer.Sanitize(html).Trim();
        return IsBlank(cleaned) ? "" : cleaned;
    }

    /// <summary>Whether the text tries to run something: a script element, an event handler, a javascript: or data: address.</summary>
    public static bool ContainsScript(string? html)
        => !string.IsNullOrEmpty(html) && ScriptRegex().IsMatch(html);

    /// <summary>Plain text (what a customer typed) as safe HTML: encoded, lines kept, paragraphs at blank lines.</summary>
    public static string FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var paragraphs = text.Replace("\r\n", "\n").Trim().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            sb.Append("<p>").Append(WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>")).Append("</p>");
        }

        return sb.ToString();
    }

    /// <summary>The text of an HTML fragment (for the AI, a mail in plain text, a preview): blocks become lines, entities are decoded.</summary>
    public static string ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var text = BlockEndRegex().Replace(html, "\n\n");
        text = BreakRegex().Replace(text, "\n");
        text = ListItemRegex().Replace(text, "\n- ");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = BlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>A short preview of an HTML fragment as one line.</summary>
    public static string Preview(string? html, int max = 140)
    {
        var text = SpaceRegex().Replace(ToText(html), " ").Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    private static bool IsBlank(string html) => ToText(html).Trim().Length == 0 && !html.Contains("<hr", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<\s*script|\son[a-z]+\s*=|javascript\s*:|vbscript\s*:|data\s*:\s*text/html|<\s*iframe|<\s*object|<\s*embed", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"</(p|div|h[1-6]|blockquote|pre|tr|table|ul|ol)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
