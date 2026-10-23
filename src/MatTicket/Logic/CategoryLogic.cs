using MatTicket.Core.Forms;
using MatTicket.Core.Text;
using MatTicket.Data;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>Categories and subcategories of the tickets, and the catalogue of questions.</summary>
public static class CategoryLogic
{
    /// <summary>Every category with its subcategories below it, as choices ("Drucker › Papierstau"). Agents need them for tickets.</summary>
    public static async Task<List<Choice>> ChoicesAsync(Biz biz, bool onlyActive = false)
    {
        biz.RequireAgent();
        return await LoadChoicesAsync(biz.Db, onlyActive);
    }

    /// <summary>The same for the public forms (no permission: a visitor picks a category).</summary>
    internal static async Task<List<Choice>> LoadChoicesAsync(MtDbContext db, bool onlyActive)
    {
        var all = await db.Category.AsNoTracking().Where(c => !onlyActive || c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        var result = new List<Choice>();
        foreach (var parent in all.Where(c => c.ParentId is null || all.All(p => p.Id != c.ParentId)))
        {
            result.Add(new Choice(parent.Id, parent.Name, parent.Description));
            foreach (var child in all.Where(c => c.ParentId == parent.Id))
            {
                result.Add(new Choice(child.Id, $"{parent.Name} › {child.Name}", child.Description));
            }
        }

        return result;
    }

    public static IQueryable<Category> Query(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.Category.AsNoTracking().Include(c => c.Parent).Include(c => c.Department).Include(c => c.Team);
    }

    public static async Task<Category?> GetAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.Category.AsNoTracking().Include(c => c.Children).FirstOrDefaultAsync(c => c.Id == id);
    }

