using Desk.Domain.Audit;
using Desk.Domain.ControlPanel;
using Desk.Domain.Identity;
using Desk.Domain.Mapping;
using Desk.Domain.Marketing;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Desk.Infrastructure.Persistence.Configurations;

public sealed class MspOrganizationConfig : IEntityTypeConfiguration<MspOrganization>
{
    public void Configure(EntityTypeBuilder<MspOrganization> b)
    {
        b.Property(x => x.PushPublicKey).HasMaxLength(100);
        b.Property(x => x.PushPrivateKeyRef).HasMaxLength(200);
        b.ToTable("msp_organizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(100).IsRequired();
        b.Property(x => x.AttentionDigestRecipients).HasMaxLength(1000);
        b.HasIndex(x => x.Slug).IsUnique();
    }
}

public sealed class PsaConnectionConfig : IEntityTypeConfiguration<PsaConnection>
{
    public void Configure(EntityTypeBuilder<PsaConnection> b)
    {
        b.ToTable("psa_connections");
        b.Property(x => x.LogoUrl).HasMaxLength(500);
        b.Property(x => x.LogoStorageKey).HasMaxLength(200);
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ApiEndpoint).HasMaxLength(500).IsRequired();
        b.Property(x => x.CredentialSecretRef).HasMaxLength(500).IsRequired();
        b.Property(x => x.LastErrorKind).HasMaxLength(40);
        b.Property(x => x.AccountKeyHash).HasMaxLength(64);
        // A database default, so a row written by the previous version of the code - which does
        // not know the column - is a connection that is not in setup, as every one of its were.
        b.Property(x => x.InSetup).HasDefaultValue(false);
        // The same, for the same reason: the previous version writes a connection nobody has asked to sync.
        b.Property(x => x.SyncRequestedFull).HasDefaultValue(false);
        b.Property(x => x.SyncRequestedBy).HasMaxLength(200);
        b.HasIndex(x => x.MspOrganizationId);
        b.HasOne<MspOrganization>().WithMany(o => o.PsaConnections)
            .HasForeignKey(x => x.MspOrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ClientCompanyConfig : IEntityTypeConfiguration<ClientCompany>
{
    public void Configure(EntityTypeBuilder<ClientCompany> b)
    {
        b.ToTable("client_companies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ExternalCompanyId).HasMaxLength(200).IsRequired();
        // One external company maps once per connection.
        b.HasIndex(x => new { x.PsaConnectionId, x.ExternalCompanyId }).IsUnique();
        b.HasOne(x => x.PsaConnection).WithMany(c => c.ClientCompanies)
            .HasForeignKey(x => x.PsaConnectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ClientUserConfig : IEntityTypeConfiguration<ClientUser>
{
    public void Configure(EntityTypeBuilder<ClientUser> b)
    {
        b.ToTable("client_users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.HasIndex(x => new { x.ClientCompanyId, x.Email }).IsUnique();
        b.HasIndex(x => x.IdpSubject);
        b.HasOne(x => x.ClientCompany).WithMany(c => c.Users)
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AppUserConfig : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("app_users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PhoneNumber).HasMaxLength(50);
        b.Property(x => x.Location).HasMaxLength(200);
        b.Property(x => x.PhotoStorageKey).HasMaxLength(200);
        b.Property(x => x.PhotoUrl).HasMaxLength(500);
        b.HasIndex(x => x.IdpSubject).IsUnique();
        // Lookup index only. The UNIQUENESS of a staff email within an organization is enforced by
        // a case-insensitive index over lower("Email"), created in the StaffEmailUniquePerOrg
        // migration — EF cannot express a functional index, and a plain unique index here would be
        // WEAKER than the rule the application already applies: creation and update both compare
        // emails case-insensitively, so a plain index would happily accept "A@b.test" beside
        // "a@b.test" while the app refused it. Email is how a Keycloak identity binds to a portal
        // user, so an ambiguous match there is an authentication problem, not a tidiness one.
        b.HasIndex(x => new { x.MspOrganizationId, x.Email });
        b.HasMany(x => x.Roles).WithOne(r => r.AppUser!).HasForeignKey(r => r.AppUserId);
        // Restrict, not cascade: deleting a manager must not silently delete or orphan-cascade
        // their reports — the manager assignment on a report has to be cleared explicitly first.
        b.HasOne(x => x.Manager).WithMany()
            .HasForeignKey(x => x.ManagerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RoleConfig : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasMany(x => x.Permissions).WithOne(p => p.Role!).HasForeignKey(p => p.RoleId);
        b.HasIndex(x => new { x.MspOrganizationId, x.Name });
    }
}

public sealed class RolePermissionConfig : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.PermissionKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.Scope).HasDefaultValue(Desk.Domain.Authorization.PermissionScope.All);
        b.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique();
    }
}

public sealed class UserRoleConfig : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("user_roles");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.AppUserId, x.RoleId }).IsUnique();
    }
}

public sealed class PsaTechnicianIgnoreConfig : IEntityTypeConfiguration<Desk.Domain.Identity.PsaTechnicianIgnore>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Identity.PsaTechnicianIgnore> b)
    {
        b.ToTable("psa_technician_ignores");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalTechnicianId).HasMaxLength(100).IsRequired();
        b.Property(x => x.ExternalTechnicianName).HasMaxLength(200);
        // A login is ignored or it is not: one row says so.
        b.HasIndex(x => new { x.PsaConnectionId, x.ExternalTechnicianId }).IsUnique();
        b.HasOne(x => x.PsaConnection).WithMany().HasForeignKey(x => x.PsaConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserPsaIdentityConfig : IEntityTypeConfiguration<Desk.Domain.Identity.UserPsaIdentity>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Identity.UserPsaIdentity> b)
    {
        b.ToTable("user_psa_identities");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalTechnicianId).HasMaxLength(100).IsRequired();
        b.Property(x => x.ExternalTechnicianName).HasMaxLength(200);
        // One identity per person per connection — a second row would make "who is this user in
        // Autotask" ambiguous exactly where the answer decides whose timesheet an hour lands on.
        b.HasIndex(x => new { x.AppUserId, x.PsaConnectionId }).IsUnique();
        // And one person per PSA login on a connection. A link says "this login IS this person":
        // with two people on one login, work done in the PSA under it was credited to whichever
        // link was read last.
        b.HasIndex(x => new { x.PsaConnectionId, x.ExternalTechnicianId }).IsUnique();
        b.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PsaConnection).WithMany().HasForeignKey(x => x.PsaConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ActivityEventConfig : IEntityTypeConfiguration<Desk.Domain.Analytics.ActivityEvent>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Analytics.ActivityEvent> b)
    {
        b.ToTable("activity_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.ActorExternalId).HasMaxLength(100);
        b.Property(x => x.Detail).HasMaxLength(500);

        // Every dashboard query is "this tenant, this window", so that pair leads. The others cover
        // the three groupings the analytics layer actually asks for: by person, by client, by ticket.
        b.HasIndex(x => new { x.MspOrganizationId, x.OccurredAt });
        b.HasIndex(x => new { x.MspOrganizationId, x.ActorUserId, x.OccurredAt });
        b.HasIndex(x => new { x.MspOrganizationId, x.ClientCompanyId, x.OccurredAt });
        b.HasIndex(x => x.TicketId);

        // No navigation properties on purpose. An activity row outlives what it describes: a ticket
        // reconciled away as deleted must not take the history of the work done on it with it.
    }
}

public sealed class ActivityDailyFactConfig : IEntityTypeConfiguration<Desk.Domain.Analytics.ActivityDailyFact>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Analytics.ActivityDailyFact> b)
    {
        b.ToTable("activity_daily_facts");
        b.HasKey(x => x.Id);
        b.Property(x => x.ActorExternalId).HasMaxLength(100);

        // Not unique, deliberately. Three of the grain's columns are nullable and Postgres treats
        // NULLs as distinct, so a unique index here would not prevent the duplicates it appears to.
        // Uniqueness is enforced by the rollup deleting and rewriting each day it recomputes.
        b.HasIndex(x => new { x.MspOrganizationId, x.Day });
        b.HasIndex(x => new { x.MspOrganizationId, x.ActorExternalId, x.Day });
        b.HasIndex(x => new { x.MspOrganizationId, x.ClientCompanyId, x.Day });
    }
}

