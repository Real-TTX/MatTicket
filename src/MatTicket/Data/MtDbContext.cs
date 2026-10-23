using System.Linq.Expressions;
using MatTicket.Data.Entities;
using MatTicket.Services;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Data;

public class MtDbContext(DbContextOptions<MtDbContext> options, ICurrentUser currentUser, TimeProvider clock) : DbContext(options)
{
    public DbSet<User> User => Set<User>();

    public DbSet<UserTotp> UserTotp => Set<UserTotp>();

    public DbSet<UserRecoveryCode> UserRecoveryCode => Set<UserRecoveryCode>();

    public DbSet<UserSession> UserSession => Set<UserSession>();

    public DbSet<EntraGroupRole> EntraGroupRole => Set<EntraGroupRole>();

    public DbSet<Department> Department => Set<Department>();

    public DbSet<Team> Team => Set<Team>();

    public DbSet<TeamMember> TeamMember => Set<TeamMember>();

    public DbSet<Category> Category => Set<Category>();

    public DbSet<Ticket> Ticket => Set<Ticket>();

    public DbSet<TicketMessage> TicketMessage => Set<TicketMessage>();

    public DbSet<TicketForm> TicketForm => Set<TicketForm>();

    public DbSet<TicketFormVersion> TicketFormVersion => Set<TicketFormVersion>();

    public DbSet<Question> Question => Set<Question>();

    public DbSet<Company> Company => Set<Company>();

    public DbSet<Contact> Contact => Set<Contact>();

    public DbSet<CustomField> CustomField => Set<CustomField>();

    public DbSet<ViewAction> ViewAction => Set<ViewAction>();

    public DbSet<DataConnection> DataConnection => Set<DataConnection>();

    public DbSet<SyncTask> SyncTask => Set<SyncTask>();

    public DbSet<SyncRun> SyncRun => Set<SyncRun>();

    public DbSet<AiProfile> AiProfile => Set<AiProfile>();

    public DbSet<AiRun> AiRun => Set<AiRun>();

    public DbSet<AiRunStep> AiRunStep => Set<AiRunStep>();

    public DbSet<AiJournalEntry> AiJournalEntry => Set<AiJournalEntry>();

    public DbSet<ActivityLog> ActivityLog => Set<ActivityLog>();

    public DbSet<Setting> Setting => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Tables are PascalCase and named like the entity; enums are stored as text; JSON documents are jsonb.
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            entityType.SetTableName(entityType.ClrType.Name);

            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                // UpdateState is a number on purpose (0 = deleted, 1 = created, 2 = updated).
                if (type.IsEnum && type != typeof(UpdateState))
                {
                    property.SetProviderClrType(typeof(string));
                    property.SetMaxLength(40);
                }
                else if (type == typeof(decimal) && property.GetColumnType() is null)
                {
                    property.SetColumnType("numeric(18,2)");
                }
                else if (type == typeof(string) && property.Name.EndsWith("Json", StringComparison.Ordinal))
                {
                    property.SetColumnType("jsonb");
                }
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                b.Entity(entityType.ClrType).HasQueryFilter(NotDeleted(entityType.ClrType));
            }
        }

        b.Entity<User>().Ignore(x => x.Initials);
        b.Entity<Contact>().Ignore(x => x.FullName);
        b.Entity<TicketMessage>().Ignore(x => x.IsPublic);
        b.Entity<AiRun>().Ignore(x => x.TotalTokens);

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.EntraObjectId);
            e.HasOne(x => x.Totp).WithOne(x => x.User).HasForeignKey<UserTotp>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserTotp>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserRecoveryCode>().HasOne(x => x.UserTotp).WithMany(x => x.RecoveryCodes).HasForeignKey(x => x.UserTotpId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EntraGroupRole>().HasIndex(x => x.GroupObjectId).IsUnique();

        b.Entity<Team>().HasOne(x => x.Department).WithMany(x => x.Teams).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TeamMember>(e =>
        {
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasOne(x => x.Team).WithMany(x => x.Members).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(x => x.Teams).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Category>(e =>
        {
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.Number);
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.LastActivityDate);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.AssigneeUserId);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.RequesterUser).WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.RequesterUserId);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.TicketForm).WithMany().HasForeignKey(x => x.TicketFormId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<TicketMessage>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.CreateDate });
            e.HasOne(x => x.Ticket).WithMany(x => x.Messages).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TicketForm>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<TicketFormVersion>(e =>
        {
            e.HasIndex(x => new { x.TicketFormId, x.Version }).IsUnique();
            e.HasOne(x => x.TicketForm).WithMany(x => x.Versions).HasForeignKey(x => x.TicketFormId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Question>().HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<Company>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.CustomerNumber);
            e.HasIndex(x => new { x.SyncTaskId, x.ExternalId });
        });
        b.Entity<Contact>(e =>
        {
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.LastName);
            e.HasIndex(x => new { x.SyncTaskId, x.ExternalId });
            e.HasOne(x => x.Company).WithMany(x => x.Contacts).HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<CustomField>().HasIndex(x => new { x.Entity, x.Key });

        b.Entity<SyncTask>().HasOne(x => x.DataConnection).WithMany().HasForeignKey(x => x.DataConnectionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SyncRun>(e =>
        {
            e.HasIndex(x => new { x.SyncTaskId, x.StartDate });
            e.HasOne(x => x.SyncTask).WithMany().HasForeignKey(x => x.SyncTaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiRun>(e =>
        {
            e.HasIndex(x => x.StartDate);
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.UserId, x.StartDate });
            e.HasIndex(x => new { x.VisitorHash, x.StartDate });
        });
        b.Entity<AiRunStep>().HasOne(x => x.AiRun).WithMany(x => x.Steps).HasForeignKey(x => x.AiRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AiJournalEntry>(e =>
        {
            e.HasIndex(x => x.AiRunId);
            e.HasOne(x => x.AiRun).WithMany().HasForeignKey(x => x.AiRunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityLog>().HasIndex(x => x.OccurredDate);
        b.Entity<Setting>().HasIndex(x => x.Key).IsUnique();
    }

    private static LambdaExpression NotDeleted(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var state = Expression.Property(parameter, nameof(ISoftDeletable.UpdateState));
        var body = Expression.NotEqual(state, Expression.Constant(UpdateState.Deleted));
        return Expression.Lambda(body, parameter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns and turns a delete of a soft-deletable record into a state change.</summary>
    private void Stamp()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var userId = currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>().ToList())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = UpdateState.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreateDate == default)
                    {
                        entry.Entity.CreateDate = now;
                    }

                    entry.Entity.CreateUserId ??= userId;
                    entry.Entity.UpdateDate = entry.Entity.CreateDate;
                    entry.Entity.UpdateUserId = entry.Entity.CreateUserId;
                    if (entry.Entity is ISoftDeletable added && added.UpdateState == UpdateState.Deleted)
                    {
                        added.UpdateState = UpdateState.Created;
                    }

                    break;

                case EntityState.Modified:
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Property(nameof(AuditedEntity.CreateDate)).IsModified = false;
                    entry.Property(nameof(AuditedEntity.CreateUserId)).IsModified = false;
                    if (entry.Entity is ISoftDeletable modified && modified.UpdateState == UpdateState.Created)
                    {
                        modified.UpdateState = UpdateState.Updated;
                    }

                    break;
            }
        }
    }
}
