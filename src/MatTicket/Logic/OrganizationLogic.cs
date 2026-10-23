using MatTicket.Data;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>A choice for a select or a picker: the key, the name and an optional second line.</summary>
public sealed record Choice(long Id, string Name, string? Detail = null, string? Colour = null);

/// <summary>Departments, teams and who is in them.</summary>
public static class OrganizationLogic
{
    // ------------------------------------------------------------------------------------------------ lookups (agents read them)

    public static async Task<List<Choice>> DepartmentChoicesAsync(Biz biz)
    {
        biz.RequireAgent();
        return await biz.Db.Department.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new Choice(d.Id, d.Name, d.Description, d.Colour)).ToListAsync();
    }

    public static async Task<List<Choice>> TeamChoicesAsync(Biz biz)
    {
        biz.RequireAgent();
        return await biz.Db.Team.AsNoTracking().Where(t => t.Department!.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Department!.SortOrder).ThenBy(t => t.Department!.Name).ThenBy(t => t.Name)
            .Select(t => new Choice(t.Id, t.Name, t.Department!.Name, null)).ToListAsync();
    }

    /// <summary>The people a ticket can be assigned to (active agents and administrators).</summary>
    public static async Task<List<Choice>> AgentChoicesAsync(Biz biz)
    {
        biz.RequireAgent();
        return await biz.Db.User.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new Choice(u.Id, u.DisplayName, u.Email, null)).ToListAsync();
    }

    // ------------------------------------------------------------------------------------------------ departments

    public static IQueryable<Department> Departments(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.Department.AsNoTracking().Include(d => d.Teams.Where(t => t.UpdateState != UpdateState.Deleted));
    }

    public static async Task<Department?> DepartmentAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.Department.AsNoTracking().Include(d => d.Teams.Where(t => t.UpdateState != UpdateState.Deleted)).FirstOrDefaultAsync(d => d.Id == id);
    }

    public static async Task<Department> SaveDepartmentAsync(Biz biz, long? id, string name, string? description, string? email, string? colour, int sortOrder)
    {
        biz.RequireAdmin();
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new BizException("Bitte gib einen Namen ein.", "Name");
        }

        if (await biz.Db.Department.AnyAsync(d => d.Name == name && d.Id != id))
        {
            throw new BizException("Eine Abteilung mit diesem Namen gibt es schon.", "Name");
        }

        var department = id is { } existing ? await biz.Db.Department.FirstOrDefaultAsync(d => d.Id == existing) ?? throw new BizException("Die Abteilung gibt es nicht mehr.") : new Department();
        department.Name = name;
        department.Description = Clean(description);
        department.Email = Clean(email);
        department.Colour = IsColour(colour) ? colour : null;
        department.SortOrder = sortOrder;
        if (id is null)
        {
            biz.Db.Department.Add(department);
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Abteilung angelegt" : "Abteilung geändert", nameof(Department), department.Id, name);
        return department;
    }

    public static async Task DeleteDepartmentAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var department = await biz.Db.Department.Include(d => d.Teams).FirstOrDefaultAsync(d => d.Id == id) ?? throw new BizException("Die Abteilung gibt es nicht mehr.");
        if (await biz.Db.Ticket.AnyAsync(t => t.DepartmentId == id && t.Status != TicketStatus.Closed))
        {
            throw new BizException("Die Abteilung hat noch offene Tickets. Verschiebe sie zuerst in eine andere Abteilung.");
        }

        foreach (var team in department.Teams)
        {
            biz.Db.Team.Remove(team);
        }

        biz.Db.Department.Remove(department);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Abteilung gelöscht", nameof(Department), id, department.Name);
    }

    // ------------------------------------------------------------------------------------------------ teams

    public static IQueryable<Team> Teams(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.Team.AsNoTracking().Include(t => t.Department).Include(t => t.Members).ThenInclude(m => m.User);
    }

    public static async Task<Team?> TeamAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        return await biz.Db.Team.AsNoTracking().Include(t => t.Department).Include(t => t.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(t => t.Id == id);
    }

    /// <summary>Saves a team with its members; <paramref name="leadIds"/> are the members who lead it.</summary>
    public static async Task<Team> SaveTeamAsync(Biz biz, long? id, long departmentId, string name, string? description, IReadOnlyCollection<long> memberIds, IReadOnlyCollection<long> leadIds)
    {
        biz.RequireAdmin();
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new BizException("Bitte gib einen Namen ein.", "Name");
        }

        if (!await biz.Db.Department.AnyAsync(d => d.Id == departmentId))
        {
            throw new BizException("Bitte wähle eine Abteilung.", "DepartmentId");
        }

        if (await biz.Db.Team.AnyAsync(t => t.DepartmentId == departmentId && t.Name == name && t.Id != id))
        {
            throw new BizException("In dieser Abteilung gibt es schon ein Team mit diesem Namen.", "Name");
        }

        var team = id is { } existing
            ? await biz.Db.Team.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == existing) ?? throw new BizException("Das Team gibt es nicht mehr.")
            : new Team();
        team.DepartmentId = departmentId;
        team.Name = name;
        team.Description = Clean(description);
        if (id is null)
        {
            biz.Db.Team.Add(team);
        }

        var valid = await biz.Db.User.Where(u => memberIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
        foreach (var gone in team.Members.Where(m => !valid.Contains(m.UserId)).ToList())
        {
            biz.Db.TeamMember.Remove(gone);
        }

        foreach (var userId in valid)
        {
            var member = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is null)
            {
                team.Members.Add(new TeamMember { UserId = userId, IsLead = leadIds.Contains(userId) });
            }
            else
            {
                member.IsLead = leadIds.Contains(userId);
            }
        }

        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync(id is null ? "Team angelegt" : "Team geändert", nameof(Team), team.Id, $"{name} ({valid.Count} Mitglieder)");
        return team;
    }

    public static async Task DeleteTeamAsync(Biz biz, long id)
    {
        biz.RequireAdmin();
        var team = await biz.Db.Team.FirstOrDefaultAsync(t => t.Id == id) ?? throw new BizException("Das Team gibt es nicht mehr.");
        // The tickets stay in the department; they only lose the team.
        await biz.Db.Ticket.Where(t => t.TeamId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.TeamId, (long?)null));
        biz.Db.Team.Remove(team);
        await biz.Db.SaveChangesAsync();
        await biz.Log.LogAsync("Team gelöscht", nameof(Team), id, team.Name);
    }

    internal static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal static bool IsColour(string? colour) => colour is { Length: 7 } && colour[0] == '#' && colour.Skip(1).All(Uri.IsHexDigit);
}
