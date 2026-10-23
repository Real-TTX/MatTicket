using System.Globalization;
using System.Text.Json;
using MatTicket.Core.Forms;
using MatTicket.Core.Text;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>A field of a CRM record as the pages, the layout and the templates know it: built in, or a custom field ("x.key").</summary>
public sealed record CrmField(string Key, string Label, bool BuiltIn, CustomFieldType Type = CustomFieldType.Text, bool Required = false, string? Section = null, string? Options = null, string? Hint = null);

/// <summary>A button of a record page, with its address already filled in.</summary>
public sealed record CrmButton(string Label, string Icon, string Url, ViewActionKind Kind, bool NewWindow);

/// <summary>
/// The small CRM: companies and contacts, the fields the administrator adds, the buttons that lead to other systems (Navision, the main
/// CRM, Google Maps) and the layout of the lists. Agents read and change records; administrators define fields, buttons and the layout, and
/// delete.
/// </summary>
public static class CrmLogic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyList<CrmField> CompanyFields =
    [
        new("Name", "Name", true, Required: true),
        new("CustomerNumber", "Kundennummer", true),
        new("Street", "Straße", true),
        new("PostalCode", "PLZ", true),
        new("City", "Ort", true),
        new("Country", "Land", true),
        new("Phone", "Telefon", true, CustomFieldType.Phone),
        new("Email", "E-Mail", true, CustomFieldType.Email),
        new("Website", "Website", true, CustomFieldType.Url),
        new("Notes", "Notizen", true, CustomFieldType.Textarea),
    ];

    public static readonly IReadOnlyList<CrmField> ContactFields =
    [
        new("Salutation", "Anrede", true),
        new("FirstName", "Vorname", true),
        new("LastName", "Nachname", true, Required: true),
        new("CompanyName", "Firma", true),
        new("Position", "Position", true),
        new("Email", "E-Mail", true, CustomFieldType.Email),
        new("Phone", "Telefon", true, CustomFieldType.Phone),
        new("Mobile", "Mobil", true, CustomFieldType.Phone),
        new("CustomerNumber", "Kundennummer", true),
        new("Street", "Straße", true),
        new("PostalCode", "PLZ", true),
        new("City", "Ort", true),
        new("Country", "Land", true),
        new("Notes", "Notizen", true, CustomFieldType.Textarea),
    ];

    public static IReadOnlyList<CrmField> BuiltIn(CrmEntity entity) => entity == CrmEntity.Company ? CompanyFields : ContactFields;

    /// <summary>Every field of a kind: the built-in ones and the custom ones, in the order of the administrator.</summary>
    public static async Task<List<CrmField>> FieldsAsync(Biz biz, CrmEntity entity)
    {
        biz.RequireAgent();
        var custom = await biz.Db.CustomField.AsNoTracking().Where(f => f.Entity == entity).OrderBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync();
        return [.. BuiltIn(entity), .. custom.Select(f => new CrmField("x." + f.Key, f.Label, false, f.Type, f.Required, f.Section, f.Options, f.Hint))];
    }

    public static Dictionary<string, string> Extras(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The value of a field of a company (built in or "x.key"), as text; also "Address" (one line) for templates.</summary>
    public static string? ValueOf(Company c, string key) => key switch
    {
        "Id" => c.Id.ToString(CultureInfo.InvariantCulture),
        "Name" => c.Name,
        "CustomerNumber" => c.CustomerNumber,
        "Street" => c.Street,
        "PostalCode" => c.PostalCode,
        "City" => c.City,
        "Country" => c.Country,
        "Phone" => c.Phone,
        "Email" => c.Email,
        "Website" => c.Website,
        "Notes" => c.Notes,
        "ExternalId" => c.ExternalId,
        "Address" => Address(c.Street, c.PostalCode, c.City, c.Country),
        _ when key.StartsWith("x.", StringComparison.Ordinal) => Extras(c.ExtraJson).GetValueOrDefault(key[2..]),
        _ => null,
    };

    public static string? ValueOf(Contact c, string key) => key switch
    {
        "Id" => c.Id.ToString(CultureInfo.InvariantCulture),
        "Salutation" => c.Salutation,
        "FirstName" => c.FirstName,
        "LastName" => c.LastName,
        "FullName" => c.FullName,
        "CompanyName" => c.Company?.Name,
        "CompanyCustomerNumber" => c.Company?.CustomerNumber,
        "Position" => c.Position,
        "Email" => c.Email,
        "Phone" => c.Phone,
        "Mobile" => c.Mobile,
        "CustomerNumber" => c.CustomerNumber ?? c.Company?.CustomerNumber,
        "Street" => c.Street ?? c.Company?.Street,
        "PostalCode" => c.PostalCode ?? c.Company?.PostalCode,
        "City" => c.City ?? c.Company?.City,
        "Country" => c.Country ?? c.Company?.Country,
        "Notes" => c.Notes,
        "ExternalId" => c.ExternalId,
        "Address" => c.Street is null && c.City is null && c.Company is not null
            ? Address(c.Company.Street, c.Company.PostalCode, c.Company.City, c.Company.Country)
            : Address(c.Street, c.PostalCode, c.City, c.Country),
        _ when key.StartsWith("x.", StringComparison.Ordinal) => Extras(c.ExtraJson).GetValueOrDefault(key[2..]),
        _ => null,
    };

    public static string Address(string? street, string? postal, string? city, string? country)
        => string.Join(", ", new[] { street, $"{postal} {city}".Trim(), country }.Where(p => !string.IsNullOrWhiteSpace(p)));

    // ================================================================================================ companies

    public static IQueryable<Company> Companies(Biz biz)
    {
        biz.RequireAgent();
        return biz.Db.Company.AsNoTracking();
    }

    public static async Task<Company?> CompanyAsync(Biz biz, long id)
    {
        biz.RequireAgent();
        return await biz.Db.Company.AsNoTracking().Include(c => c.Contacts.OrderBy(x => x.LastName)).FirstOrDefaultAsync(c => c.Id == id);
    }

    public static async Task<Company> SaveCompanyAsync(Biz biz, long? id, Company input, IReadOnlyDictionary<string, string?> extras)
    {
        biz.RequireAgent();
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new BizException("Bitte gib den Namen der Firma ein.", "Input.Name");
        }

        if (!string.IsNullOrWhiteSpace(input.Email) && !FormEngine.IsEmail(input.Email.Trim()))
        {
            throw new BizException("Das ist keine gültige E-Mail-Adresse.", "Input.Email");
        }

        var company = id is { } existing ? await biz.Db.Company.FirstOrDefaultAsync(c => c.Id == existing) ?? throw new BizException("Die Firma gibt es nicht mehr.") : new Company();
        company.Name = name;
        company.CustomerNumber = OrganizationLogic.Clean(input.CustomerNumber);
        company.Street = OrganizationLogic.Clean(input.Street);
        company.PostalCode = OrganizationLogic.Clean(input.PostalCode);
        company.City = OrganizationLogic.Clean(input.City);
        company.Country = OrganizationLogic.Clean(input.Country);
        company.Phone = OrganizationLogic.Clean(input.Phone);
        company.Email = OrganizationLogic.Clean(input.Email);
        company.Website = NormalizeUrl(input.Website);
        company.Notes = OrganizationLogic.Clean(input.Notes);
        company.ExtraJson = await MergeExtrasAsync(biz, CrmEntity.Company, company.ExtraJson, extras);
        if (id is null)
        {
            biz.Db.Company.Add(company);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Firma angelegt" : "Firma geändert", nameof(Company), company.Id, name);
        return company;
    }

    public static async Task DeleteCompanyAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var company = await biz.Db.Company.FirstOrDefaultAsync(c => c.Id == id) ?? throw new BizException("Die Firma gibt es nicht mehr.");
        biz.Db.Company.Remove(company);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Firma gelöscht", nameof(Company), id, company.Name);
    }

    public static async Task<List<Choice>> CompanyChoicesAsync(Biz biz)
    {
        biz.RequireAgent();
        return await biz.Db.Company.AsNoTracking().OrderBy(c => c.Name).Take(2000).Select(c => new Choice(c.Id, c.Name, c.CustomerNumber ?? c.City, null)).ToListAsync();
    }

    // ================================================================================================ contacts

    public static IQueryable<Contact> Contacts(Biz biz)
    {
        biz.RequireAgent();
        return biz.Db.Contact.AsNoTracking().Include(c => c.Company);
    }

    public static async Task<Contact?> ContactAsync(Biz biz, long id)
    {
        biz.RequireAgent();
        return await biz.Db.Contact.AsNoTracking().Include(c => c.Company).FirstOrDefaultAsync(c => c.Id == id);
    }

    public static async Task<Contact> SaveContactAsync(Biz biz, long? id, Contact input, IReadOnlyDictionary<string, string?> extras)
    {
        biz.RequireAgent();
        if (string.IsNullOrWhiteSpace(input.LastName))
        {
            throw new BizException("Bitte gib den Nachnamen ein.", "Input.LastName");
        }

        if (!string.IsNullOrWhiteSpace(input.Email) && !FormEngine.IsEmail(input.Email.Trim()))
        {
            throw new BizException("Das ist keine gültige E-Mail-Adresse.", "Input.Email");
        }

        if (input.CompanyId is { } companyId && !await biz.Db.Company.AnyAsync(c => c.Id == companyId))
        {
            throw new BizException("Diese Firma gibt es nicht.", "Input.CompanyId");
        }

        var contact = id is { } existing ? await biz.Db.Contact.FirstOrDefaultAsync(c => c.Id == existing) ?? throw new BizException("Den Kontakt gibt es nicht mehr.") : new Contact();
        contact.CompanyId = input.CompanyId;
        contact.Salutation = OrganizationLogic.Clean(input.Salutation);
        contact.FirstName = (input.FirstName ?? "").Trim();
        contact.LastName = input.LastName.Trim();
        contact.Position = OrganizationLogic.Clean(input.Position);
        contact.Email = OrganizationLogic.Clean(input.Email)?.ToLowerInvariant();
        contact.Phone = OrganizationLogic.Clean(input.Phone);
        contact.Mobile = OrganizationLogic.Clean(input.Mobile);
        contact.CustomerNumber = OrganizationLogic.Clean(input.CustomerNumber);
        contact.Street = OrganizationLogic.Clean(input.Street);
        contact.PostalCode = OrganizationLogic.Clean(input.PostalCode);
        contact.City = OrganizationLogic.Clean(input.City);
        contact.Country = OrganizationLogic.Clean(input.Country);
        contact.Notes = OrganizationLogic.Clean(input.Notes);
        contact.ExtraJson = await MergeExtrasAsync(biz, CrmEntity.Contact, contact.ExtraJson, extras);
        if (id is null)
        {
            biz.Db.Contact.Add(contact);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Kontakt angelegt" : "Kontakt geändert", nameof(Contact), contact.Id, contact.FullName);
        return contact;
    }

    public static async Task DeleteContactAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var contact = await biz.Db.Contact.FirstOrDefaultAsync(c => c.Id == id) ?? throw new BizException("Den Kontakt gibt es nicht mehr.");
        biz.Db.Contact.Remove(contact);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Kontakt gelöscht", nameof(Contact), id, contact.FullName);
    }

    public static async Task<List<Choice>> ContactChoicesAsync(Biz biz)
    {
        biz.RequireAgent();
        return await biz.Db.Contact.AsNoTracking().OrderBy(c => c.LastName).ThenBy(c => c.FirstName).Take(3000)
            .Select(c => new Choice(c.Id, (c.FirstName + " " + c.LastName).Trim(), c.Company != null ? c.Company.Name : c.Email, null)).ToListAsync();
    }

    /// <summary>The tickets of a company or a contact that the person may see, newest first.</summary>
    public static async Task<List<Ticket>> TicketsOfAsync(Biz biz, long? companyId, long? contactId)
    {
        var visible = await TicketLogic.VisibleAsync(biz);
        return await visible.Where(t => (companyId != null && t.CompanyId == companyId) || (contactId != null && t.ContactId == contactId))
            .OrderByDescending(t => t.LastActivityDate).Take(50).ToListAsync();
    }

    /// <summary>The values of the custom fields from a form, checked against their definitions (required, number, date, e-mail, choices).</summary>
    private static async Task<string> MergeExtrasAsync(Biz biz, CrmEntity entity, string? currentJson, IReadOnlyDictionary<string, string?> posted)
    {
        var fields = await biz.Db.CustomField.AsNoTracking().Where(f => f.Entity == entity).ToListAsync();
        var values = Extras(currentJson);
        foreach (var f in fields)
        {
            if (!posted.TryGetValue(f.Key, out var raw))
            {
                continue;
            }

            var value = (raw ?? "").Trim();
            var field = "Extra." + f.Key;
            if (value.Length == 0)
            {
                if (f.Required)
                {
                    throw new BizException($"Bitte fülle „{f.Label}“ aus.", field);
                }

                values.Remove(f.Key);
                continue;
            }

            var ok = f.Type switch
            {
                CustomFieldType.Number => decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out _),
                CustomFieldType.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                CustomFieldType.Email => FormEngine.IsEmail(value),
                CustomFieldType.Url => Uri.TryCreate(NormalizeUrl(value), UriKind.Absolute, out _),
                CustomFieldType.Select => CategoryLogic.ParseOptions(f.Options).Any(o => o.Label == value || o.Value == value),
                CustomFieldType.Bool => value is "true" or "false",
                _ => true,
            };
            if (!ok)
            {
                throw new BizException($"„{f.Label}“ hat keinen gültigen Wert.", field);
            }

            values[f.Key] = f.Type == CustomFieldType.Url ? NormalizeUrl(value)! : value.Length > 4000 ? value[..4000] : value;
        }

        return JsonSerializer.Serialize(values, Json);
    }

    internal static string? NormalizeUrl(string? url)
    {
        var u = OrganizationLogic.Clean(url);
        return u is null ? null : u.Contains("://", StringComparison.Ordinal) ? u : "https://" + u;
    }

    // ================================================================================================ custom fields

    public static IQueryable<CustomField> CustomFields(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.CustomField.AsNoTracking();
    }

    public static async Task<CustomField?> CustomFieldAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.CustomField.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    }

    public static async Task<CustomField> SaveCustomFieldAsync(Biz biz, long? id, CustomField input)
    {
        biz.RequireAdmin();
        var label = (input.Label ?? "").Trim();
        if (label.Length == 0)
        {
            throw new BizException("Bitte gib eine Beschriftung ein.", "Input.Label");
        }

        var key = string.IsNullOrWhiteSpace(input.Key) ? Slug.Key(label) : input.Key.Trim();
        if (!Slug.IsValidKey(key))
        {
            throw new BizException("Der Schlüssel besteht aus Kleinbuchstaben, Ziffern und _ und beginnt mit einem Buchstaben.", "Input.Key");
        }

        if (await biz.Db.CustomField.AnyAsync(f => f.Entity == input.Entity && f.Key == key && f.Id != id))
        {
            throw new BizException("Diesen Schlüssel gibt es schon.", "Input.Key");
        }

        if (input.Type == CustomFieldType.Select && CategoryLogic.ParseOptions(input.Options).Count == 0)
        {
            throw new BizException("Bitte gib die Auswahlmöglichkeiten ein, eine pro Zeile.", "Input.Options");
        }

        var field = id is { } existing ? await biz.Db.CustomField.FirstOrDefaultAsync(f => f.Id == existing) ?? throw new BizException("Das Feld gibt es nicht mehr.") : new CustomField();
        field.Entity = input.Entity;
        field.Key = key;
        field.Label = label;
        field.Type = input.Type;
        field.Options = input.Type == CustomFieldType.Select ? input.Options?.Trim() : null;
        field.Hint = OrganizationLogic.Clean(input.Hint);
        field.Section = OrganizationLogic.Clean(input.Section);
        field.Required = input.Required;
        field.SortOrder = input.SortOrder;
        if (id is null)
        {
            biz.Db.CustomField.Add(field);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Feld angelegt" : "Feld geändert", nameof(CustomField), field.Id, $"{input.Entity}: {label}");
        return field;
    }

    public static async Task DeleteCustomFieldAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var field = await biz.Db.CustomField.FirstOrDefaultAsync(f => f.Id == id) ?? throw new BizException("Das Feld gibt es nicht mehr.");
        biz.Db.CustomField.Remove(field);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Feld gelöscht", nameof(CustomField), id, field.Label);
    }

    // ================================================================================================ buttons (view actions)

    public static IQueryable<ViewAction> ViewActions(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.ViewAction.AsNoTracking();
    }

    public static async Task<ViewAction?> ViewActionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.ViewAction.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    public static async Task<ViewAction> SaveViewActionAsync(Biz biz, long? id, ViewAction input)
    {
        biz.RequireAdmin();
        if (string.IsNullOrWhiteSpace(input.Label))
        {
            throw new BizException("Bitte gib die Beschriftung des Knopfs ein.", "Input.Label");
        }

        var template = (input.Template ?? "").Trim();
        if (template.Length == 0)
        {
            throw new BizException("Bitte gib die Vorlage ein.", "Input.Template");
        }

        if (input.Kind == ViewActionKind.Link)
        {
            var scheme = template.Split(':', 2)[0].ToLowerInvariant();
            if (scheme is "javascript" or "data" or "vbscript" or "file")
            {
                throw new BizException("Diese Art von Adresse ist nicht erlaubt.", "Input.Template");
            }

            if (!template.Contains(':'))
            {
                throw new BizException("Die Vorlage muss eine vollständige Adresse sein (https://… oder ein eigenes Schema wie dynamicsnav://…).", "Input.Template");
            }
        }

        var action = id is { } existing ? await biz.Db.ViewAction.FirstOrDefaultAsync(a => a.Id == existing) ?? throw new BizException("Den Knopf gibt es nicht mehr.") : new ViewAction();
        action.Entity = input.Entity;
        action.Kind = input.Kind;
        action.Label = input.Label.Trim();
        action.Icon = string.IsNullOrWhiteSpace(input.Icon) ? DefaultIcon(input.Kind) : input.Icon.Trim();
        action.Template = template;
        action.NewWindow = input.NewWindow;
        action.IsActive = input.IsActive;
        action.SortOrder = input.SortOrder;
        if (id is null)
        {
            biz.Db.ViewAction.Add(action);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Knopf angelegt" : "Knopf geändert", nameof(ViewAction), action.Id, action.Label);
        return action;
    }

    public static async Task DeleteViewActionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var action = await biz.Db.ViewAction.FirstOrDefaultAsync(a => a.Id == id) ?? throw new BizException("Den Knopf gibt es nicht mehr.");
        biz.Db.ViewAction.Remove(action);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Knopf gelöscht", nameof(ViewAction), id, action.Label);
    }

    public static string DefaultIcon(ViewActionKind kind) => kind switch
    {
        ViewActionKind.Map => "map-pin",
        ViewActionKind.Call => "phone",
        ViewActionKind.Mail => "mail",
        _ => "external",
    };

    /// <summary>The buttons of a record with their addresses filled in; a button whose placeholders are all empty is left out.</summary>
    public static async Task<List<CrmButton>> ButtonsAsync(Biz biz, CrmEntity entity, Func<string, string?> value)
    {
        biz.RequireAgent();
        var actions = await biz.Db.ViewAction.AsNoTracking().Where(a => a.Entity == entity && a.IsActive).OrderBy(a => a.SortOrder).ThenBy(a => a.Label).ToListAsync();
        var buttons = new List<CrmButton>();
        foreach (var a in actions)
        {
            var names = Placeholders.Names(a.Template);
            if (names.Count > 0 && names.All(n => string.IsNullOrWhiteSpace(value(n))))
            {
                continue;
            }

            var url = a.Kind switch
            {
                ViewActionKind.Map => "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(Placeholders.Fill(a.Template, value, urlEncode: false)),
                ViewActionKind.Call => "tel:" + new string(Placeholders.Fill(a.Template, value, urlEncode: false).Where(ch => char.IsDigit(ch) || ch == '+').ToArray()),
                ViewActionKind.Mail => "mailto:" + Placeholders.Fill(a.Template, value, urlEncode: false).Trim(),
                _ => Placeholders.Fill(a.Template, value, urlEncode: true),
            };
            buttons.Add(new CrmButton(a.Label, a.Icon, url, a.Kind, a.NewWindow && a.Kind is ViewActionKind.Link or ViewActionKind.Map));
        }

        return buttons;
    }

    /// <summary>The embedded map of an address (Google Maps without a key): only loaded when the person opens it.</summary>
    public static string MapEmbedUrl(string address) => "https://maps.google.com/maps?output=embed&q=" + Uri.EscapeDataString(address);

    // ================================================================================================ layout

    public static CrmLayout Layout(Biz biz, CrmEntity entity)
    {
        biz.RequireAgent();
        var layout = SettingLogic.Get<CrmLayout>(biz, SettingStore.Layout(entity));
        if (layout.ListColumns.Count == 0)
        {
            layout.ListColumns = entity == CrmEntity.Company ? ["Name", "CustomerNumber", "City", "Phone"] : ["LastName", "CompanyName", "Email", "Phone"];
        }

        return layout;
    }

    public static async Task SaveLayoutAsync(Biz biz, CrmEntity entity, IEnumerable<string> columns, IEnumerable<string> hidden)
    {
        biz.RequireAdmin();
        var known = (await FieldsAsync(biz, entity)).Select(f => f.Key).ToHashSet();
        var layout = new CrmLayout
        {
            ListColumns = [.. columns.Where(known.Contains).Distinct().Take(8)],
            Hidden = [.. hidden.Where(k => known.Contains(k) && !BuiltIn(entity).Any(f => f.Key == k && f.Required)).Distinct()],
        };
        if (layout.ListColumns.Count == 0)
        {
            throw new BizException("Die Liste braucht mindestens eine Spalte.");
        }

        await SettingLogic.SaveAsync(biz, SettingStore.Layout(entity), layout, "Ansicht geändert");
    }
}