public sealed class TicketConfig : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.ToTable("tickets");
        b.Property(x => x.Resolution).HasMaxLength(4000);
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(500).IsRequired();
        b.Property(x => x.RequesterName).HasMaxLength(200).IsRequired();
        b.Property(x => x.RequesterEmail).HasMaxLength(320).IsRequired();
        b.Property(x => x.PsaTicketType).HasMaxLength(200);
        b.Property(x => x.PsaIssueType).HasMaxLength(200);
        b.Property(x => x.PsaSubIssueType).HasMaxLength(200);
        b.Property(x => x.TimeWorkedHours).HasPrecision(10, 2);
        b.Property(x => x.BillableHours).HasPrecision(10, 2);
        b.Property(x => x.NonBillableHours).HasPrecision(10, 2);
        b.Property(x => x.Version).IsConcurrencyToken();
        // A given PSA ticket appears once per connection.
        b.HasIndex(x => new { x.PsaConnectionId, x.ExternalTicketId })
            .IsUnique()
            .HasFilter("\"ExternalTicketId\" IS NOT NULL");
        b.HasIndex(x => new { x.MspOrganizationId, x.PortalStatus });
        // The paged list's order (newest first), the Overdue / Due soon / Due today counts, the
        // resolved-this-week count, and "raised by me" on the team's boards. Each is a query the list,
        // My Work or the Overview now runs in the database instead of in the browser.
        b.HasIndex(x => new { x.MspOrganizationId, x.CreatedAt });
        b.HasIndex(x => new { x.MspOrganizationId, x.SlaDueAt });
        b.HasIndex(x => new { x.MspOrganizationId, x.ResolvedAt });
        b.HasIndex(x => new { x.MspOrganizationId, x.CreatedByUserId });
        b.HasIndex(x => x.CorrelationId);
        // Client-portal list queries filter by company; dashboard metrics group by technician.
        b.HasIndex(x => x.ClientCompanyId);
        b.HasIndex(x => new { x.MspOrganizationId, x.AssignedTechnicianExternalId });
        // "My tickets" for a portal-only technician runs on this, and it is the query every one of
        // them issues on every page load — the same reason the PSA-side index above exists.
        b.HasIndex(x => new { x.MspOrganizationId, x.AssignedAppUserId });
        b.HasMany(x => x.Notes).WithOne(n => n.Ticket!).HasForeignKey(n => n.TicketId);
        b.HasMany(x => x.Attachments).WithOne(a => a.Ticket!).HasForeignKey(a => a.TicketId);
        // The board lists: one board, newest first, is the query the team lives on all day.
        b.HasIndex(x => new { x.MspOrganizationId, x.BoardId, x.PortalStatus });
        b.Property(x => x.Number).HasMaxLength(20);
        // Quoted to each other in conversation, so it has to be unique within the organization.
        b.HasIndex(x => new { x.MspOrganizationId, x.Number })
            .IsUnique()
            .HasFilter("\"Number\" IS NOT NULL");
        // One ticket per alert per source: a tool repeating the same condition must not open a second.
        b.HasIndex(x => new { x.AlertSourceId, x.SourceAlertId })
            .IsUnique()
            .HasFilter("\"SourceAlertId\" IS NOT NULL");
        b.Property(x => x.SourceAlertId).HasMaxLength(200);
        b.Property(x => x.Source).HasMaxLength(40);
        // A board filtered to one department, which is how a desk organised by department reads it.
        b.HasIndex(x => new { x.MspOrganizationId, x.DepartmentId });
        // A team's own queue: the list every member of that team opens first.
        b.HasIndex(x => new { x.MspOrganizationId, x.AssignedTeamId });
        b.Property(x => x.DeviceExternalId).HasMaxLength(100);
        // A device page lists its tickets.
        b.HasIndex(x => x.DeviceId);
    }
}

