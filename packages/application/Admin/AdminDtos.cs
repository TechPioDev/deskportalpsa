using Desk.Domain.Authorization;
using Desk.Domain.Enums;

namespace Desk.Application.Admin;

/// <summary>PSA connection as shown to admins. Deliberately has NO credential field — secrets stay in the encrypted store.</summary>
public sealed record ConnectionSummary(
    Guid Id,
    string Name,
    ProviderType Provider,
    string ApiEndpoint,
    string? TenantIdentifier,
    ConnectionStatus Status,
    bool IsEnabled,
    DateTimeOffset? LastSuccessfulSyncAt,
    string? LastError,
    // Counts drawn from what has actually synced, so the card cannot show a number the portal
    // does not hold. Trailing + defaulted so existing construction sites are unaffected.
    DateTimeOffset? LastHealthCheckAt = null,
    int TicketCount = 0,
    int CustomerCount = 0,
    int ContactCount = 0,
    string? LogoUrl = null,
    // NAMES of the credential fields that currently hold a stored value — never the values, which
    // stay write-only by design. Exists so the edit form can distinguish "leave blank to keep the
    // existing key" from "there is nothing stored to keep": after the Vault-era secret loss, both
    // rendered as the same 'unchanged' placeholder and an admin could not tell whether their
    // re-entry had actually landed.
    IReadOnlyList<string>? StoredCredentialKeys = null);

public sealed record CreateConnectionInput(
    string Name,
    ProviderType Provider,
    string ApiEndpoint,
    string? TenantIdentifier,
    IReadOnlyDictionary<string, string> Credentials,
    string? TimeZone,
    string? LogoUrl = null);

public sealed record MappingRuleDto(
    Guid Id,
    ProviderType Provider,
    MappingScope Scope,
    Guid? PsaConnectionId,
    string PortalField,
    string? PortalValue,
    string ExternalField,
    string? ExternalValue,
    MappingDirection Direction,
    bool IsRequired,
    string? FallbackValue,
    bool IsActive,
    int Version);

public sealed record UpsertMappingInput(
    Guid? Id,
    ProviderType Provider,
    MappingScope Scope,
    Guid? PsaConnectionId,
    string PortalField,
    string? PortalValue,
    string ExternalField,
    string? ExternalValue,
    MappingDirection Direction,
    bool IsRequired,
    string? FallbackValue);

public sealed record MappingVersionDto(Guid Id, ProviderType Provider, Guid? PsaConnectionId, int Version, string ChangedByUserId, string? ChangeNote, DateTimeOffset CreatedAt);

public sealed record JobSummary(
    Guid Id,
    string JobType,
    BackgroundJobStatus Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt);

public sealed record AuditEntryDto(
    Guid Id,
    string Action,
    string EntityType,
    string? EntityId,
    string? ActorDisplayName,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    string? DetailJson);

public sealed record ConnectionHealthDto(
    Guid ConnectionId,
    string Name,
    ProviderType Provider,
    ConnectionStatus Status,
    DateTimeOffset? LastSuccessfulSyncAt,
    DateTimeOffset? LastHealthCheckAt,
    int PendingJobs,
    int DeadLetterJobs,
    int FailedSyncEvents,
    string? LastError);

/// <summary>A ticket the portal holds that never reached the PSA.</summary>
public sealed record UnsyncedTicketDto(
    Guid TicketId,
    Guid PsaConnectionId,
    string ConnectionName,
    string Title,
    string? CustomerName,
    string SyncStatus,
    string? SyncError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt);

/// <summary>How many are outstanding, and which they are.</summary>
public sealed record UnsyncedTicketsDto(int Count, IReadOnlyList<UnsyncedTicketDto> Tickets);

/// <summary>Outcome of a single resync attempt.</summary>
public sealed record ResyncResultDto(bool Success, Guid TicketId, string? ExternalTicketId, string? Error);

