using MatTicket.Configuration;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Services;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>An action is not allowed for the person who asked: the request ends with 403, whatever the page did or did not check.</summary>
public sealed class ForbiddenException(string message = "Dafür fehlt die Berechtigung.") : Exception(message);

/// <summary>A rule of the business was broken ("the slug is taken"): the message is for the person and is shown at the form.</summary>
public sealed class BizException(string message, string? field = null) : Exception(message)
{
    public string? Field { get; } = field;
}

/// <summary>
/// What every operation of the business layer works with: the database, who is acting, the clock and the shared services. The logic itself
/// lives in static classes (<c>TicketLogic</c>, <c>FormLogic</c> ...) that take this as their first argument, and every one of them checks
/// the permission itself (<see cref="RequireAdmin"/>, <see cref="RequireAgent"/>): a page that forgets to check opens nothing.
/// </summary>
public sealed class Biz(MtDbContext db, ICurrentUser user, TimeProvider clock, ActivityLogger log, SecretProtector secrets, ConfigStore config, IServiceProvider services)
{
    private AgentScope? _scope;

    public MtDbContext Db { get; } = db;

    public ICurrentUser User { get; } = user;

    public TimeProvider Clock { get; } = clock;

    public ActivityLogger Log { get; } = log;

    public SecretProtector Secrets { get; } = secrets;

    public ConfigStore Config { get; } = config;

    public IServiceProvider Services { get; } = services;

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public long UserId => User.UserId ?? throw new ForbiddenException("Bitte melde dich an.");

    public bool IsAdmin => User.IsAdmin;

    public bool IsAgent => User.Role is UserRole.Admin or UserRole.Agent;

    public void RequireAdmin()
    {
        if (!User.IsAdmin)
        {
            throw new ForbiddenException("Das dürfen nur Administratoren.");
        }
    }

    /// <summary>Administrators and agents: the people who work on tickets.</summary>
    public void RequireAgent()
    {
        if (!IsAgent)
        {
            throw new ForbiddenException("Das dürfen nur Mitarbeiter des Service.");
        }
    }

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>The departments and teams of the signed-in person (through the teams they are a member of), once per request.</summary>
    public async Task<AgentScope> ScopeAsync(CancellationToken ct = default)
    {
        if (_scope is not null)
        {
            return _scope;
        }

        var id = User.UserId;
        if (id is null)
        {
            return _scope = new AgentScope([], [], []);
        }

        var teams = await Db.TeamMember.AsNoTracking()
            .Where(m => m.UserId == id && m.Team!.UpdateState != UpdateState.Deleted)
            .Select(m => new { m.TeamId, m.Team!.DepartmentId, m.IsLead })
            .ToListAsync(ct);
        return _scope = new AgentScope(
            [.. teams.Select(t => t.TeamId).Distinct()],
            [.. teams.Select(t => t.DepartmentId).Distinct()],
            [.. teams.Where(t => t.IsLead).Select(t => t.TeamId).Distinct()]);
    }
}

/// <summary>Where an agent works: their teams, the departments of those teams, and the teams they lead.</summary>
public sealed record AgentScope(IReadOnlyList<long> TeamIds, IReadOnlyList<long> DepartmentIds, IReadOnlyList<long> LeadTeamIds);
