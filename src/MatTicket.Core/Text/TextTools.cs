using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MatTicket.Core.Text;

/// <summary>Placeholders like <c>{CustomerNumber}</c> or <c>{x.branche}</c> in a template, filled from a record.</summary>
public static partial class Placeholders
{
    /// <summary>
    /// Replaces every <c>{Name}</c> by <paramref name="lookup"/>(Name); an unknown name becomes empty. With <paramref name="urlEncode"/> the values are
    /// encoded for an address (the template itself is the administrator's and stays as it is).
    /// </summary>
    public static string Fill(string template, Func<string, string?> lookup, bool urlEncode)
        => PlaceholderRegex().Replace(template ?? "", m =>
        {
            var value = lookup(m.Groups[1].Value) ?? "";
            return urlEncode ? Uri.EscapeDataString(value) : value;
        });

    /// <summary>The names used in a template.</summary>
    public static IReadOnlyList<string> Names(string? template)
        => [.. PlaceholderRegex().Matches(template ?? "").Select(m => m.Groups[1].Value).Distinct()];

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_.\-]{0,60})\}")]
    private static partial Regex PlaceholderRegex();
}

/// <summary>Addresses and keys made from names: lower case letters, digits and dashes, umlauts written out.</summary>
public static partial class Slug
{
    public static string From(string? text, int max = 60)
    {
        var s = (text ?? "").Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }

        var slug = DashesRegex().Replace(sb.ToString(), "-").Trim('-');
        return slug.Length <= max ? slug : slug[..max].Trim('-');
    }

    /// <summary>A key of an answer or a field: like a slug, with an underscore instead of the dash ("serien_nummer").</summary>
    public static string Key(string? text) => From(text, 40).Replace('-', '_');

    public static bool IsValid(string? slug) => !string.IsNullOrEmpty(slug) && ValidRegex().IsMatch(slug);

    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && KeyRegex().IsMatch(key);

    [GeneratedRegex("-{2,}")]
    private static partial Regex DashesRegex();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,58}[a-z0-9])?$")]
    private static partial Regex ValidRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyRegex();
}

/// <summary>
/// Takes personal data out of a text before it goes to an AI (when the administrator has not allowed it): mail addresses, phone numbers,
/// IBANs. The names of the requester are replaced by the caller, who knows them.
/// </summary>
public static partial class Pii
{
    public static string Mask(string? text, IEnumerable<string>? names = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var masked = EmailRegex().Replace(text, "[E-Mail]");
        masked = IbanRegex().Replace(masked, "[IBAN]");
        masked = PhoneRegex().Replace(masked, "[Telefon]");
        foreach (var name in (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length > 2))
        {
            masked = masked.Replace(name.Trim(), "[Name]", StringComparison.OrdinalIgnoreCase);
        }

        return masked;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){3,7}(?:\s?[A-Z0-9]{1,4})?\b")]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"(?<![\w])(?:\+|00)?\d[\d\s/\-()]{6,}\d")]
    private static partial Regex PhoneRegex();
}
