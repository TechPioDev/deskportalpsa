using System.Linq.Expressions;
using Desk.Application.Abstractions;
using Desk.Domain.Audit;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.ControlPanel;
using Desk.Domain.Identity;
using Desk.Domain.Mapping;
using Desk.Domain.Organization;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Persistence;

/// <summary>
/// The application database context. Two isolation guarantees are enforced here and cannot be
/// bypassed by callers:
///   1. READ — a global query filter constrains every <see cref="ITenantScoped"/> entity to the
///      current tenant (except for platform scope). No repository can opt out.
///   2. WRITE — on save, new tenant-scoped rows are stamped with the current tenant, and any
///      attempt to insert/modify a row for a different tenant is rejected.
/// The audit log is append-only: modifying or deleting an existing entry throws.
/// </summary>
public class DeskDbContext(DbContextOptions<DeskDbContext> options, ITenantContext tenant, TimeProvider clock)
    : DbContext(options)
{
    public DbSet<MspOrganization> MspOrganizations => Set<MspOrganization>();
    public DbSet<PsaConnection> PsaConnections => Set<PsaConnection>();
    public DbSet<ClientCompany> ClientCompanies => Set<ClientCompany>();
    public DbSet<ClientUser> ClientUsers => Set<ClientUser>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Desk.Domain.Identity.UserPsaIdentity> UserPsaIdentities => Set<Desk.Domain.Identity.UserPsaIdentity>();
    public DbSet<Desk.Domain.Analytics.ActivityEvent> ActivityEvents => Set<Desk.Domain.Analytics.ActivityEvent>();
    public DbSet<Desk.Domain.Analytics.ActivityDailyFact> ActivityDailyFacts => Set<Desk.Domain.Analytics.ActivityDailyFact>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();
    public DbSet<Desk.Domain.Assistant.AssistantSettings> AssistantSettings => Set<Desk.Domain.Assistant.AssistantSettings>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<TicketTimeEntry> TicketTimeEntries => Set<TicketTimeEntry>();
    public DbSet<FieldMapping> FieldMappings => Set<FieldMapping>();
    public DbSet<FieldMappingVersion> FieldMappingVersions => Set<FieldMappingVersion>();
    public DbSet<SyncEvent> SyncEvents => Set<SyncEvent>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<TicketInstruction> TicketInstructions => Set<TicketInstruction>();
    public DbSet<ClientAccessGrant> ClientAccessGrants => Set<ClientAccessGrant>();
    public DbSet<Approver> Approvers => Set<Approver>();
    public DbSet<EscalationLevel> EscalationLevels => Set<EscalationLevel>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<BusinessHours> BusinessHours => Set<BusinessHours>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<ClientBranding> ClientBrandings => Set<ClientBranding>();
    public DbSet<FaqArticle> FaqArticles => Set<FaqArticle>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<OrganizationEmailSettings> OrganizationEmailSettings => Set<OrganizationEmailSettings>();
    public DbSet<Desk.Domain.Reporting.StaffReportSchedule> StaffReportSchedules => Set<Desk.Domain.Reporting.StaffReportSchedule>();
    public DbSet<Desk.Domain.Reporting.StaffReportRun> StaffReportRuns => Set<Desk.Domain.Reporting.StaffReportRun>();
    public DbSet<Desk.Domain.Marketing.Enquiry> Enquiries => Set<Desk.Domain.Marketing.Enquiry>();

    // Encrypted PSA-credential storage — infrastructure plumbing, not a domain concept, so it is
    // not ITenantScoped and sits outside the tenant query filter below.
    public DbSet<SecretBlob> SecretBlobs => Set<SecretBlob>();

    // Staff organizational structure (Phase 1 of the RBAC expansion — see the plan). Populated and
    // usable now; not yet consulted by any enforcement.
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<UserDepartment> UserDepartments => Set<UserDepartment>();
    public DbSet<UserTeam> UserTeams => Set<UserTeam>();
    public DbSet<UserBoardAccess> UserBoardAccesses => Set<UserBoardAccess>();
    public DbSet<UserBoardGrant> UserBoardGrants => Set<UserBoardGrant>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<PermissionTemplate> PermissionTemplates => Set<PermissionTemplate>();
    public DbSet<PermissionTemplateEntry> PermissionTemplateEntries => Set<PermissionTemplateEntry>();

    // Read by the compiled query filter below. Guid.Empty can never match a real row, so an
    // unresolved (null) tenant that is not platform scope yields zero rows — fail closed.
    private Guid CurrentTenantId => tenant.OrganizationId ?? Guid.Empty;
    private bool BypassTenantFilter => tenant.IsPlatformScope;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeskDbContext).Assembly);

        // SQLite (local mode) can't ORDER BY or range-compare DateTimeOffset. Store every timestamp
        // as a sortable binary long so all timestamp queries translate. No effect on Postgres.
        if (Database.IsSqlite())
        {
            var converter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                foreach (var prop in entityType.GetProperties())
                    if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                        prop.SetValueConverter(converter);
        }

        // Apply the tenant query filter to every entity implementing ITenantScoped.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                var method = typeof(DeskDbContext)
                    .GetMethod(nameof(BuildTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType);
                var filter = method.Invoke(this, null)!;
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter((LambdaExpression)filter);
            }
        }

        // Same idea for entities that are either a tenant's own row OR a global/built-in one
        // (nullable org id) — e.g. PermissionTemplate, AuditLogEntry. Deliberately NOT applied to
        // AppUser/Role: see the INullableTenantScoped doc comment for why that would break sign-in.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(INullableTenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                var method = typeof(DeskDbContext)
                    .GetMethod(nameof(BuildNullableTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entityType.ClrType);
                var filter = method.Invoke(this, null)!;
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter((LambdaExpression)filter);
            }
        }
    }

    private LambdaExpression BuildTenantFilter<TEntity>() where TEntity : class, ITenantScoped
        // References instance members, so EF re-evaluates per DbContext instance/query.
        => (Expression<Func<TEntity, bool>>)(e => BypassTenantFilter || e.MspOrganizationId == CurrentTenantId);

    private LambdaExpression BuildNullableTenantFilter<TEntity>() where TEntity : class, INullableTenantScoped
        => (Expression<Func<TEntity, bool>>)(e => BypassTenantFilter || e.MspOrganizationId == null || e.MspOrganizationId == CurrentTenantId);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyInvariants();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        ApplyInvariants();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void ApplyInvariants()
    {
        var now = clock.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries())
        {
            // Timestamps
            if (entry is { Entity: BaseEntity be, State: EntityState.Added })
            {
                if (be.CreatedAt == default) be.CreatedAt = now;
                be.UpdatedAt = now;
            }
            else if (entry is { Entity: BaseEntity ue, State: EntityState.Modified })
            {
                ue.UpdatedAt = now;
            }

            // Audit log is append-only.
            if (entry.Entity is AuditLogEntry && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit log entries are immutable and cannot be modified or deleted.");

            // Tenant write isolation.
            if (entry.Entity is ITenantScoped scoped)
            {
                if (entry.State == EntityState.Added)
                {
                    // Stamp new rows with the active tenant unless running under platform scope.
                    if (!BypassTenantFilter)
                    {
                        if (!tenant.OrganizationId.HasValue)
                            throw new InvalidOperationException("Cannot persist a tenant-scoped entity without an established tenant scope.");
                        if (scoped.MspOrganizationId == Guid.Empty)
                            scoped.MspOrganizationId = tenant.OrganizationId.Value;
                        else if (scoped.MspOrganizationId != tenant.OrganizationId.Value)
                            throw new InvalidOperationException("Cross-tenant write blocked: entity tenant does not match the current scope.");
                    }
                }
                else if (entry.State is EntityState.Modified or EntityState.Deleted && !BypassTenantFilter)
                {
                    if (tenant.OrganizationId.HasValue && scoped.MspOrganizationId != tenant.OrganizationId.Value)
                        throw new InvalidOperationException("Cross-tenant write blocked: entity belongs to a different tenant.");
                }
            }
        }
    }
}