/// <summary>A role a staff user can hold, by id (for assignment) and name (for display).</summary>
public sealed record RoleOptionDto(Guid Id, string Name);

/// <summary>A department as referenced from a user (no nested teams — that would be redundant
/// with UserSummary's own Teams list). See DepartmentWithTeamsDto for the standalone listing.</summary>
public sealed record DepartmentOptionDto(Guid Id, string Name);
public sealed record TeamOptionDto(Guid Id, string Name, Guid DepartmentId);
public sealed record DepartmentWithTeamsDto(Guid Id, string Name, IReadOnlyList<TeamOptionDto> Teams);

/// <summary>A board is a PSA-synced queue/board name, not a stored entity — see PsaTicketLink /
/// the Phase-1 design note. Grouped by connection because the same board name can exist under
/// more than one PSA connection without meaning the same thing.</summary>
public sealed record BoardOptionDto(Guid PsaConnectionId, string ConnectionName, string BoardName);

/// <summary>A team as shown on the Departments & Teams admin page — includes the fields the picker's
/// TeamOptionDto deliberately omits (IsActive, UserCount) because a picker only ever offers active,
/// assignable teams and doesn't need to explain why one is missing.</summary>
public sealed record TeamManageDto(Guid Id, Guid DepartmentId, string Name, bool IsActive, int SortOrder, int UserCount);

/// <summary>A department as shown on the Departments & Teams admin page. Separate from
/// DepartmentWithTeamsDto (the picker used by Add User / User Details) because the admin page needs
/// to see and reactivate INACTIVE departments too, not just offer active ones for assignment.</summary>
public sealed record DepartmentManageDto(
    Guid Id, string Name, string? Description, bool IsActive, bool IsSystemDefault, int SortOrder,
    IReadOnlyList<TeamManageDto> Teams, int PrimaryUserCount, int SecondaryUserCount);

public sealed record CreateDepartmentInput(string Name, string? Description);
public sealed record UpdateDepartmentInput(string Name, string? Description);
public sealed record CreateTeamInput(Guid DepartmentId, string Name);
public sealed record UpdateTeamInput(string Name);

public sealed record PermissionTemplateOptionDto(Guid Id, string Name, string? Description, RoleType BaseRoleType);

/// <summary>One catalogue entry, as the Roles &amp; Permissions matrix renders it — which scopes are
/// legal for the key, so the UI can only offer what enforcement can honour.</summary>
public sealed record PermissionDefinitionDto(
    string Key, string Module, string DisplayName,
    IReadOnlyList<PermissionScope> SupportedScopes, PermissionScope DefaultScope, bool IsBoardAware);

public sealed record RoleGrantDto(string PermissionKey, PermissionScope Scope);

/// <summary>HeldByCaller drives the UI's self-escalation guard: a role you hold is read-only to
/// you, because editing it is editing your own permissions.</summary>
public sealed record RoleDetailDto(
    Guid Id, string Name, bool IsSystemRole, RoleType? BuiltInType, int UserCount, bool HeldByCaller,
    IReadOnlyList<RoleGrantDto> Grants);

public sealed record SaveRoleInput(string Name, IReadOnlyList<RoleGrantDto> Grants);

/// <summary>One user's row on the org-wide Effective Permissions screen: their resolved access to
/// ONE permission, with enough provenance to explain it — the resolved answer comes from the same
/// engine enforcement uses, and ViaRoles names which of their roles grant the key at all.</summary>
public sealed record UserEffectivePermissionDto(
    Guid UserId, string DisplayName, string Email, string? PhotoUrl, bool IsActive,
    PermissionScope Scope, string Source, string BoardAccessMode, IReadOnlyList<string> ViaRoles);