public sealed class BoardConfig : IEntityTypeConfiguration<Board>
{
    public void Configure(EntityTypeBuilder<Board> b)
    {
        b.ToTable("boards");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Key).HasMaxLength(8).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        // Two people raising a ticket at the same moment read the same next number. As a concurrency
        // token, the second save fails and retries with the next number instead of colliding on the
        // unique ticket-number index.
        b.Property(x => x.NextNumber).IsConcurrencyToken();
        // The prefix appears in every ticket number on the board, so two boards cannot share one.
        b.HasIndex(x => new { x.MspOrganizationId, x.Key }).IsUnique();
        b.HasMany(x => x.Members).WithOne(m => m.Board!).HasForeignKey(m => m.BoardId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Topics).WithOne(t => t.Board!).HasForeignKey(t => t.BoardId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BoardTopicConfig : IEntityTypeConfiguration<BoardTopic>
{
    public void Configure(EntityTypeBuilder<BoardTopic> b)
    {
        b.ToTable("board_topics");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.DefaultPriority).HasMaxLength(20);
        b.HasIndex(x => new { x.BoardId, x.Name }).IsUnique();
    }
}

public sealed class BoardMemberConfig : IEntityTypeConfiguration<BoardMember>
{
    public void Configure(EntityTypeBuilder<BoardMember> b)
    {
        b.ToTable("board_members");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.BoardId, x.AppUserId }).IsUnique();
    }
}

public sealed class AlertSourceConfig : IEntityTypeConfiguration<AlertSource>
{
    public void Configure(EntityTypeBuilder<AlertSource> b)
    {
        b.ToTable("alert_sources");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.KeyHint).HasMaxLength(16).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(500);
        // Every delivery arrives with a key and nothing else to go on, so this is the lookup.
        b.HasIndex(x => x.KeyHash).IsUnique();
        b.HasOne(x => x.Board).WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
        // The pinned client. Losing the company un-pins the source rather than deleting it.
        b.HasOne<Desk.Domain.Tenancy.ClientCompany>().WithMany().HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class SlaPlanConfig : IEntityTypeConfiguration<SlaPlan>
{
    public void Configure(EntityTypeBuilder<SlaPlan> b)
    {
        b.ToTable("sla_plans");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        // Two plans called "Standard" is a choice nobody can make correctly from a dropdown.
        b.HasIndex(x => new { x.MspOrganizationId, x.Name }).IsUnique();
    }
}

public sealed class TicketTaskConfig : IEntityTypeConfiguration<TicketTask>
{
    public void Configure(EntityTypeBuilder<TicketTask> b)
    {
        b.ToTable("ticket_tasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        // One ticket's list, in order: the only way these are ever read.
        b.HasIndex(x => new { x.TicketId, x.SortOrder });
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TicketLinkConfig : IEntityTypeConfiguration<TicketLink>
{
    public void Configure(EntityTypeBuilder<TicketLink> b)
    {
        b.ToTable("ticket_links");
        b.HasKey(x => x.Id);
        // Read from either end: a ticket shows the links it made and the links made to it.
        b.HasIndex(x => new { x.FromTicketId, x.ToTicketId, x.Kind }).IsUnique();
        b.HasIndex(x => x.ToTicketId);
        b.HasOne(x => x.FromTicket).WithMany().HasForeignKey(x => x.FromTicketId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ToTicket).WithMany().HasForeignKey(x => x.ToTicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

// ---- Workforce: working schedules and skills ----------------------------------------------------

public sealed class WorkScheduleConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkSchedule>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkSchedule> b)
    {
        b.ToTable("work_schedules");
        b.HasKey(x => x.Id);
        b.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
        // One version per person per starting day; "the version in force on a date" reads this index.
        b.HasIndex(x => new { x.AppUserId, x.EffectiveFrom }).IsUnique();
        b.HasIndex(x => x.MspOrganizationId);
        // The schedule is the person's own configuration: it goes when they do.
        b.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Days).WithOne(d => d.WorkSchedule!).HasForeignKey(d => d.WorkScheduleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkScheduleDayConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkScheduleDay>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkScheduleDay> b)
    {
        b.ToTable("work_schedule_days");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.WorkScheduleId, x.Day }).IsUnique();
        b.HasMany(x => x.Breaks).WithOne(k => k.WorkScheduleDay!).HasForeignKey(k => k.WorkScheduleDayId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkScheduleBreakConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkScheduleBreak>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkScheduleBreak> b)
    {
        b.ToTable("work_schedule_breaks");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.WorkScheduleDayId);
    }
}

public sealed class SkillConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.Skill>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.Skill> b)
    {
        b.ToTable("skills");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.NormalizedName).HasMaxLength(80).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300);
        // "SonicWall" and "sonicwall " are one skill within an organization.
        b.HasIndex(x => new { x.MspOrganizationId, x.NormalizedName }).IsUnique();
    }
}

