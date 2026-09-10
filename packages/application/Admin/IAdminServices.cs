using Desk.Domain.Enums;

namespace Desk.Application.Admin;

/// <summary>Appends an immutable audit entry for the current actor + tenant. Detail is redacted of secrets.</summary>
public interface IAuditWriter
{
    Task WriteAsync(string action, string entityType, string? entityId, object? detail = null, CancellationToken ct = default);
}

/// <summary>
/// PSA connection administration. Creating a connection writes its credentials to the secret store
/// and persists only the returned reference — the raw secret is never stored on the row, returned to
/// a caller, or logged. Every mutation is audited.
/// </summary>
public interface IConnectionAdminService
{
    Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct = default);
    Task<ConnectionSummary> CreateAsync(CreateConnectionInput input, CancellationToken ct = default);
    Task SetEnabledAsync(Guid connectionId, bool enabled, CancellationToken ct = default);

    /// <summary>Stores an uploaded logo and points the connection at it. Audited.</summary>
    Task<ConnectionSummary> UploadLogoAsync(Guid connectionId, ConnectionLogoUpload upload, CancellationToken ct = default);

    /// <summary>Removes the logo, deleting the stored object when there was one.</summary>
    Task RemoveLogoAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Bytes and content type of a stored logo, or null when none is stored.</summary>
    Task<StoredLogo?> GetLogoAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Live-tests a saved connection against its PSA, updates its health status, and audits it.</summary>
    Task<ConnectionTestResultDto> TestAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Read-only pre-flight: can this connection log time with its current settings?</summary>
    Task<TimeEntryReadinessDto> CheckTimeEntryAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Updates a connection's settings and, if new credentials are supplied, rotates them in the store. Audited.</summary>
    Task<ConnectionSummary> UpdateAsync(Guid connectionId, UpdateConnectionInput input, CancellationToken ct = default);

    /// <summary>Returns the connection's field options — cached from configure time, discovered on first miss.</summary>
    Task<ConnectionFieldsDto> GetFieldsAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Forces a fresh discovery from the PSA and updates the cache.</summary>
    Task<ConnectionFieldsDto> RefreshFieldsAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Reads the connection's sync behaviour + import filters.</summary>
    Task<ConnectionSettingsDto> GetSettingsAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Updates the connection's sync behaviour + import filters. Audited.</summary>
    Task<ConnectionSettingsDto> SaveSettingsAsync(Guid connectionId, ConnectionSettingsDto input, CancellationToken ct = default);
}

/// <summary>
/// Field-mapping administration. Every upsert captures an immutable version snapshot of the mapping
/// set and writes an audit entry; rollback restores a prior snapshot (also audited).
/// </summary>
public interface IMappingAdminService
{
    Task<IReadOnlyList<MappingRuleDto>> ListAsync(ProviderType provider, CancellationToken ct = default);
    Task<MappingRuleDto> UpsertAsync(UpsertMappingInput input, string? changeNote, CancellationToken ct = default);
    Task<IReadOnlyList<MappingVersionDto>> VersionsAsync(ProviderType provider, Guid? connectionId, CancellationToken ct = default);

    /// <summary>Removes a mapping rule entirely (snapshotted + audited, like any other change).</summary>
    Task DeleteAsync(Guid ruleId, CancellationToken ct = default);
    Task RollbackAsync(Guid versionId, CancellationToken ct = default);
}

public interface IJobMonitorService
{
    Task<IReadOnlyList<JobSummary>> ListAsync(BackgroundJobStatus? status, CancellationToken ct = default);
    /// <summary>Requeues a dead-lettered job for another attempt. Audited.</summary>
    Task ReprocessAsync(Guid jobId, CancellationToken ct = default);
}

public interface IIntegrationHealthService
{
    Task<IReadOnlyList<ConnectionHealthDto>> SnapshotAsync(CancellationToken ct = default);
}

public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEntryDto>> ListAsync(
        int take = 100, string? action = null, string? entityId = null, CancellationToken ct = default);
}