/// <summary>
/// SignInLinked distinguishes "can log in today" from "invited, binds on first login": a created
/// user has no IdP subject until they first sign in and their verified email matches.
///
/// LastActiveAt is the one real activity signal this app tracks (see AppUser.LastActiveAt) — it is
/// NOT a login-event timestamp, because the app has no session/login-event log to draw one from.
/// </summary>
public sealed record UserSummary(
    Guid Id, string Email, string DisplayName, bool IsActive, bool SignInLinked,
    IReadOnlyList<RoleOptionDto> Roles,
    string? PhoneNumber,
    string? Location,
    string? PhotoUrl,
    Guid? ManagerId,
    string? ManagerName,
    DepartmentOptionDto? PrimaryDepartment,
    IReadOnlyList<DepartmentOptionDto> SecondaryDepartments,
    IReadOnlyList<TeamOptionDto> Teams,
    BoardAccessMode BoardAccessMode,
    IReadOnlyList<BoardOptionDto> BoardGrants,
    DateTimeOffset? LastActiveAt,
    DateTimeOffset CreatedAt);

/// <summary>One page of the user list, plus the counts the summary cards need — computed from the
/// SAME underlying query as the list, not a second independent count that could disagree with it.</summary>
public sealed record UserListResultDto(
    IReadOnlyList<UserSummary> Users, int TotalMatching, int Page, int PageSize, UserSummaryCountsDto Summary);

public sealed record UserSummaryCountsDto(int Total, int Active, int Pending, int Administrators);

/// <summary>Every filter is optional; an absent one applies no constraint. Page is 1-based.</summary>
public sealed record UserListQuery(
    string? Search = null,
    Guid? RoleId = null,
    Guid? DepartmentId = null,
    Guid? TeamId = null,
    string? BoardName = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25);

public sealed record CreateStaffUserInput(string DisplayName, string Email, IReadOnlyList<Guid> RoleIds);

/// <summary>One row of a staff import, as it arrived from the spreadsheet.</summary>
/// <param name="Department">
/// Matched to an existing department BY NAME, case-insensitively. Deliberately not created on the
/// fly: a typo in a spreadsheet would otherwise become a permanent department, and the row that
/// caused it would look like it imported perfectly.
/// </param>
public sealed record ImportStaffUserRow(string DisplayName, string Email, string? Department);

/// <param name="DryRun">
/// Report what WOULD happen and write nothing. The point of the whole endpoint: forty rows is far
/// past the number a person can check by eye, and an import that half-succeeds is worse than one
/// that refuses - you cannot tell by looking which half landed.
/// </param>
public sealed record ImportStaffUsersInput(
    IReadOnlyList<ImportStaffUserRow> Rows, IReadOnlyList<Guid> RoleIds, bool DryRun);

public enum ImportRowOutcome
{
    /// <summary>Would be created, or was.</summary>
    Created = 0,
    /// <summary>Someone already holds this email. Skipped, never overwritten - an import must not
    /// silently rename a colleague or move them between departments.</summary>
    AlreadyExists = 1,
    /// <summary>Rejected before anything was written. Reason says why.</summary>
    Invalid = 2,
}

public sealed record ImportStaffUserResult(
    string Email, string DisplayName, ImportRowOutcome Outcome, string? Reason, Guid? UserId);

public sealed record ImportStaffUsersResult(
    bool DryRun, int Created, int AlreadyExisted, int Invalid,
    IReadOnlyList<ImportStaffUserResult> Rows);

public sealed record UpdateStaffUserInput(
    string DisplayName, string Email, string? PhoneNumber, string? Location, Guid? ManagerId);

public sealed record UserPhotoUpload(string FileName, string ContentType, byte[] Content);

/// <summary>What a user can actually do with one permission, for the User Details Permissions tab.
/// Source is what makes this explainable rather than a bare answer — see IEffectivePermissionService.</summary>
public sealed record EffectivePermissionDto(
    string PermissionKey, string Module, string DisplayName,
    PermissionScope Scope, string Source, bool IsBoardAware, string BoardAccessMode);

/// <summary>Actions a bulk operation can perform across a set of users in one call.</summary>
public enum BulkUserAction { AssignRole, RemoveRole, AssignDepartment, AssignTeam, Activate, Deactivate, Delete }

