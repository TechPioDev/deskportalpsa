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
    public DbSet<ClientCompanyAccess> ClientCompanyAccess => Set<ClientCompanyAccess>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Desk.Domain.Identity.UserPsaIdentity> UserPsaIdentities => Set<Desk.Domain.Identity.UserPsaIdentity>();
    public DbSet<Desk.Domain.Identity.PsaTechnicianIgnore> PsaTechnicianIgnores => Set<Desk.Domain.Identity.PsaTechnicianIgnore>();
    public DbSet<Desk.Domain.Analytics.ActivityEvent> ActivityEvents => Set<Desk.Domain.Analytics.ActivityEvent>();
    public DbSet<Desk.Domain.Analytics.ActivityDailyFact> ActivityDailyFacts => Set<Desk.Domain.Analytics.ActivityDailyFact>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardMember> BoardMembers => Set<BoardMember>();
    public DbSet<BoardTopic> BoardTopics => Set<BoardTopic>();
    public DbSet<TicketAssignment> TicketAssignments => Set<TicketAssignment>();
    public DbSet<TicketFollower> TicketFollowers => Set<TicketFollower>();
    public DbSet<SavedTicketView> SavedTicketViews => Set<SavedTicketView>();
    public DbSet<SlaPlan> SlaPlans => Set<SlaPlan>();
    public DbSet<TicketTask> TicketTasks => Set<TicketTask>();
    public DbSet<TicketLink> TicketLinks => Set<TicketLink>();
    public DbSet<Desk.Domain.Workforce.WorkSchedule> WorkSchedules => Set<Desk.Domain.Workforce.WorkSchedule>();
    public DbSet<Desk.Domain.Workforce.WorkScheduleDay> WorkScheduleDays => Set<Desk.Domain.Workforce.WorkScheduleDay>();
    public DbSet<Desk.Domain.Workforce.WorkScheduleBreak> WorkScheduleBreaks => Set<Desk.Domain.Workforce.WorkScheduleBreak>();
    public DbSet<Desk.Domain.Workforce.Skill> Skills => Set<Desk.Domain.Workforce.Skill>();
    public DbSet<Desk.Domain.Workforce.StaffSkill> StaffSkills => Set<Desk.Domain.Workforce.StaffSkill>();
    public DbSet<Desk.Domain.Workforce.CapacityException> CapacityExceptions => Set<Desk.Domain.Workforce.CapacityException>();
    public DbSet<Desk.Domain.Workforce.WorkAllocation> WorkAllocations => Set<Desk.Domain.Workforce.WorkAllocation>();
    public DbSet<Desk.Domain.Workforce.WorkPlanning> WorkPlannings => Set<Desk.Domain.Workforce.WorkPlanning>();
    public DbSet<Desk.Domain.Workforce.WorkSession> WorkSessions => Set<Desk.Domain.Workforce.WorkSession>();
    public DbSet<Desk.Domain.Workforce.WorkSessionSegment> WorkSessionSegments => Set<Desk.Domain.Workforce.WorkSessionSegment>();
    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();
    public DbSet<DeskHoliday> DeskHolidays => Set<DeskHoliday>();
    public DbSet<TicketSatisfaction> TicketSatisfactions => Set<TicketSatisfaction>();
    public DbSet<TicketApproval> TicketApprovals => Set<TicketApproval>();
    public DbSet<Desk.Domain.Knowledge.KbArticle> KbArticles => Set<Desk.Domain.Knowledge.KbArticle>();
    public DbSet<Desk.Domain.Knowledge.KbArticleClient> KbArticleClients => Set<Desk.Domain.Knowledge.KbArticleClient>();
    public DbSet<Desk.Domain.Knowledge.KbDeflection> KbDeflections => Set<Desk.Domain.Knowledge.KbDeflection>();
    public DbSet<Desk.Domain.Notifications.PushSubscription> PushSubscriptions => Set<Desk.Domain.Notifications.PushSubscription>();
    public DbSet<Desk.Domain.Notifications.PushPreference> PushPreferences => Set<Desk.Domain.Notifications.PushPreference>();
    public DbSet<Desk.Domain.Notifications.PushTicketState> PushTicketStates => Set<Desk.Domain.Notifications.PushTicketState>();
    public DbSet<Desk.Domain.Notifications.PushNotification> PushNotifications => Set<Desk.Domain.Notifications.PushNotification>();
    public DbSet<RecurringTicket> RecurringTickets => Set<RecurringTicket>();
    public DbSet<AlertSource> AlertSources => Set<AlertSource>();
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();
    public DbSet<Desk.Domain.Assistant.AssistantSettings> AssistantSettings => Set<Desk.Domain.Assistant.AssistantSettings>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<TicketTimeEntry> TicketTimeEntries => Set<TicketTimeEntry>();
    public DbSet<FieldMapping> FieldMappings => Set<FieldMapping>();
    public DbSet<ClassificationMapping> ClassificationMappings => Set<ClassificationMapping>();
    public DbSet<PsaCustomField> PsaCustomFields => Set<PsaCustomField>();
    public DbSet<FieldMappingVersion> FieldMappingVersions => Set<FieldMappingVersion>();
    public DbSet<SyncEvent> SyncEvents => Set<SyncEvent>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<OutboundOperation> OutboundOperations => Set<OutboundOperation>();
    public DbSet<SyncFailure> SyncFailures => Set<SyncFailure>();
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
        List<Desk.Domain.Tickets.TicketTimeEntry>? unplaced = null;

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

            // A time entry carries the PSA account of its ticket. Noted here and given it below, all
            // of them together: asked for one at a time, a thousand entries saved at once was a
            // thousand looks through everything this unit of work holds.
            if (entry is { Entity: Desk.Domain.Tickets.TicketTimeEntry time, State: EntityState.Added } && time.PsaConnectionId is null)
                (unplaced ??= []).Add(time);

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

        if (unplaced is not null) PlaceTimeEntries(unplaced);
    }

    /// <summary>
    /// Gives each new time entry the PSA account of its ticket, where whoever made the entry did not.
    /// "Who has logged time" is read off the entry, and an entry without its account would simply
    /// not be in the answer - so this is not left to each place that writes one.
    ///
    /// The ticket is nearly always in hand: time is logged on a ticket that was just read. Those
    /// that are not are asked for together, once. A ticket with no PSA is an answer, not a miss.
    /// </summary>
    private void PlaceTimeEntries(List<Desk.Domain.Tickets.TicketTimeEntry> entries)
    {
        var wanted = entries.Where(e => e.Ticket is null).Select(e => e.TicketId).ToHashSet();
        var account = new Dictionary<Guid, Guid?>();
        if (wanted.Count > 0)
        {
            foreach (var ticket in ChangeTracker.Entries<Desk.Domain.Tickets.Ticket>())
                if (wanted.Remove(ticket.Entity.Id)) account[ticket.Entity.Id] = ticket.Entity.PsaConnectionId;
        }
        if (wanted.Count > 0)
        {
            var ids = wanted.ToList();
            foreach (var ticket in Tickets.IgnoreQueryFilters().AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.PsaConnectionId }).ToList())
                account[ticket.Id] = ticket.PsaConnectionId;
        }
        foreach (var entry in entries)
            entry.PsaConnectionId = entry.Ticket is { } own ? own.PsaConnectionId : account.GetValueOrDefault(entry.TicketId);
    }
}