    public static async Task<List<Choice>> ParentChoicesAsync(Biz biz, long? except)
    {
        biz.RequireAdmin();
        return await biz.Db.Category.AsNoTracking().Where(c => c.ParentId == null && c.Id != except).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new Choice(c.Id, c.Name, null, null)).ToListAsync();
    }

    public static async Task<Category> SaveAsync(Biz biz, long? id, Category input)
    {
        biz.RequireAdmin();
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new BizException("Bitte gib einen Namen ein.", "Name");
        }

        if (input.ParentId is { } parentId)
        {
            var parent = await biz.Db.Category.AsNoTracking().FirstOrDefaultAsync(c => c.Id == parentId) ?? throw new BizException("Die übergeordnete Kategorie gibt es nicht.", "ParentId");
            if (parent.ParentId is not null || parentId == id)
            {
                throw new BizException("Unterkategorien gehen nur eine Ebene tief.", "ParentId");
            }

            if (id is { } self && await biz.Db.Category.AnyAsync(c => c.ParentId == self))
            {
                throw new BizException("Diese Kategorie hat selbst Unterkategorien und kann keine Unterkategorie werden.", "ParentId");
            }
        }

        if (input.TeamId is { } teamId && input.DepartmentId is { } departmentId && !await biz.Db.Team.AnyAsync(t => t.Id == teamId && t.DepartmentId == departmentId))
        {
            throw new BizException("Das Team gehört nicht zu der gewählten Abteilung.", "TeamId");
        }

        var category = id is { } existing ? await biz.Db.Category.FirstOrDefaultAsync(c => c.Id == existing) ?? throw new BizException("Die Kategorie gibt es nicht mehr.") : new Category();
        category.Name = name;
        category.ParentId = input.ParentId;
        category.Description = OrganizationLogic.Clean(input.Description);
        category.Icon = OrganizationLogic.Clean(input.Icon);
        category.DepartmentId = input.DepartmentId;
        category.TeamId = input.TeamId;
        category.DefaultPriority = input.DefaultPriority;
        category.IsActive = input.IsActive;
        category.SortOrder = input.SortOrder;
        category.AccessGroupsJson = PortalLogic.GroupsToJson(PortalLogic.Groups(input.AccessGroupsJson));
        if (id is null)
        {
            biz.Db.Category.Add(category);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Kategorie angelegt" : "Kategorie geändert", nameof(Category), category.Id, name);
        return category;
    }

    public static async Task DeleteAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var category = await biz.Db.Category.FirstOrDefaultAsync(c => c.Id == id) ?? throw new BizException("Die Kategorie gibt es nicht mehr.");
        if (await biz.Db.Category.AnyAsync(c => c.ParentId == id))
        {
            throw new BizException("Die Kategorie hat noch Unterkategorien. Lösche oder verschiebe sie zuerst.");
        }

        biz.Db.Category.Remove(category);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Kategorie gelöscht", nameof(Category), id, category.Name);
    }

    // ------------------------------------------------------------------------------------------------ the catalogue of questions

    public static IQueryable<Question> Questions(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.Question.AsNoTracking().Include(q => q.Category);
    }

    public static async Task<Question?> QuestionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.Question.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id);
    }

    public static async Task<Question> SaveQuestionAsync(Biz biz, long? id, Question input)
    {
        biz.RequireAdmin();
        var label = (input.Label ?? "").Trim();
        if (label.Length == 0)
        {
            throw new BizException("Bitte gib die Frage ein.", "Label");
        }

        var key = string.IsNullOrWhiteSpace(input.Key) ? Slug.Key(label) : input.Key.Trim();
        if (!Slug.IsValidKey(key))
        {
            throw new BizException("Der Schlüssel besteht aus Kleinbuchstaben, Ziffern und _ und beginnt mit einem Buchstaben.", "Key");
        }

        if (!BlockTypes.IsKnown(input.Type) || !BlockTypes.IsQuestion(input.Type))
        {
            throw new BizException("Diese Art von Frage gibt es nicht.", "Type");
        }

        if (BlockTypes.HasOptions(input.Type) && ParseOptions(input.Options).Count == 0)
        {
            throw new BizException("Bitte gib die Auswahlmöglichkeiten ein, eine pro Zeile.", "Options");
        }

        var question = id is { } existing ? await biz.Db.Question.FirstOrDefaultAsync(q => q.Id == existing) ?? throw new BizException("Die Frage gibt es nicht mehr.") : new Question();
        question.Label = label;
        question.Key = key;
        question.Type = input.Type;
        question.Options = BlockTypes.HasOptions(input.Type) ? input.Options?.Trim() : null;
        question.Help = OrganizationLogic.Clean(input.Help);
        question.Placeholder = OrganizationLogic.Clean(input.Placeholder);
        question.Required = input.Required;
        question.CategoryId = input.CategoryId;
        if (id is null)
        {
            biz.Db.Question.Add(question);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Frage angelegt" : "Frage geändert", nameof(Question), question.Id, label);
        return question;
    }

    public static async Task DeleteQuestionAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var question = await biz.Db.Question.FirstOrDefaultAsync(q => q.Id == id) ?? throw new BizException("Die Frage gibt es nicht mehr.");
        biz.Db.Question.Remove(question);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Frage gelöscht", nameof(Question), id, question.Label);
    }

    /// <summary>The choices of a question, one per line: "wert|Beschriftung" or just "Beschriftung".</summary>
    public static List<FormOption> ParseOptions(string? text)
        => [.. (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
                var label = parts.Length == 2 ? parts[1] : parts[0];
                return new FormOption { Value = Slug.Key(parts[0]) is { Length: > 0 } v ? v : "wert", Label = label };
            })
            .Where(o => o.Label.Length > 0)
            .DistinctBy(o => o.Value)];

    /// <summary>The catalogue as blocks for the palette of the designer.</summary>
    public static async Task<List<FormBlock>> QuestionBlocksAsync(Biz biz)
    {
        biz.RequireAdmin();
        var all = await biz.Db.Question.AsNoTracking().OrderBy(q => q.Label).ToListAsync();
        return [.. all.Select(q => new FormBlock
        {
            Type = q.Type,
            Key = q.Key,
            Label = q.Label,
            Help = q.Help,
            Placeholder = q.Placeholder,
            Required = q.Required,
            Options = ParseOptions(q.Options),
            QuestionId = q.Id,
        })];
    }
}
