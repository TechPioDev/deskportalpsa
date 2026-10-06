using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Sync;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Api.Auth;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>PSA connection administration. Secrets are written to the encrypted store and never returned.</summary>
[ApiController]
[Route("api/admin/connections")]
public sealed class AdminConnectionsController(
    IConnectionAdminService svc,
    IConnectionSyncRunner syncRunner,
    DeskDbContext db,
    IConfiguration config,
    ISyncHealthService syncHealth,
    Desk.Application.Abstractions.ICurrentUser user,
    Desk.Application.Admin.IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.ConnectionsView)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));

    /// <summary>Uploads a logo for the connections page. Small by design — this is a brand mark.</summary>
    [HttpPost("{id:guid}/logo")]
    [RequirePermission(Permissions.ConnectionsManage)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> UploadLogo(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Choose an image to upload." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return Ok(await svc.UploadLogoAsync(id, new ConnectionLogoUpload(file.FileName, file.ContentType, ms.ToArray()), ct));
    }

    [HttpDelete("{id:guid}/logo")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> RemoveLogo(Guid id, CancellationToken ct)
    {
        await svc.RemoveLogoAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Serves the stored logo. nosniff and a locked-down CSP because this route returns bytes an
    /// administrator supplied — the browser must never be talked into treating them as a document.
    /// </summary>
    [HttpGet("{id:guid}/logo")]
    [RequirePermission(Permissions.ConnectionsView)]
    public async Task<IActionResult> GetLogo(Guid id, CancellationToken ct)
    {
        var logo = await svc.GetLogoAsync(id, ct);
        if (logo is null) return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "private, max-age=300";
        return File(logo.Content, logo.ContentType);
    }

    [HttpPost]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Create([FromBody] CreateConnectionInput input, CancellationToken ct)
        => Ok(await svc.CreateAsync(input, ct));

    [HttpPost("{id:guid}/enabled")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> SetEnabled(Guid id, [FromBody] bool enabled, CancellationToken ct)
    {
        await svc.SetEnabledAsync(id, enabled, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
        => Ok(await svc.TestAsync(id, ct));

    /// <summary>Read-only: reports whether time logging will work with the current settings.</summary>
    [HttpPost("{id:guid}/check-time-entry")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> CheckTimeEntry(Guid id, CancellationToken ct)
        => Ok(await svc.CheckTimeEntryAsync(id, ct));

    /// <summary>
    /// Pull tickets from the provider into the portal. Incremental by default; pass full=true to
    /// re-pull everything (e.g. after changing field mappings, so existing tickets are re-translated).
    /// </summary>
    [HttpPost("{id:guid}/sync")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Sync(Guid id, [FromQuery] bool full, CancellationToken ct)
    {
        // Under the caller's tenant: another organization's connection is not found, before
        // anything is recorded about it.
        var paused = await db.PsaConnections.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { Paused = c.SyncPausedAt != null })
            .FirstOrDefaultAsync(ct);
        if (paused is null) return NotFound();
        // Said in words. A run on a paused connection reads nothing, and "0 fetched" would look
        // like a sync that found nothing new.
        if (paused.Paused)
            throw new Desk.Application.Common.ValidationFailedException("Sync is paused for this connection. Resume it to read from the PSA again.");
        // Recorded as asked for, before it runs: a run that then fails was still requested.
        await audit.WriteAsync("connection.sync.requested", "PsaConnection", id.ToString(), new { full }, ct);

        // One run per connection. While the scheduled sync (or an earlier click) has it, this
        // answers 409 and starts nothing: it used to start a second run over the same tickets.
        var result = await syncRunner.RunAsync(id,
            new SyncRunRequest(full, Manual: true, RequestedBy: user.DisplayName ?? user.Email ?? user.Subject), ct);
        await EnsureLocalClientIdentityAsync(id, ct);
        return Ok(result);
    }

    /// <summary>Every PSA the portal names: those that can be connected, with what each needs, and those that are planned.</summary>
    [HttpGet("providers")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public IActionResult Providers() => Ok(svc.Providers());

    /// <summary>What this connection's PSA can and cannot do, so a screen offers only what will work.</summary>
    [HttpGet("{id:guid}/capabilities")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Capabilities(Guid id, CancellationToken ct) => Ok(await svc.CapabilitiesAsync(id, ct));

    /// <summary>The test, line by line. Reads only: nothing is written to the PSA.</summary>
    [HttpPost("{id:guid}/check")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Check(Guid id, CancellationToken ct) => Ok(await svc.CheckAsync(id, ct));

    /// <summary>Each status and priority the PSA lists, and what this connection's rules make of it.</summary>
    [HttpGet("{id:guid}/mapping-coverage")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> MappingCoverage(Guid id, CancellationToken ct) => Ok(await svc.MappingCoverageAsync(id, ct));

    /// <summary>How well this connection's mapping covers what its PSA sends.</summary>
    [HttpGet("{id:guid}/mapping-health")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> MappingHealth(Guid id, CancellationToken ct) => Ok(await svc.MappingHealthAsync(id, ct));

    /// <summary>Sample tickets and what the rules make of each.</summary>
    [HttpGet("{id:guid}/mapping-preview")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> MappingPreview(Guid id, [FromQuery] int take = 10, CancellationToken ct = default)
        => Ok(await svc.MappingPreviewAsync(id, take, ct));

    /// <summary>Re-maps imported tickets still showing the PSA's own word for a value a rule now maps.</summary>
    [HttpPost("{id:guid}/mapping-apply")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> MappingApply(Guid id, CancellationToken ct) => Ok(await svc.ApplyMappingAsync(id, ct));

    /// <summary>How much an import would bring in, asked of the PSA before anything is imported.</summary>
    [HttpGet("{id:guid}/preview")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct) => Ok(await svc.PreviewAsync(id, ct));

    /// <summary>What stands between this connection and being switched on.</summary>
    [HttpGet("{id:guid}/preflight")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Preflight(Guid id, CancellationToken ct) => Ok(await svc.PreflightAsync(id, ct));

    /// <summary>Switches a new connection on for the first time. Refused until a test has passed.</summary>
    [HttpPost("{id:guid}/activate")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => Ok(await svc.ActivateAsync(id, ct));

    [HttpPost("{id:guid}/pause-sync")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> PauseSync(Guid id, CancellationToken ct) => Ok(await svc.PauseSyncAsync(id, ct));

    [HttpPost("{id:guid}/resume-sync")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> ResumeSync(Guid id, CancellationToken ct) => Ok(await svc.ResumeSyncAsync(id, ct));

    /// <summary>The connections that have been put away, so that one can be restored.</summary>
    [HttpGet("archived")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Archived(CancellationToken ct) => Ok(await svc.ArchivedAsync(ct));

    /// <summary>Puts a connection away. Nothing it imported is removed, and it can be restored.</summary>
    [HttpPost("{id:guid}/archive")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        await svc.ArchiveAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => Ok(await svc.RestoreAsync(id, ct));

    /// <summary>Where the connection's sync stands: its cursor, its recent runs, what it still owes.</summary>
    [HttpGet("{id:guid}/sync-state")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> SyncState(Guid id, [FromQuery] int runs = 20, CancellationToken ct = default)
        => Ok(await syncHealth.StateAsync(id, runs, ct));

    /// <summary>The records the sync could not read or apply and has not yet got through.</summary>
    [HttpGet("{id:guid}/sync-failures")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> SyncFailures(Guid id, CancellationToken ct)
        => Ok(await syncHealth.FailuresAsync(id, ct));

    [HttpPost("{id:guid}/sync-failures/{failureId:guid}/retry")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> RetrySyncFailure(Guid id, Guid failureId, CancellationToken ct)
    {
        await syncHealth.RetryAsync(id, failureId, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/sync-failures/{failureId:guid}/dismiss")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> DismissSyncFailure(Guid id, Guid failureId, CancellationToken ct)
    {
        await syncHealth.DismissAsync(id, failureId, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateConnectionInput input, CancellationToken ct)
        => Ok(await svc.UpdateAsync(id, input, ct));

    [HttpGet("{id:guid}/fields")]
    [RequirePermission(Permissions.ConnectionsView)]
    public async Task<IActionResult> Fields(Guid id, CancellationToken ct)
        => Ok(await svc.GetFieldsAsync(id, ct));

    /// <summary>Sync behaviour + import filters for this connection.</summary>
    [HttpGet("{id:guid}/settings")]
    [RequirePermission(Permissions.ConnectionsView)]
    public async Task<IActionResult> GetSettings(Guid id, CancellationToken ct)
        => Ok(await svc.GetSettingsAsync(id, ct));

    [HttpPut("{id:guid}/settings")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> SaveSettings(Guid id, [FromBody] ConnectionSettingsDto input, CancellationToken ct)
        => Ok(await svc.SaveSettingsAsync(id, input, ct));

    /// <summary>Force a fresh discovery of field options from the PSA and update the cache.</summary>
    [HttpPost("{id:guid}/fields/refresh")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> RefreshFields(Guid id, CancellationToken ct)
        => Ok(await svc.RefreshFieldsAsync(id, ct));

    // Local demo only: the dev auto-login is an MSP admin, not a client. The client-portal pages
    // (Tickets, Notifications, Profile) resolve by client identity, so once a sync has produced real
    // tickets we link the dev subject to the busiest synced company (as its administrator) — using
    // real synced data, never fabricated rows — so the whole portal shows live data under one login.
    private async Task EnsureLocalClientIdentityAsync(Guid connectionId, CancellationToken ct)
    {
        if (!config.GetValue("LocalMode:Enabled", false)) return;
        if (await db.ClientUsers.IgnoreQueryFilters().AnyAsync(u => u.IdpSubject == DatabaseSeeder.DevAdminSubject, ct)) return;

        var companyId = await db.Tickets
            .Where(t => t.PsaConnectionId == connectionId)
            .GroupBy(t => t.ClientCompanyId)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefaultAsync(ct);
        // No tickets synced yet, or only tickets that belong to no client: nothing to attach to.
        if (companyId is not { } clientCompanyId || clientCompanyId == Guid.Empty) return;

        var company = await db.ClientCompanies.FirstAsync(c => c.Id == clientCompanyId, ct);
        db.ClientUsers.Add(new ClientUser
        {
            MspOrganizationId = company.MspOrganizationId,
            ClientCompanyId = clientCompanyId,
            Email = "dev-admin@local",
            DisplayName = "Demo Admin",
            IdpSubject = DatabaseSeeder.DevAdminSubject,
            IsCompanyAdministrator = true,
            IsActive = true,
        });
        await db.SaveChangesAsync(ct);
    }
}


/// <summary>Field-mapping administration with versioning + rollback (all audited).</summary>
[ApiController]
[Route("api/admin/mappings")]
public sealed class AdminMappingsController(IMappingAdminService svc) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.MappingsView)]
    public async Task<IActionResult> List([FromQuery] ProviderType provider, CancellationToken ct)
        => Ok(await svc.ListAsync(provider, ct));

    [HttpPost]
    [RequirePermission(Permissions.MappingsManage)]
    public async Task<IActionResult> Upsert([FromBody] UpsertMappingInput input, [FromQuery] string? note, CancellationToken ct)
        => Ok(await svc.UpsertAsync(input, note, ct));

    /// <summary>The portal's own statuses and priorities: what a PSA's values are mapped to.</summary>
    [HttpGet("vocabulary")]
    [RequirePermission(Permissions.MappingsView)]
    public IActionResult Vocabulary()
        => Ok(new PortalVocabularyDto(Desk.Domain.Tickets.PortalVocabulary.Statuses, Desk.Domain.Tickets.PortalVocabulary.Priorities));

    /// <summary>What values a connection's PSA sends should become in the portal. Any number, saved together.</summary>
    [HttpPut("inbound/{connectionId:guid}")]
    [RequirePermission(Permissions.MappingsManage)]
    public async Task<IActionResult> SetInbound(
        Guid connectionId, [FromBody] IReadOnlyList<SetInboundMappingInput> changes, [FromQuery] string? note, CancellationToken ct)
        => Ok(await svc.SetInboundAsync(connectionId, changes, note, ct));

    [HttpDelete("{ruleId:guid}")]
    [RequirePermission(Permissions.MappingsManage)]
    public async Task<IActionResult> Delete(Guid ruleId, CancellationToken ct)
    { await svc.DeleteAsync(ruleId, ct); return NoContent(); }

    [HttpGet("versions")]
    [RequirePermission(Permissions.MappingsView)]
    public async Task<IActionResult> Versions([FromQuery] ProviderType provider, [FromQuery] Guid? connectionId, CancellationToken ct)
        => Ok(await svc.VersionsAsync(provider, connectionId, ct));

    [HttpPost("versions/{versionId:guid}/rollback")]
    [RequirePermission(Permissions.MappingsManage)]
    public async Task<IActionResult> Rollback(Guid versionId, CancellationToken ct)
    {
        await svc.RollbackAsync(versionId, ct);
        return NoContent();
    }

    [HttpGet("versions/status")]
    [RequirePermission(Permissions.MappingsView)]
    public async Task<IActionResult> SnapshotStatus([FromQuery] ProviderType provider, [FromQuery] Guid? connectionId, CancellationToken ct)
        => Ok(await svc.SnapshotStatusAsync(provider, connectionId, ct));

    [HttpPost("versions")]
    [RequirePermission(Permissions.MappingsManage)]
    public async Task<IActionResult> SaveSnapshot([FromQuery] ProviderType provider, [FromQuery] Guid? connectionId, [FromQuery] string? note, CancellationToken ct)
        => Ok(await svc.SaveSnapshotAsync(provider, connectionId, note, ct));
}

/// <summary>Background job monitor with dead-letter reprocessing.</summary>
[ApiController]
[Route("api/admin/jobs")]
public sealed class JobMonitorController(IJobMonitorService svc) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.JobsManage)]
    public async Task<IActionResult> List([FromQuery] BackgroundJobStatus? status, CancellationToken ct)
        => Ok(await svc.ListAsync(status, ct));

    [HttpPost("{id:guid}/reprocess")]
    [RequirePermission(Permissions.JobsManage)]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        await svc.ReprocessAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/admin")]
public sealed class AdminReadController(
    IIntegrationHealthService health,
    IAuditQueryService auditQuery,
    ITicketResyncService resync,
    IUserAdminService users,
    IConnectionAdminService connections,
    ITechnicianProvisioningService provisioning) : ControllerBase
{
    /// <summary>Tickets the portal holds that never reached the PSA — the count and which they are.</summary>
    [HttpGet("tickets/unsynced")]
    [RequirePermission(Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Unsynced(
        [FromQuery] Guid? connectionId, [FromServices] ICurrentUser user,
        [FromServices] Desk.Application.Authorization.IEffectivePermissionService permissions, CancellationToken ct)
    {
        await RequireEveryTicketAsync(user, permissions, ct);
        return Ok(await resync.ListAsync(connectionId, ct));
    }

    /// <summary>
    /// These lists name tickets from across the organization. Integration-health access alone is not
    /// enough: the Auditor role holds it with no ticket access at all, and was reading ticket titles,
    /// client comments and references through here.
    /// </summary>
    private static async Task RequireEveryTicketAsync(
        ICurrentUser user, Desk.Application.Authorization.IEffectivePermissionService permissions, CancellationToken ct)
    {
        if (!await user.SeesEveryTicketAsync(permissions, ct))
            throw new ForbiddenException("This list covers every ticket in the organization, so it needs access to all tickets.");
    }

    /// <summary>
    /// Pushes one outstanding ticket again. Deliberately one at a time: each retry hits the provider
    /// and can fail for its own reason, and a bulk button would bury which ones did.
    /// </summary>
    [HttpPost("tickets/{id:guid}/resync")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Resync(Guid id, CancellationToken ct)
        => Ok(await resync.ResyncAsync(id, ct));

    [HttpGet("health")]
    [RequirePermission(Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Health(CancellationToken ct) => Ok(await health.SnapshotAsync(ct));

    /// <summary>Everything an administrator should look at, most urgent first, plus the daily digest settings.</summary>
    [HttpGet("attention")]
    [RequirePermission(Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Attention(
        [FromServices] IAttentionService attention, [FromServices] ICurrentUser user,
        [FromServices] Desk.Application.Authorization.IEffectivePermissionService permissions, CancellationToken ct)
    {
        await RequireEveryTicketAsync(user, permissions, ct);
        return Ok(await attention.ListAsync(ct));
    }

    public sealed record DigestRecipientsInput(string? Recipients);

    /// <summary>Who receives the daily "needs attention" email. Blank switches it off.</summary>
    [HttpPut("attention/digest")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> SetDigest([FromServices] IAttentionService attention, [FromBody] DigestRecipientsInput input, CancellationToken ct)
    {
        var (digest, invalid) = await attention.SetDigestRecipientsAsync(input.Recipients, ct);
        return Ok(new { digest.Recipients, digest.LastSentOn, invalid });
    }

    /// <summary>Sends the current list to the digest recipients now, as proof it arrives.</summary>
    [HttpPost("attention/digest/send")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> SendDigest([FromServices] IAttentionService attention, CancellationToken ct)
        => Ok(await attention.SendDigestNowAsync(ct));

    [HttpGet("audit")]
    [RequirePermission(Permissions.AuditView)]
    public async Task<IActionResult> Audit(
        [FromQuery] string? action, [FromQuery] string? entityId, [FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await auditQuery.ListAsync(take, action, entityId, ct));

    /// <summary>
    /// Real attachment-storage usage. The sidebar used to show a hardcoded "6.8 GB of 10 GB" —
    /// decoration presented as fact. Only CLEAN files count: quarantined uploads keep no bytes.
    /// </summary>
    [HttpGet("storage")]
    [RequirePermission(Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Storage([FromServices] DeskDbContext db, CancellationToken ct)
    {
        var clean = db.TicketAttachments.Where(a => a.ScanStatus == Desk.Domain.Enums.AttachmentScanStatus.Clean);
        return Ok(new
        {
            usedBytes = await clean.SumAsync(a => (long?)a.SizeBytes, ct) ?? 0L,
            fileCount = await clean.CountAsync(ct),
            ticketCount = await clean.Select(a => a.TicketId).Distinct().CountAsync(ct),
        });
    }

    [HttpGet("users")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> Users([FromQuery] UserListQuery query, CancellationToken ct)
        => Ok(await users.ListAsync(query, ct));

    [HttpGet("users/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> UserDetail(Guid id, CancellationToken ct)
    {
        var user = await users.GetAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpGet("roles")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> StaffRoles(CancellationToken ct) => Ok(await users.StaffRolesAsync(ct));

    // ---- Roles & Permissions module (§6) — gated by roles.manage, not users.manage ----

    [HttpGet("roles/catalog")]
    [RequirePermission(Permissions.RolesManage)]
    public IActionResult RoleCatalog([FromServices] IRoleAdminService roles) => Ok(roles.Catalog());

    [HttpGet("roles/detailed")]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<IActionResult> RolesDetailed([FromServices] IRoleAdminService roles, CancellationToken ct)
        => Ok(await roles.ListAsync(ct));

    [HttpPost("roles")]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<IActionResult> CreateRole([FromServices] IRoleAdminService roles, [FromBody] SaveRoleInput input, CancellationToken ct)
        => Ok(await roles.CreateAsync(input, ct));

    [HttpPut("roles/{id:guid}")]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<IActionResult> UpdateRole([FromServices] IRoleAdminService roles, Guid id, [FromBody] SaveRoleInput input, CancellationToken ct)
        => Ok(await roles.UpdateAsync(id, input, ct));

    [HttpDelete("roles/{id:guid}")]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<IActionResult> DeleteRole([FromServices] IRoleAdminService roles, Guid id, CancellationToken ct)
    { await roles.DeleteAsync(id, ct); return NoContent(); }

    /// <summary>§13 — every staff user's effective access to one permission. Key as a query param
    /// because permission keys contain dots.</summary>
    [HttpGet("effective-permissions")]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<IActionResult> EffectivePermissionHolders(
        [FromServices] IRoleAdminService roles, [FromQuery] string key, CancellationToken ct)
        => Ok(await roles.HoldersAsync(key, ct));

    [HttpGet("departments")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> Departments(CancellationToken ct) => Ok(await users.DepartmentsAsync(ct));

    [HttpGet("org-structure")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> OrgStructure(CancellationToken ct) => Ok(await users.DepartmentsManageAsync(ct));

    [HttpPost("departments")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> CreateDepartment([FromBody] CreateDepartmentInput input, CancellationToken ct)
        => Ok(await users.CreateDepartmentAsync(input, ct));

    [HttpPut("departments/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> UpdateDepartment(Guid id, [FromBody] UpdateDepartmentInput input, CancellationToken ct)
        => Ok(await users.UpdateDepartmentAsync(id, input, ct));

    [HttpPut("departments/{id:guid}/active")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetDepartmentActive(Guid id, [FromBody] bool active, CancellationToken ct)
    { await users.SetDepartmentActiveAsync(id, active, ct); return NoContent(); }

    [HttpDelete("departments/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> DeleteDepartment(Guid id, CancellationToken ct)
    { await users.DeleteDepartmentAsync(id, ct); return NoContent(); }

    [HttpPost("teams")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> CreateTeam([FromBody] CreateTeamInput input, CancellationToken ct)
        => Ok(await users.CreateTeamAsync(input, ct));

    [HttpPut("teams/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> UpdateTeam(Guid id, [FromBody] UpdateTeamInput input, CancellationToken ct)
        => Ok(await users.UpdateTeamAsync(id, input, ct));

    [HttpPut("teams/{id:guid}/active")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetTeamActive(Guid id, [FromBody] bool active, CancellationToken ct)
    { await users.SetTeamActiveAsync(id, active, ct); return NoContent(); }

    [HttpDelete("teams/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> DeleteTeam(Guid id, CancellationToken ct)
    { await users.DeleteTeamAsync(id, ct); return NoContent(); }

    [HttpGet("boards")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> Boards(CancellationToken ct) => Ok(await users.BoardsAsync(ct));

    [HttpGet("permission-templates")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> PermissionTemplates(CancellationToken ct) => Ok(await users.PermissionTemplatesAsync(ct));

    [HttpPost("users")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> CreateUser([FromBody] CreateStaffUserInput input, CancellationToken ct)
        => Ok(await users.CreateAsync(input, ct));

    /// <summary>
    /// Creates staff from a spreadsheet. Send DryRun first and show the caller what would happen:
    /// forty rows is well past what anyone checks by eye, and a half-finished import cannot be told
    /// from a complete one by looking at the result.
    /// </summary>
    [HttpPost("users/import")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> ImportUsers([FromBody] ImportStaffUsersInput input, CancellationToken ct)
        => Ok(await users.ImportAsync(input, ct));

    [HttpPut("users/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateStaffUserInput input, CancellationToken ct)
        => Ok(await users.UpdateAsync(id, input, ct));

    [HttpPut("users/{id:guid}/active")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetUserActive(Guid id, [FromBody] bool active, CancellationToken ct)
    { await users.SetActiveAsync(id, active, ct); return NoContent(); }

    [HttpDelete("users/{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    { await users.DeleteAsync(id, ct); return NoContent(); }
    /// <summary>
    /// The PSA's technicians, with what the portal already knows about each. Read-only: nothing is
    /// created until an administrator provisions someone explicitly.
    /// </summary>
    [HttpGet("psa-technicians/{psaConnectionId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> PsaTechnicians(Guid psaConnectionId, CancellationToken ct)
        => Ok(await provisioning.ListAsync(psaConnectionId, ct));

    /// <summary>Says a PSA login is to be left alone (an API account, someone who left), or takes that back.</summary>
    [HttpPut("psa-technicians/{psaConnectionId:guid}/{externalTechnicianId}/ignored")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> IgnoreTechnician(
        Guid psaConnectionId, string externalTechnicianId, [FromBody] IgnoreTechnicianRequest request, CancellationToken ct)
    {
        await provisioning.SetIgnoredAsync(psaConnectionId, externalTechnicianId, request.Ignored, request.Name, ct);
        return NoContent();
    }

    public sealed record IgnoreTechnicianRequest(bool Ignored, [System.ComponentModel.DataAnnotations.StringLength(200)] string? Name);

    /// <summary>Creates (or links) the portal user for one PSA technician and maps them.</summary>
    [HttpPost("psa-technicians/{psaConnectionId:guid}/{externalTechnicianId}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> ProvisionTechnician(Guid psaConnectionId, string externalTechnicianId, CancellationToken ct)
        => Ok(await provisioning.ProvisionAsync(psaConnectionId, externalTechnicianId, ct));


    /// <summary>
    /// Who this user is in each PSA — what their logged time is attributed to — with each
    /// connection's technicians to choose from. Discovery is joined HERE rather than inside the
    /// user service so that service stays a pure reader of its own tables. A connection whose
    /// technicians cannot be discovered still appears, with an empty list: the mapping it already
    /// holds is worth showing even when the picker cannot be filled.
    /// </summary>
    [HttpGet("users/{id:guid}/psa-identities")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> PsaIdentities(Guid id, CancellationToken ct)
    {
        var rows = await users.PsaIdentitiesAsync(id, ct);
        var result = new List<object>(rows.Count);
        foreach (var r in rows)
            result.Add(new
            {
                r.PsaConnectionId, r.ConnectionName, r.ExternalTechnicianId, r.ExternalTechnicianName,
                technicians = await TechniciansAsync(r.PsaConnectionId, ct),
            });
        return Ok(result);
    }

    [HttpPut("users/{id:guid}/psa-identities/{psaConnectionId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetPsaIdentity(
        Guid id, Guid psaConnectionId, [FromBody] SetPsaIdentityRequest input, CancellationToken ct)
    {
        // The label is resolved from the provider's OWN list, never taken from the request: the
        // browser could send a name that does not belong to the id, and the id is what is written.
        var name = string.IsNullOrWhiteSpace(input.ExternalTechnicianId) ? null
            : (await TechniciansAsync(psaConnectionId, ct))
                .FirstOrDefault(t => t.Value == input.ExternalTechnicianId!.Trim())?.Label;
        await users.SetPsaIdentityAsync(id, psaConnectionId, input.ExternalTechnicianId, name, ct);
        return NoContent();
    }

    private async Task<IReadOnlyList<FieldOptionDto>> TechniciansAsync(Guid connectionId, CancellationToken ct)
    {
        try { return (await connections.GetFieldsAsync(connectionId, ct)).Technicians; }
        catch (Exception) { return []; } // discovery down: no picker, but the page still works
    }

    [HttpPost("users/{id:guid}/roles/{roleId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> AssignRole(Guid id, Guid roleId, CancellationToken ct)
    { await users.AssignRoleAsync(id, roleId, ct); return NoContent(); }

    [HttpDelete("users/{id:guid}/roles/{roleId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId, CancellationToken ct)
    { await users.RemoveRoleAsync(id, roleId, ct); return NoContent(); }

    [HttpPost("users/{id:guid}/departments/{departmentId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetDepartment(Guid id, Guid departmentId, [FromQuery] bool isPrimary, CancellationToken ct)
    { await users.SetDepartmentAsync(id, departmentId, isPrimary, ct); return NoContent(); }

    [HttpDelete("users/{id:guid}/departments/{departmentId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> RemoveDepartment(Guid id, Guid departmentId, CancellationToken ct)
    { await users.RemoveDepartmentAsync(id, departmentId, ct); return NoContent(); }

    [HttpPost("users/{id:guid}/teams/{teamId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> AssignTeam(Guid id, Guid teamId, CancellationToken ct)
    { await users.AssignTeamAsync(id, teamId, ct); return NoContent(); }

    [HttpDelete("users/{id:guid}/teams/{teamId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> RemoveTeam(Guid id, Guid teamId, CancellationToken ct)
    { await users.RemoveTeamAsync(id, teamId, ct); return NoContent(); }

    /// <summary>Null or blank clears the mapping — the user's time returns to the connection default.</summary>
    public sealed record SetPsaIdentityRequest(
        [System.ComponentModel.DataAnnotations.StringLength(100)] string? ExternalTechnicianId);

    public sealed record SetBoardAccessModeRequest(Desk.Domain.Authorization.BoardAccessMode Mode);

    [HttpPut("users/{id:guid}/board-access")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetBoardAccessMode(Guid id, [FromBody] SetBoardAccessModeRequest req, CancellationToken ct)
    { await users.SetBoardAccessModeAsync(id, req.Mode, ct); return NoContent(); }

    public sealed record BoardGrantRequest(Guid PsaConnectionId, string BoardName, Desk.Domain.Authorization.BoardAction Actions);

    [HttpPut("users/{id:guid}/board-grants")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetBoardGrant(Guid id, [FromBody] BoardGrantRequest req, CancellationToken ct)
    { await users.SetBoardGrantAsync(id, req.PsaConnectionId, req.BoardName, req.Actions, ct); return NoContent(); }

    [HttpDelete("users/{id:guid}/board-grants")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> RemoveBoardGrant(Guid id, [FromQuery] Guid psaConnectionId, [FromQuery] string boardName, CancellationToken ct)
    { await users.RemoveBoardGrantAsync(id, psaConnectionId, boardName, ct); return NoContent(); }

    [HttpPost("users/{id:guid}/apply-template/{templateId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> ApplyPermissionTemplate(Guid id, Guid templateId, CancellationToken ct)
    { await users.ApplyPermissionTemplateAsync(id, templateId, ct); return NoContent(); }

    [HttpGet("users/{id:guid}/permissions")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> UserPermissions(Guid id, CancellationToken ct)
        => Ok(await users.GetEffectivePermissionsAsync(id, ct));

    [HttpPost("users/{id:guid}/photo")]
    [RequirePermission(Permissions.UsersManage)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> UploadUserPhoto(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Choose an image to upload." });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return Ok(await users.UploadPhotoAsync(id, new UserPhotoUpload(file.FileName, file.ContentType, ms.ToArray()), ct));
    }

    [HttpDelete("users/{id:guid}/photo")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> RemoveUserPhoto(Guid id, CancellationToken ct)
    { await users.RemovePhotoAsync(id, ct); return NoContent(); }

    [HttpGet("users/{id:guid}/photo")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> GetUserPhoto(Guid id, CancellationToken ct)
    {
        var photo = await users.GetPhotoAsync(id, ct);
        if (photo is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "private, max-age=300";
        return File(photo.Content, photo.ContentType);
    }

    [HttpPost("users/bulk")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> BulkUsers([FromBody] BulkUserActionInput input, CancellationToken ct)
        => Ok(await users.BulkAsync(input, ct));
}