/// <summary>
/// The Roles &amp; Permissions module (§6): read the permission catalogue, see every staff role's
/// grants, and create/edit/delete CUSTOM roles for this tenant. Built-in roles are read-only here —
/// they are shared rows across every tenant, so editing one would silently change other
/// organizations' access (and could lock this tenant's own administrators out).
/// </summary>
public interface IRoleAdminService
{
    IReadOnlyList<PermissionDefinitionDto> Catalog();
    Task<IReadOnlyList<RoleDetailDto>> ListAsync(CancellationToken ct = default);
    Task<RoleDetailDto> CreateAsync(SaveRoleInput input, CancellationToken ct = default);

    /// <summary>Custom roles only. Refused for a role the CALLER holds — editing a role you hold is
    /// editing your own permissions, the same self-escalation the per-user guards already block.</summary>
    Task<RoleDetailDto> UpdateAsync(Guid roleId, SaveRoleInput input, CancellationToken ct = default);

    /// <summary>Custom roles only, and refused while any user still holds it — losing permissions
    /// as a side effect of someone else's cleanup is an outage, not a deletion.</summary>
    Task DeleteAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>Every staff user's EFFECTIVE access to one permission — the org-wide answer to
    /// "who can do this?", resolved through the same engine enforcement consults (roles unioned,
    /// overrides replacing), never re-derived from role rows alone.</summary>
    Task<IReadOnlyList<UserEffectivePermissionDto>> HoldersAsync(string permissionKey, CancellationToken ct = default);
}

public interface IUserAdminService
{
    Task<UserListResultDto> ListAsync(UserListQuery query, CancellationToken ct = default);
    Task<UserSummary?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Staff roles only — the ones this page may hand out. Client and platform roles are excluded.</summary>
    Task<IReadOnlyList<RoleOptionDto>> StaffRolesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DepartmentWithTeamsDto>> DepartmentsAsync(CancellationToken ct = default);

    /// <summary>The full admin view of departments and teams — includes inactive rows and usage
    /// counts, unlike DepartmentsAsync's active-only picker list.</summary>
    Task<IReadOnlyList<DepartmentManageDto>> DepartmentsManageAsync(CancellationToken ct = default);
    Task<DepartmentManageDto> CreateDepartmentAsync(CreateDepartmentInput input, CancellationToken ct = default);
    Task<DepartmentManageDto> UpdateDepartmentAsync(Guid departmentId, UpdateDepartmentInput input, CancellationToken ct = default);
    Task SetDepartmentActiveAsync(Guid departmentId, bool active, CancellationToken ct = default);

    /// <summary>Hard delete — cascades to its teams and every user's membership in it. Callers should
    /// prefer SetDepartmentActiveAsync(false) unless the department was created by mistake.</summary>
    Task DeleteDepartmentAsync(Guid departmentId, CancellationToken ct = default);

    Task<TeamManageDto> CreateTeamAsync(CreateTeamInput input, CancellationToken ct = default);
    Task<TeamManageDto> UpdateTeamAsync(Guid teamId, UpdateTeamInput input, CancellationToken ct = default);
    Task SetTeamActiveAsync(Guid teamId, bool active, CancellationToken ct = default);

    /// <summary>Hard delete — cascades to every user's membership in it.</summary>
    Task DeleteTeamAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Distinct (connection, board name) pairs derived from synced tickets — boards are not
    /// a stored entity, so this list is only ever as current as the last sync.</summary>
    Task<IReadOnlyList<BoardOptionDto>> BoardsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PermissionTemplateOptionDto>> PermissionTemplatesAsync(CancellationToken ct = default);

    /// <summary>Creates a technician/manager/admin account. Sign-in binds on their first IdP login by email.</summary>
    Task<UserSummary> CreateAsync(CreateStaffUserInput input, CancellationToken ct = default);

