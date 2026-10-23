using System.Text.Json;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.UiKit;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Pages.Crm;

/// <summary>What the list and edit pages of companies and contacts share: the columns the administrator chose, the filters of select fields, the custom fields of a form.</summary>
public static class CrmPage
{
    /// <summary>The columns of the list as the layout says, each with its cell.</summary>
    public static List<(TableColumn Column, Func<T, IHtmlContent> Cell)> Columns<T>(IReadOnlyList<CrmField> fields, CrmLayout layout, Func<T, string, string?> value, Func<T, string> href, string nameKey)
    {
        var result = new List<(TableColumn, Func<T, IHtmlContent>)>();
        var first = true;
        foreach (var key in layout.ListColumns)
        {
            var field = fields.FirstOrDefault(f => f.Key == key);
            if (field is null)
            {
                continue;
            }

            var isFirst = first;
            first = false;
            result.Add((new TableColumn(field.Label, HideOnPhone: !isFirst && result.Count > 1), item =>
            {
                var text = value(item, key);
                if (isFirst || key == nameKey)
                {
                    return Ui.Link(string.IsNullOrWhiteSpace(text) ? "(ohne Namen)" : text, href(item));
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return Ui.None();
                }

                return field.Type switch
                {
                    CustomFieldType.Email => new HtmlString($"<a href=\"mailto:{System.Net.WebUtility.HtmlEncode(text)}\">{System.Net.WebUtility.HtmlEncode(text)}</a>"),
                    CustomFieldType.Phone => new HtmlString($"<a href=\"tel:{System.Net.WebUtility.HtmlEncode(text)}\">{System.Net.WebUtility.HtmlEncode(text)}</a>"),
                    CustomFieldType.Bool => Ui.Text(text == "true" ? "ja" : "nein"),
                    CustomFieldType.Date when DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var d) => Ui.Text(Services.Fmt.Date(d)),
                    _ => Ui.Text(text),
                };
            }));
        }

        return result;
    }

    /// <summary>A filter for each custom field that is a select: the records whose value is the chosen one.</summary>
    public static List<FilterSpec<T>> SelectFilters<T>(IReadOnlyList<CrmField> fields, System.Linq.Expressions.Expression<Func<T, string>> extraJson)
    {
        var filters = new List<FilterSpec<T>>();
        foreach (var field in fields.Where(f => !f.BuiltIn && f.Type == CustomFieldType.Select))
        {
            var key = field.Key[2..];
            var options = CategoryLogic.ParseOptions(field.Options).Select(o => new FilterOption(o.Label, o.Label)).ToList();
            filters.Add(new FilterSpec<T>("x_" + key, field.Label, options, (q, v) =>
            {
                var json = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = v });
                var parameter = extraJson.Parameters[0];
                var call = System.Linq.Expressions.Expression.Call(
                    typeof(NpgsqlJsonDbFunctionsExtensions).GetMethod(nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains), [typeof(DbFunctions), typeof(object), typeof(object)])!,
                    System.Linq.Expressions.Expression.Constant(EF.Functions),
                    System.Linq.Expressions.Expression.Convert(extraJson.Body, typeof(object)),
                    System.Linq.Expressions.Expression.Constant(json, typeof(object)));
                return q.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(call, parameter));
            }));
        }

        return filters;
    }

    /// <summary>The values of the custom fields of a posted form ("Extra.key").</summary>
    public static Dictionary<string, string?> PostedExtras(IFormCollection form)
        => form.Keys.Where(k => k.StartsWith("Extra.", StringComparison.Ordinal))
            .ToDictionary(k => k[6..], k => (string?)form[k].LastOrDefault());
}