public sealed class StaffSkillConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.StaffSkill>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.StaffSkill> b)
    {
        b.ToTable("staff_skills");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.AppUserId, x.SkillId }).IsUnique();
        b.HasIndex(x => x.SkillId);
        b.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
        // A skill is retired, never deleted, while anyone holds it.
        b.HasOne(x => x.Skill).WithMany().HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CapacityExceptionConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.CapacityException>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.CapacityException> b)
    {
        b.ToTable("capacity_exceptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
        b.Property(x => x.Note).HasMaxLength(200);
        // "This person's exceptions around these dates" - the only way capacity ever reads them. Both
        // shapes carry dates (a part-day one, the dates it starts and ends on), so one index serves both.
        b.HasIndex(x => new { x.AppUserId, x.FromDate, x.ToDate });
        b.HasIndex(x => x.MspOrganizationId);
        // The person's own availability: it goes when they do.
        b.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkAllocationConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkAllocation>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkAllocation> b)
    {
        b.ToTable("work_allocations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Note).HasMaxLength(300);
        b.Property(x => x.OverrideReason).HasMaxLength(300);
        b.Property(x => x.OverriddenConflicts).HasMaxLength(200);
        b.Property(x => x.CancelReason).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Ignore(x => x.When);
        // "This person's plan around these dates" is every read the capacity engine and My Plan
        // make; "what is planned on this ticket" is the ticket page's.
        b.HasIndex(x => new { x.AppUserId, x.StartsAt });
        b.HasIndex(x => x.TicketId);
        b.HasIndex(x => new { x.MspOrganizationId, x.StartsAt });
        // Planned time goes with the work and with the person: a deleted ticket or account leaves
        // no plan behind that nobody could open.
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkPlanningConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkPlanning>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkPlanning> b)
    {
        b.ToTable("work_planning");
        b.HasKey(x => x.Id);
        b.Property(x => x.Note).HasMaxLength(300);
        // One planning row per piece of work; the ticket is the system of record and takes the row with it.
        b.HasIndex(x => x.TicketId).IsUnique();
        b.HasIndex(x => x.MspOrganizationId);
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkSessionConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkSession>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkSession> b)
    {
        b.ToTable("work_sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Note).HasMaxLength(2000);
        b.Property(x => x.Version).IsConcurrencyToken();
        // "What is this person doing now": the lookup every screen makes.
        b.HasIndex(x => new { x.AppUserId, x.Status });
        // One running clock per person, held by the database itself where it supports a partial
        // index (PostgreSQL and SQLite); the person's gate holds it everywhere else.
        b.HasIndex(x => x.AppUserId).HasDatabaseName("IX_work_sessions_one_active").IsUnique().HasFilter("\"Status\" = 1");
        // A person's day, and a ticket's sessions.
        b.HasIndex(x => new { x.MspOrganizationId, x.AppUserId, x.StartedAt });
        b.HasIndex(x => x.TicketId);
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Segments).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkSessionSegmentConfig : IEntityTypeConfiguration<Desk.Domain.Workforce.WorkSessionSegment>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Workforce.WorkSessionSegment> b)
    {
        b.ToTable("work_session_segments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.SessionId, x.StartedAt });
    }
}

public sealed class CannedResponseConfig : IEntityTypeConfiguration<CannedResponse>
{
    public void Configure(EntityTypeBuilder<CannedResponse> b)
    {
        b.ToTable("canned_responses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.Body).HasMaxLength(10000).IsRequired();
        b.HasIndex(x => new { x.MspOrganizationId, x.BoardId, x.Name }).IsUnique();
    }
}

public sealed class TicketSatisfactionConfig : IEntityTypeConfiguration<TicketSatisfaction>
{
    public void Configure(EntityTypeBuilder<TicketSatisfaction> b)
    {
        b.ToTable("ticket_satisfaction");
        b.HasKey(x => x.Id);
        b.Property(x => x.Comment).HasMaxLength(1000);
        b.Property(x => x.TechnicianExternalId).HasMaxLength(100);
        b.Property(x => x.TechnicianName).HasMaxLength(200);
        // One answer per ticket: a changed mind replaces the rating, it does not add a second vote.
        b.HasIndex(x => x.TicketId).IsUnique();
        // Every report asks "rated between these dates".
        b.HasIndex(x => new { x.MspOrganizationId, x.RatedAt });
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TicketApprovalConfig : IEntityTypeConfiguration<TicketApproval>
{
    public void Configure(EntityTypeBuilder<TicketApproval> b)
    {
        b.ToTable("ticket_approvals");
        b.HasKey(x => x.Id);
        b.Property(x => x.ApproverName).HasMaxLength(200);
        b.Property(x => x.ApproverEmail).HasMaxLength(320);
        b.Property(x => x.Request).HasMaxLength(TicketApproval.MaxRequestLength);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.DecisionComment).HasMaxLength(TicketApproval.MaxCommentLength);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        // The ticket page reads its approvals; the approver's list reads what is waiting at their company.
        b.HasIndex(x => x.TicketId);
        b.HasIndex(x => new { x.ClientCompanyId, x.State });
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class KbArticleConfig : IEntityTypeConfiguration<Desk.Domain.Knowledge.KbArticle>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Knowledge.KbArticle> b)
    {
        b.ToTable("kb_articles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(Desk.Domain.Knowledge.KbArticle.MaxTitleLength).IsRequired();
        b.Property(x => x.Body).HasMaxLength(Desk.Domain.Knowledge.KbArticle.MaxBodyLength);
        b.Property(x => x.Category).HasMaxLength(Desk.Domain.Knowledge.KbArticle.MaxCategoryLength);
        b.Property(x => x.AuthorName).HasMaxLength(200);
        b.Property(x => x.UpdatedByName).HasMaxLength(200);
        b.HasMany(x => x.Clients).WithOne(x => x.Article).HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class KbArticleClientConfig : IEntityTypeConfiguration<Desk.Domain.Knowledge.KbArticleClient>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Knowledge.KbArticleClient> b)
    {
        b.ToTable("kb_article_clients");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ArticleId, x.ClientCompanyId }).IsUnique();
        b.HasIndex(x => x.ClientCompanyId);
    }
}

public sealed class KbDeflectionConfig : IEntityTypeConfiguration<Desk.Domain.Knowledge.KbDeflection>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Knowledge.KbDeflection> b)
    {
        b.ToTable("kb_deflections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Query).HasMaxLength(200);
        // The stats read "since a date"; the article list reads counts per article.
        b.HasIndex(x => new { x.MspOrganizationId, x.OccurredAt });
        b.HasIndex(x => new { x.Source, x.ArticleId });
    }
}

public sealed class PushSubscriptionConfig : IEntityTypeConfiguration<Desk.Domain.Notifications.PushSubscription>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Notifications.PushSubscription> b)
    {
        b.ToTable("push_subscriptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Endpoint).HasMaxLength(1000).IsRequired();
        b.Property(x => x.P256dh).HasMaxLength(200).IsRequired();
        b.Property(x => x.Auth).HasMaxLength(100).IsRequired();
        b.Property(x => x.DeviceLabel).HasMaxLength(80);
        // One browser, one row, whoever signed it up last.
        b.HasIndex(x => x.Endpoint).IsUnique();
        b.HasIndex(x => x.AppUserId);
    }
}

public sealed class PushPreferenceConfig : IEntityTypeConfiguration<Desk.Domain.Notifications.PushPreference>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Notifications.PushPreference> b)
    {
        b.ToTable("push_preferences");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.AppUserId).IsUnique();
    }
}