public sealed record BulkUserActionInput(
    BulkUserAction Action, IReadOnlyList<Guid> UserIds, Guid? RoleId = null, Guid? DepartmentId = null, Guid? TeamId = null);

/// <summary>Per-row outcome, not an all-or-nothing result — one row failing (most commonly the
/// caller's own id on a self-guarded action) must not silently fail every other row in the batch.</summary>
public sealed record BulkUserActionResultDto(IReadOnlyList<BulkUserRowResultDto> Rows);

public sealed record BulkUserRowResultDto(Guid UserId, bool Success, string? Reason);

public sealed record ConnectionTestResultDto(bool Success, string? Message, double LatencyMs);

public sealed record UpdateConnectionInput(
    string Name,
    string ApiEndpoint,
    string? TenantIdentifier,
    string? TimeZone,
    bool IsEnabled,
    // When non-empty, replaces the stored credentials (rotation). Leave empty to keep existing.
    IReadOnlyDictionary<string, string>? Credentials,
    string? LogoUrl = null);

public sealed record ConnectionLogoUpload(string FileName, string ContentType, byte[] Content);

public sealed record StoredLogo(byte[] Content, string ContentType);

/// <summary>
/// A discovered option as the admin UI sees it. <see cref="Value"/> is sent TO the provider (import
/// filters, queue reassignment); <see cref="SyncValue"/> is what tickets arrive carrying and is what
/// a mapping rule must store. See <see cref="Desk.PsaCore.Models.ExternalFieldOption"/>.
/// </summary>
public sealed record FieldOptionDto(string Value, string Label, string SyncValue);

/// <summary>Live field discovery for a connection: service boards/queues, statuses, priorities, categories.</summary>
/// <summary>Whether time logging will work, and what to change when it will not.</summary>
public sealed record TimeEntryReadinessDto(
    bool Ready, string Summary, IReadOnlyList<string> Remedies, IReadOnlyList<string> AvailableRoles);

public sealed record ConnectionFieldsDto(
    IReadOnlyList<FieldOptionDto> QueuesOrBoards,
    IReadOnlyList<FieldOptionDto> Statuses,
    IReadOnlyList<FieldOptionDto> Priorities,
    IReadOnlyList<FieldOptionDto> Categories,
    IReadOnlyList<FieldOptionDto> WorkTypes,
    IReadOnlyList<FieldOptionDto> WorkRoles,
    IReadOnlyList<FieldOptionDto> Technicians,
    IReadOnlyList<TechnicianCoverageDto> TechnicianCoverage);

/// <summary>A technician's role on one queue/board. Repeated per queue they cover.</summary>
public sealed record TechnicianCoverageDto(string TechnicianId, string? RoleId, string? RoleName, string? QueueOrBoardId);

/// <summary>Per-connection sync behaviour + import filters (what flows, and which tickets are ours).</summary>
public sealed record ConnectionSettingsDto(
    bool TwoWaySync, bool AutoImportNewTickets, bool ImportNotes, bool ImportSystemNotes, bool SyncAttachments,
    bool ImportOpenTickets, bool ImportClosedTickets,
    string? FilterCompanyIds, string? FilterQueueIds, string? FilterResourceIds, int? FilterActiveWithinDays,
    string? DefaultQueueOrBoardId, string? DefaultTicketType, string? DefaultIssueType, string? DefaultSubIssueType,
    string? DefaultTimeEntryResourceId, string? DefaultTimeEntryRoleId);

/// <summary>
/// Who a staff user is inside one PSA connection, plus that connection's technicians to pick from.
/// Per connection because the identifier differs per provider in both value and KIND — Autotask
/// takes a numeric resourceID, ConnectWise a member identifier string.
/// </summary>
public sealed record UserPsaIdentityDto(
    Guid PsaConnectionId,
    string ConnectionName,
    string? ExternalTechnicianId,
    string? ExternalTechnicianName);