    /// <summary>
    /// Creates many staff users from a spreadsheet, row by row through the same path a single
    /// creation takes - one set of validation rules, one audit event shape, no second way to make
    /// a user.
    ///
    /// A bad row is reported and skipped rather than aborting the run: in a list of forty, one
    /// malformed address should not cost the other thirty-nine, and the caller gets a per-row
    /// account of what happened either way.
    /// </summary>
    Task<ImportStaffUsersResult> ImportAsync(ImportStaffUsersInput input, CancellationToken ct = default);

    /// <summary>Edits profile fields. Never touches roles, department/team, or board access.</summary>
    Task<UserSummary> UpdateAsync(Guid userId, UpdateStaffUserInput input, CancellationToken ct = default);

    /// <summary>Activate or deactivate. Deactivating also unbinds nothing — reactivation restores access.</summary>
    Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);

    /// <summary>Permanently removes the account and every row that references it (roles, department/
    /// team/board membership, permission overrides). Blocked on self, exactly like role changes.</summary>
    Task DeleteAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Who this user is in each PSA, with each connection's technician list to choose from.</summary>
    Task<IReadOnlyList<UserPsaIdentityDto>> PsaIdentitiesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Sets who this user is in one PSA so their logged time is attributed to them.
    /// A null id clears the mapping and returns their time to the connection's default resource.</summary>
    Task SetPsaIdentityAsync(Guid userId, Guid psaConnectionId, string? externalTechnicianId, string? externalTechnicianName, CancellationToken ct = default);

    Task AssignRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);
    Task RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);

    /// <summary>Adds (or re-flags) a department membership. The partial unique index on the table
    /// enforces exactly one primary — setting a new primary un-sets the previous one.</summary>
    Task SetDepartmentAsync(Guid userId, Guid departmentId, bool isPrimary, CancellationToken ct = default);
    Task RemoveDepartmentAsync(Guid userId, Guid departmentId, CancellationToken ct = default);
    Task AssignTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);
    Task RemoveTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    Task SetBoardAccessModeAsync(Guid userId, Domain.Authorization.BoardAccessMode mode, CancellationToken ct = default);
    Task SetBoardGrantAsync(Guid userId, Guid psaConnectionId, string boardName, Domain.Authorization.BoardAction actions, CancellationToken ct = default);
    Task RemoveBoardGrantAsync(Guid userId, Guid psaConnectionId, string boardName, CancellationToken ct = default);

    /// <summary>Materializes a template's entries as UserPermissionOverride rows tagged with the
    /// template's id — not a second grant mechanism, per the PermissionTemplate design.</summary>
    Task ApplyPermissionTemplateAsync(Guid userId, Guid templateId, CancellationToken ct = default);

    /// <summary>Every catalogued permission, resolved through IEffectivePermissionService, for the
    /// User Details Permissions tab.</summary>
    Task<IReadOnlyList<EffectivePermissionDto>> GetEffectivePermissionsAsync(Guid userId, CancellationToken ct = default);

    Task<UserSummary> UploadPhotoAsync(Guid userId, UserPhotoUpload upload, CancellationToken ct = default);
    Task RemovePhotoAsync(Guid userId, CancellationToken ct = default);
    Task<StoredLogo?> GetPhotoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Applies one action across many users. Per-row outcome — a row a self-guard blocks
    /// does not fail the rest of the batch.</summary>
    Task<BulkUserActionResultDto> BulkAsync(BulkUserActionInput input, CancellationToken ct = default);
}

/// <summary>
/// Tickets the portal holds that never reached the PSA: how many, which, and pushing them again.
/// </summary>
public interface ITicketResyncService
{
    /// <summary>Outstanding tickets, newest first, optionally for one connection.</summary>
    Task<UnsyncedTicketsDto> ListAsync(Guid? connectionId = null, CancellationToken ct = default);

    /// <summary>Re-attempts one ticket, rebuilding the request from current mappings and defaults.</summary>
    Task<ResyncResultDto> ResyncAsync(Guid ticketId, CancellationToken ct = default);
}