public sealed class PushTicketStateConfig : IEntityTypeConfiguration<Desk.Domain.Notifications.PushTicketState>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Notifications.PushTicketState> b)
    {
        b.ToTable("push_ticket_states");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.TicketId).IsUnique();
    }
}

public sealed class PushNotificationConfig : IEntityTypeConfiguration<Desk.Domain.Notifications.PushNotification>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Notifications.PushNotification> b)
    {
        b.ToTable("push_notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(500).IsRequired();
        b.Property(x => x.Url).HasMaxLength(300).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(300);
        // The sender reads what is unsent, oldest first; the prune reads by age.
        b.HasIndex(x => new { x.SentAt, x.CreatedAt });
        b.HasIndex(x => new { x.AppUserId, x.CreatedAt });
    }
}

public sealed class DeskHolidayConfig : IEntityTypeConfiguration<DeskHoliday>
{
    public void Configure(EntityTypeBuilder<DeskHoliday> b)
    {
        b.ToTable("desk_holidays");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        // One entry per day: two names for the same closed day is a list nobody can trust.
        b.HasIndex(x => new { x.MspOrganizationId, x.Date }).IsUnique();
    }
}

public sealed class RecurringTicketConfig : IEntityTypeConfiguration<RecurringTicket>
{
    public void Configure(EntityTypeBuilder<RecurringTicket> b)
    {
        b.ToTable("recurring_tickets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(500).IsRequired();
        b.Property(x => x.Priority).HasMaxLength(20);
        b.Property(x => x.Checklist).HasMaxLength(6000);
        b.Property(x => x.LastOutcome).HasMaxLength(500);
        // The worker's only question, asked every few minutes across every tenant.
        b.HasIndex(x => new { x.IsActive, x.NextRunAt });
        b.HasOne(x => x.Board).WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TicketFollowerConfig : IEntityTypeConfiguration<TicketFollower>
{
    public void Configure(EntityTypeBuilder<TicketFollower> b)
    {
        b.ToTable("ticket_followers");
        b.HasKey(x => x.Id);
        // Following twice is the same as following once, and the UI must not have to de-duplicate it.
        b.HasIndex(x => new { x.TicketId, x.AppUserId }).IsUnique();
        // "What am I following" is the query behind a whole saved view, run on every page load.
        b.HasIndex(x => new { x.MspOrganizationId, x.AppUserId });
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SavedTicketViewConfig : IEntityTypeConfiguration<SavedTicketView>
{
    public void Configure(EntityTypeBuilder<SavedTicketView> b)
    {
        b.ToTable("saved_ticket_views");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Search).HasMaxLength(200);
        b.Property(x => x.Status).HasMaxLength(50);
        b.Property(x => x.Priority).HasMaxLength(20);
        b.Property(x => x.Company).HasMaxLength(300);
        b.Property(x => x.Queue).HasMaxLength(200);
        b.Property(x => x.ConnectionName).HasMaxLength(200);
        b.Property(x => x.PersonKey).HasMaxLength(80);
        b.Property(x => x.Openness).HasMaxLength(20);
        // One name per owner per list: a second "Overdue" of my own is a mistake, not a variant.
        // Two people may each have their own, which is why the owner is part of it.
        b.HasIndex(x => new { x.MspOrganizationId, x.OwnerUserId, x.BoardId, x.Name }).IsUnique();
    }
}

public sealed class TicketAssignmentConfig : IEntityTypeConfiguration<TicketAssignment>
{
    public void Configure(EntityTypeBuilder<TicketAssignment> b)
    {
        b.ToTable("ticket_assignments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Note).HasMaxLength(2000);
        b.HasIndex(x => new { x.TicketId, x.CreatedAt });
        b.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AssistantSettingsConfig : IEntityTypeConfiguration<Desk.Domain.Assistant.AssistantSettings>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Assistant.AssistantSettings> b)
    {
        b.ToTable("assistant_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.CredentialSecretRef).HasMaxLength(200);
        b.Property(x => x.Model).HasMaxLength(100).IsRequired();
        // One row per tenant: the settings ARE the organization's, not a list to choose from.
        b.HasIndex(x => x.MspOrganizationId).IsUnique();
    }
}

public sealed class TicketNoteConfig : IEntityTypeConfiguration<TicketNote>
{
    public void Configure(EntityTypeBuilder<TicketNote> b)
    {
        b.ToTable("ticket_notes");
        b.HasKey(x => x.Id);
        b.Property(x => x.AuthorName).HasMaxLength(200).IsRequired();
        b.Property(x => x.AuthorExternalId).HasMaxLength(64);
        b.Property(x => x.Body).IsRequired();
        b.HasIndex(x => x.TicketId);
    }
}

public sealed class TicketAttachmentConfig : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> b)
    {
        b.ToTable("ticket_attachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.OriginalFileName).HasMaxLength(400).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(200).IsRequired();
        b.Property(x => x.StorageObjectKey).HasMaxLength(400).IsRequired();
        b.Property(x => x.AuthorExternalId).HasMaxLength(64);
        b.HasIndex(x => x.TicketId);
        b.HasIndex(x => x.TicketNoteId);
    }
}

public sealed class TicketTimeEntryConfig : IEntityTypeConfiguration<TicketTimeEntry>
{
    public void Configure(EntityTypeBuilder<TicketTimeEntry> b)
    {
        b.ToTable("ticket_time_entries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Hours).HasPrecision(9, 4);
        b.Property(x => x.ExternalEntryId).HasMaxLength(100);
        b.Property(x => x.TechnicianName).HasMaxLength(200);
        b.Property(x => x.WorkTypeLabel).HasMaxLength(200);
        b.HasIndex(x => x.TicketId);
        // Reconciling a provider read against portal rows is a lookup by the PSA's own id.
        b.HasIndex(x => x.ExternalEntryId);
        // "Hours this technician logged between two dates" is the query every productivity report
        // runs, per person, per range — so it gets the date alongside the person.
        b.HasIndex(x => new { x.MspOrganizationId, x.AppUserId, x.EntryDate });
        // A stopped clock writes exactly one entry: a second write for the same session is refused by the database.
        b.HasIndex(x => x.WorkSessionId).IsUnique().HasFilter("\"WorkSessionId\" IS NOT NULL");
    }
}

public sealed class FieldMappingConfig : IEntityTypeConfiguration<FieldMapping>
{
    public void Configure(EntityTypeBuilder<FieldMapping> b)
    {
        b.ToTable("field_mappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.PortalField).HasMaxLength(100).IsRequired();
        b.Property(x => x.ExternalField).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.MspOrganizationId, x.Provider, x.Scope });
    }
}

public sealed class FieldMappingVersionConfig : IEntityTypeConfiguration<FieldMappingVersion>
{
    public void Configure(EntityTypeBuilder<FieldMappingVersion> b)
    {
        b.ToTable("field_mapping_versions");
        b.HasKey(x => x.Id);
        b.Property(x => x.SnapshotJson).IsRequired();
        b.HasIndex(x => new { x.MspOrganizationId, x.Provider, x.PsaConnectionId, x.Version });
    }
}

public sealed class SyncEventConfig : IEntityTypeConfiguration<SyncEvent>
{
    public void Configure(EntityTypeBuilder<SyncEvent> b)
    {
        b.ToTable("sync_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.SourceMarker).HasMaxLength(20).IsRequired();
        // Duplicate deliveries of the same source event are dropped by this constraint.
        b.HasIndex(x => new { x.PsaConnectionId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => x.TicketId);
    }
}

public sealed class BackgroundJobConfig : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> b)
    {
        b.ToTable("background_jobs");
        b.HasKey(x => x.Id);
        b.Property(x => x.JobType).HasMaxLength(100).IsRequired();
        b.Property(x => x.PayloadJson).IsRequired();
        // A database default, so a job queued by the previous version of the code - which does
        // not know the column - starts at zero like any other.
        b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(0);
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}

public sealed class SyncCursorConfig : IEntityTypeConfiguration<SyncCursor>
{
    public void Configure(EntityTypeBuilder<SyncCursor> b)
    {
        b.ToTable("sync_cursors");
        b.HasKey(x => x.Id);
        b.Property(x => x.Entity).HasMaxLength(40).IsRequired();
        // A provider's page cursor can be a whole URL (Autotask's is).
        b.Property(x => x.Continuation).HasMaxLength(4000);
        b.HasIndex(x => new { x.PsaConnectionId, x.Entity }).IsUnique();
        b.HasOne<PsaConnection>().WithMany().HasForeignKey(x => x.PsaConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SyncRunConfig : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> b)
    {
        b.ToTable("sync_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.Property(x => x.Notice).HasMaxLength(1000);
        b.Property(x => x.RequestedBy).HasMaxLength(200);
        // The lock. A second run for a connection that already has one in progress cannot be
        // recorded, so it cannot start - whichever process it is in.
        b.HasIndex(x => x.PsaConnectionId).HasDatabaseName("IX_sync_runs_one_running").IsUnique().HasFilter("\"Status\" = 0");
        b.HasIndex(x => new { x.PsaConnectionId, x.StartedAt });
        b.HasOne<PsaConnection>().WithMany().HasForeignKey(x => x.PsaConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SyncFailureConfig : IEntityTypeConfiguration<SyncFailure>
{
    public void Configure(EntityTypeBuilder<SyncFailure> b)
    {
        b.ToTable("sync_failures");
        b.HasKey(x => x.Id);
        b.Property(x => x.Entity).HasMaxLength(40).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
        b.Property(x => x.Operation).HasMaxLength(40).IsRequired();
        b.Property(x => x.Category).HasMaxLength(40).IsRequired();
        b.Property(x => x.Message).HasMaxLength(1000).IsRequired();
        // One open failure per record and operation: failing again updates it rather than adding a row.
        b.HasIndex(x => new { x.PsaConnectionId, x.Entity, x.ExternalId, x.Operation })
            .HasDatabaseName("IX_sync_failures_one_open").IsUnique().HasFilter("\"Status\" IN (0, 2)");
        b.HasIndex(x => new { x.PsaConnectionId, x.Status, x.NextAttemptAt });
        b.HasOne<PsaConnection>().WithMany().HasForeignKey(x => x.PsaConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TicketInstructionConfig : IEntityTypeConfiguration<TicketInstruction>
{
    public void Configure(EntityTypeBuilder<TicketInstruction> b)
    {
        b.ToTable("ticket_instructions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.LastEditedBy).HasMaxLength(200);
        // Exactly one instruction row per scope: the org-wide default (null company) and one per account.
        // A filtered unique index would be ideal, but SQLite (local mode) and the org-default NULL make a
        // plain composite index the portable choice; upsert logic enforces single-row semantics.
        b.HasIndex(x => new { x.MspOrganizationId, x.ClientCompanyId }).IsUnique();
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ClientAccessGrantConfig : IEntityTypeConfiguration<ClientAccessGrant>
{
    public void Configure(EntityTypeBuilder<ClientAccessGrant> b)
    {
        b.ToTable("client_access_grants");
        b.HasKey(x => x.Id);
        // One grant per (user, section, account-scope). Null company = all accounts.
        b.HasIndex(x => new { x.ClientUserId, x.Section, x.ClientCompanyId }).IsUnique();
        b.HasOne(x => x.ClientUser).WithMany()
            .HasForeignKey(x => x.ClientUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ApproverConfig : IEntityTypeConfiguration<Approver>
{
    public void Configure(EntityTypeBuilder<Approver> b)
    {
        b.ToTable("approvers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Scope).HasMaxLength(500);
        b.HasIndex(x => x.ClientCompanyId);
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EscalationLevelConfig : IEntityTypeConfiguration<EscalationLevel>
{
    public void Configure(EntityTypeBuilder<EscalationLevel> b)
    {
        b.ToTable("escalation_levels");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Contact).HasMaxLength(300);
        b.Property(x => x.Condition).HasMaxLength(500);
        b.HasIndex(x => x.ClientCompanyId);
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class HolidayConfig : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> b)
    {
        b.ToTable("holidays");
        b.HasKey(x => x.Id);
        b.Property(x => x.Date).HasMaxLength(10).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.ClientCompanyId);
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceConfig : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Type).HasMaxLength(100);
        b.Property(x => x.Identifier).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.ExternalId).HasMaxLength(100);
        // Existing rows are devices somebody listed on purpose: they are active, not retired.
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Ignore(x => x.FromPsa);
        b.HasIndex(x => x.ClientCompanyId);
        // The sync matches on the PSA's own id, per connection.
        b.HasIndex(x => new { x.PsaConnectionId, x.ExternalId });
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BusinessHoursConfig : IEntityTypeConfiguration<BusinessHours>
{
    public void Configure(EntityTypeBuilder<BusinessHours> b)
    {
        b.ToTable("business_hours");
        b.HasKey(x => x.Id);
        b.Property(x => x.TimeZone).HasMaxLength(100);
        b.Property(x => x.ScheduleJson).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(1000);
        // One business-hours row per account.
        b.HasIndex(x => x.ClientCompanyId).IsUnique();
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AnnouncementConfig : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> b)
    {
        b.ToTable("announcements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.AuthorName).HasMaxLength(200);
        b.HasIndex(x => x.ClientCompanyId);
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FaqArticleConfig : IEntityTypeConfiguration<FaqArticle>
{
    public void Configure(EntityTypeBuilder<FaqArticle> b)
    {
        b.ToTable("faq_articles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Question).HasMaxLength(500).IsRequired();
        b.Property(x => x.Answer).IsRequired();
        b.Property(x => x.Category).HasMaxLength(100);
        b.HasIndex(x => x.ClientCompanyId);
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ReportScheduleConfig : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> b)
    {
        b.ToTable("report_schedules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Recipients).HasMaxLength(2000);
        b.HasIndex(x => x.ClientCompanyId);
        // The worker scans for due, enabled schedules.
        b.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrganizationEmailSettingsConfig : IEntityTypeConfiguration<OrganizationEmailSettings>
{
    public void Configure(EntityTypeBuilder<OrganizationEmailSettings> b)
    {
        b.ToTable("organization_email_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Method).HasMaxLength(10).IsRequired().HasDefaultValue("Smtp");
        b.Property(x => x.GraphTenantId).HasMaxLength(100);
        b.Property(x => x.GraphClientId).HasMaxLength(36);
        b.Property(x => x.Host).HasMaxLength(253).IsRequired();
        b.Property(x => x.Security).HasMaxLength(20).IsRequired();
        b.Property(x => x.Username).HasMaxLength(320);
        b.Property(x => x.PasswordSecretRef).HasMaxLength(200);
        b.Property(x => x.FromAddress).HasMaxLength(320).IsRequired();
        b.Property(x => x.FromName).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.MspOrganizationId).IsUnique(); // one account per organization
    }
}

public sealed class StaffReportScheduleConfig : IEntityTypeConfiguration<Desk.Domain.Reporting.StaffReportSchedule>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Reporting.StaffReportSchedule> b)
    {
        b.ToTable("staff_report_schedules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Recipients).HasMaxLength(2000);
        b.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
        b.HasOne<ClientCompany>().WithMany().HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StaffReportRunConfig : IEntityTypeConfiguration<Desk.Domain.Reporting.StaffReportRun>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Reporting.StaffReportRun> b)
    {
        b.ToTable("staff_report_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(500);
        b.Property(x => x.DeliveryNote).HasMaxLength(500);
        b.HasIndex(x => x.GeneratedAt);
        // A deleted schedule keeps its history: the runs were sent, and what was sent stays downloadable.
        b.HasOne<Desk.Domain.Reporting.StaffReportSchedule>().WithMany()
            .HasForeignKey(x => x.StaffReportScheduleId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ClientCompany>().WithMany().HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ReportRunConfig : IEntityTypeConfiguration<ReportRun>
{
    public void Configure(EntityTypeBuilder<ReportRun> b)
    {
        b.ToTable("report_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Format).HasMaxLength(20).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(500);
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.DeliveryNote).HasMaxLength(500);
        b.HasIndex(x => new { x.ClientCompanyId, x.GeneratedAt });
    }
}

public sealed class ClientBrandingConfig : IEntityTypeConfiguration<ClientBranding>
{
    public void Configure(EntityTypeBuilder<ClientBranding> b)
    {
        b.ToTable("client_branding");
        b.HasKey(x => x.Id);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.LogoUrl).HasMaxLength(1000);
        b.Property(x => x.AccentColor).HasMaxLength(20);
        // One branding row per account.
        b.HasIndex(x => x.ClientCompanyId).IsUnique();
        b.HasOne(x => x.ClientCompany).WithMany()
            .HasForeignKey(x => x.ClientCompanyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AuditLogEntryConfig : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.ToTable("audit_log");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.MspOrganizationId, x.CreatedAt });
        b.HasIndex(x => x.CorrelationId);
    }
}

public sealed class EnquiryConfig : IEntityTypeConfiguration<Desk.Domain.Marketing.Enquiry>
{
    public void Configure(EntityTypeBuilder<Desk.Domain.Marketing.Enquiry> b)
    {
        b.ToTable("enquiries");
        b.HasKey(x => x.Id);
        // The same constants the submission check uses, so the column and the rule cannot drift.
        // This comment used to claim an oversized field was refused rather than truncated; it was
        // the validator that clipped, and the column simply never saw the part it had removed.
        b.Property(x => x.Name).HasMaxLength(Enquiry.NameMax).IsRequired();
        b.Property(x => x.Email).HasMaxLength(Enquiry.EmailMax).IsRequired();
        b.Property(x => x.Company).HasMaxLength(Enquiry.CompanyMax);
        b.Property(x => x.Phone).HasMaxLength(Enquiry.PhoneMax);
        b.Property(x => x.Message).HasMaxLength(Enquiry.MessageMax).IsRequired();
        b.Property(x => x.PreferredTime).HasMaxLength(Enquiry.PreferredTimeMax);
        b.Property(x => x.SourcePage).HasMaxLength(Enquiry.SourcePageMax);
        // The list is read newest-first and filtered by status; this is that query.
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
