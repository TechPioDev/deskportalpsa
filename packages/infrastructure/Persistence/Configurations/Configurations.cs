using Desk.Domain.Audit;
using Desk.Domain.ControlPanel;
using Desk.Domain.Identity;
using Desk.Domain.Mapping;
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
        b.ToTable("msp_organizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(100).IsRequired();
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
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(500).IsRequired();
        b.Property(x => x.RequesterName).HasMaxLength(200).IsRequired();
        b.Property(x => x.RequesterEmail).HasMaxLength(320).IsRequired();
        b.Property(x => x.TimeWorkedHours).HasPrecision(10, 2);
        b.Property(x => x.BillableHours).HasPrecision(10, 2);
        b.Property(x => x.NonBillableHours).HasPrecision(10, 2);
        b.Property(x => x.Version).IsConcurrencyToken();
        // A given PSA ticket appears once per connection.
        b.HasIndex(x => new { x.PsaConnectionId, x.ExternalTicketId })
            .IsUnique()
            .HasFilter("\"ExternalTicketId\" IS NOT NULL");
        b.HasIndex(x => new { x.MspOrganizationId, x.PortalStatus });
        b.HasIndex(x => x.CorrelationId);
        // Client-portal list queries filter by company; dashboard metrics group by technician.
        b.HasIndex(x => x.ClientCompanyId);
        b.HasIndex(x => new { x.MspOrganizationId, x.AssignedTechnicianExternalId });
        b.HasMany(x => x.Notes).WithOne(n => n.Ticket!).HasForeignKey(n => n.TicketId);
        b.HasMany(x => x.Attachments).WithOne(a => a.Ticket!).HasForeignKey(a => a.TicketId);
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
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
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
        b.HasIndex(x => x.ClientCompanyId);
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
        // Caps match the API's validation, so an oversized field is refused rather than truncated.
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Email).HasMaxLength(200).IsRequired();
        b.Property(x => x.Company).HasMaxLength(160);
        b.Property(x => x.Phone).HasMaxLength(60);
        b.Property(x => x.Message).HasMaxLength(4000).IsRequired();
        b.Property(x => x.PreferredTime).HasMaxLength(200);
        b.Property(x => x.SourcePage).HasMaxLength(200);
        // The list is read newest-first and filtered by status; this is that query.
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
