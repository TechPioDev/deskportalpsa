using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// Staff time management. Lists, logs, edits and deletes PSA time entries for a synced ticket, then
/// recomputes the portal ticket's worked/billable/non-billable aggregates from the PSA (the system
/// of record) so the productivity dashboards stay in sync. A technician action gated by
/// <see cref="Permissions.TicketsLogTime"/> — distinct from the client-portal ticket endpoints.
/// </summary>
// Class-level [Authorize] as a floor: every action here also carries [RequirePermission],
// but that is opt-in per action — an action added later without one would otherwise be
// reachable anonymously. This makes authentication the default and the omission harmless.
[Authorize]
[ApiController]
[Route("api/tickets")]
public sealed class TicketTimeController(
    DeskDbContext db, IConnectorResolver connectors, IConnectionAdminService admin,
    ITicketScopeQuery scopeQuery, ICurrentUser user) : ControllerBase
{
    [HttpGet("{id:guid}/time-options")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> TimeOptions(Guid id, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        // Read from the cached field set (discovered when the connection was configured).
        var fields = await admin.GetFieldsAsync(ticket.PsaConnectionId, ct);
        return Ok(new
        {
            workTypes = fields.WorkTypes.Select(o => new { o.Value, o.Label }),
            workRoles = fields.WorkRoles.Select(o => new { o.Value, o.Label }),
        });
    }

    /// <summary>
    /// The ticket's time, reconciled from two places. The PSA owns the hours, so its entries are the
    /// spine of the list; the portal's own rows supply what the PSA cannot know — which system the
    /// entry was logged in — and carry the entries that never made it, which would otherwise be
    /// invisible to everyone.
    /// </summary>
    [HttpGet("{id:guid}/time")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> List(Guid id, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        var local = await db.TicketTimeEntries.AsNoTracking()
            .Where(t => t.TicketId == id)
            .ToListAsync(ct);

        // Whose time it is. The PSA files every hour a portal-only technician logs under the account
        // the portal writes as, so the provider's name on those entries credits the account - on
        // Techpio's Autotask, "Sudanshu Aggarwal" - with the team's work. The portal's own row knows
        // who actually logged it; where there is no such row the entry names nobody, which is true,
        // rather than the account, which is not.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var userIds = local.Where(l => l.AppUserId is not null).Select(l => l.AppUserId!.Value).Distinct().ToList();
        var userNames = userIds.Count == 0
            ? []
            : await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        (string? Id, string? Name) Who(TicketTimeEntry? origin, string? technicianId, string? technicianName)
        {
            if (!account.IsAccount(ticket.PsaConnectionId, technicianId)) return (technicianId, technicianName);
            return origin?.AppUserId is { } uid && userNames.TryGetValue(uid, out var person) ? (null, person) : (null, null);
        }
        TimeRow Local(TicketTimeEntry l) => LocalRowAs(l, Who(l, l.TechnicianExternalId, l.TechnicianName));

        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            return Ok(local.OrderByDescending(l => l.EntryDate).Select(Local));

        var connector = await connectors.ResolveAsync(ticket.PsaConnectionId, ct);
        var entries = await connector.GetTimeEntriesAsync(ticket.ExternalTicketId, ct);

        var portalByExternalId = local
            .Where(l => l.ExternalEntryId is not null)
            .ToDictionary(l => l.ExternalEntryId!, l => l);

        var rows = entries.Select(e =>
        {
            portalByExternalId.TryGetValue(e.ExternalId, out var origin);
            var who = Who(origin, e.TechnicianExternalId, e.TechnicianName);
            return new TimeRow(
                // Composed exactly as the thread composes it, through the same helper — two
                // renderings of one entry that disagreed would be a bug waiting to happen.
                // Staff-only endpoint; internal text never leaves it.
                e.ExternalId, e.Hours, e.Billable, e.BillableOption.ToString(), e.EntryDate,
                TimeEntryNarrative.Compose(e.Notes, e.InternalNotes),
                who.Id, who.Name, e.WorkType,
                origin is null ? nameof(TimeEntrySource.Provider) : nameof(TimeEntrySource.Portal),
                nameof(TimeEntrySyncStatus.Synced), null);
        }).ToList();

        // Anything the PSA rejected or has not accepted yet: it exists only here.
        rows.AddRange(local
            .Where(l => l.SyncStatus != TimeEntrySyncStatus.Synced)
            .Select(Local));

        return Ok(rows.OrderByDescending(r => r.EntryDate));
    }

    private static TimeRow LocalRowAs(TicketTimeEntry l, (string? Id, string? Name) who) => new(
        l.ExternalEntryId ?? l.Id.ToString(), l.Hours, l.Billable,
        l.Billable ? nameof(BillableOption.Billable) : nameof(BillableOption.DoNotBill),
        l.EntryDate, l.Notes, who.Id, who.Name, l.WorkTypeLabel,
        l.Source.ToString(), l.SyncStatus.ToString(), l.SyncError);

    /// <summary>One row of the time panel, whichever side it came from.</summary>
    public sealed record TimeRow(
        string ExternalId, decimal Hours, bool Billable, string BillableOption, DateTimeOffset EntryDate,
        string? Notes, string? Technician, string? TechnicianName, string? WorkType,
        string Source, string SyncStatus, string? SyncError);

    [HttpPost("{id:guid}/time")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> LogTime(Guid id, [FromBody] LogTimeRequest req, CancellationToken ct)
    {
        var (ticket, connector) = await LoadSyncedAsync(id, ct);
        var billable = ParseBillable(req.Billable);

        // Written before the push, not after: a rejected entry used to disappear with the 400, taking
        // the technician's logged work with it and leaving nothing to retry from.
        var record = new TicketTimeEntry
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticket.Id,
            NoteId = req.NoteId is { } nid && await db.TicketNotes.AnyAsync(n => n.Id == nid && n.TicketId == id, ct)
                ? nid : null,
            Hours = req.Hours,
            Billable = billable == BillableOption.Billable,
            Notes = req.Notes,
            WorkTypeId = Blank(req.WorkType),
            WorkTypeLabel = await WorkTypeLabelAsync(ticket, Blank(req.WorkType), ct),
            WorkRoleId = Blank(req.WorkRole),
            Source = TimeEntrySource.Portal,
            SyncStatus = TimeEntrySyncStatus.Pending,
            EntryDate = DateTimeOffset.UtcNow,
            // Stamped at CREATION, not read again at push time: a retry can happen days later and
            // by a different person, and the hour belongs to whoever did the work. Null when this
            // user has no identity on this connection — the connector then falls back to the
            // connection's default resource, which is the behaviour every entry had before.
            TechnicianExternalId = await MyTechnicianIdAsync(ticket.PsaConnectionId, ct),
            // Who logged it HERE, which is the only attribution that survives for a technician the
            // PSA has never heard of. Stamped at creation for the same reason as the line above: a
            // retry days later by someone else must not rewrite whose hour it was.
            AppUserId = user.UserId,
        };
        db.TicketTimeEntries.Add(record);
        await db.SaveChangesAsync(ct);

        if (!await PushAsync(record, ticket, connector, ct))
            throw new ValidationFailedException(record.SyncError ?? "The PSA rejected the time entry.");

        return Ok(await RecomputeAsync(ticket, connector, ct));
    }

    /// <summary>
    /// Re-sends an entry the PSA previously rejected. Nothing about the entry is re-entered: the
    /// original hours, work type and notes are pushed again, so fixing the connection is all it
    /// takes to recover work that would otherwise have to be typed in a second time.
    /// </summary>
    [HttpPost("{id:guid}/time/{entryId:guid}/retry")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Retry(Guid id, Guid entryId, CancellationToken ct)
    {
        var (ticket, connector) = await LoadSyncedAsync(id, ct);
        var record = await db.TicketTimeEntries.FirstOrDefaultAsync(t => t.Id == entryId && t.TicketId == id, ct)
            ?? throw new NotFoundException("Time entry");
        if (record.SyncStatus == TimeEntrySyncStatus.Synced)
            throw new ValidationFailedException("This entry is already recorded in the PSA.");

        if (!await PushAsync(record, ticket, connector, ct))
            throw new ValidationFailedException(record.SyncError ?? "The PSA rejected the time entry again.");

        return Ok(await RecomputeAsync(ticket, connector, ct));
    }

    /// <summary>
    /// This user's identity in the given PSA, or null when they have none. Null is the ordinary
    /// case for an unmapped user and means "fall back to the connection default" — not an error.
    /// </summary>
    private async Task<string?> MyTechnicianIdAsync(Guid psaConnectionId, CancellationToken ct)
        => user.UserId is not { } uid ? null
            : await db.UserPsaIdentities.AsNoTracking()
                .Where(i => i.AppUserId == uid && i.PsaConnectionId == psaConnectionId)
                .Select(i => i.ExternalTechnicianId)
                .FirstOrDefaultAsync(ct);

    /// <summary>Pushes a portal record to the PSA and stamps the outcome on it either way.</summary>
    private async Task<bool> PushAsync(TicketTimeEntry record, Ticket ticket, IServiceManagementConnector connector, CancellationToken ct)
    {
        CreateTimeEntryResult result;
        try
        {
            result = await connector.AddTimeEntryAsync(ticket.ExternalTicketId!,
                new UnifiedTimeEntryCreateRequest(record.Hours, record.WorkTypeId, record.WorkRoleId,
                    record.Billable ? BillableOption.Billable : BillableOption.DoNotBill,
                    record.Notes, MemberIdentifier: record.TechnicianExternalId), ct);
        }
        catch (ConnectorException ex)
        {
            // A provider REJECTION is a failed push, not an unhandled fault. Letting it propagate
            // left the row carrying whatever error it failed with last time, so a retry that failed
            // for a NEW reason still displayed the old one — which is how an admin ends up "fixing"
            // a setting that was already correct. Store what the provider actually said, now.
            result = new CreateTimeEntryResult(false, null, ex.Message);
        }

        if (!result.Success)
        {
            record.SyncStatus = TimeEntrySyncStatus.Failed;
            record.SyncError = result.Error;
            await db.SaveChangesAsync(ct);
            return false;
        }

        record.ExternalEntryId = result.ExternalId;
        record.SyncStatus = TimeEntrySyncStatus.Synced;
        record.SyncError = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Resolves a work-type id to its label once, so the row stays readable later.</summary>
    private async Task<string?> WorkTypeLabelAsync(Ticket ticket, string? workTypeId, CancellationToken ct)
    {
        if (workTypeId is null) return null;
        try
        {
            var fields = await admin.GetFieldsAsync(ticket.PsaConnectionId, ct);
            return fields.WorkTypes.FirstOrDefault(o => o.Value == workTypeId)?.Label;
        }
        catch (Exception) { return null; } // a label is cosmetic; never fail a time log over it
    }

    [HttpPut("{id:guid}/time/{entryId}")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Update(Guid id, string entryId, [FromBody] UpdateTimeRequest req, CancellationToken ct)
    {
        var (ticket, connector) = await LoadSyncedAsync(id, ct);
        await EnsureEntryBelongsToTicketAsync(ticket, connector, entryId, ct);
        var result = await connector.UpdateTimeEntryAsync(entryId,
            new UnifiedTimeEntryUpdate(req.Hours, req.Billable is null ? null : ParseBillable(req.Billable), req.Notes), ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the change.");
        return Ok(await RecomputeAsync(ticket, connector, ct));
    }

    [HttpDelete("{id:guid}/time/{entryId}")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Delete(Guid id, string entryId, CancellationToken ct)
    {
        var (ticket, connector) = await LoadSyncedAsync(id, ct);

        // A rejected entry has no provider counterpart — its id is the portal's own. Asking the PSA
        // to delete it would fail, leaving the row stuck on screen with no way to clear it.
        if (Guid.TryParse(entryId, out var localId))
        {
            var unsynced = await db.TicketTimeEntries
                .FirstOrDefaultAsync(t => t.Id == localId && t.TicketId == id && t.ExternalEntryId == null, ct);
            if (unsynced is not null)
            {
                db.TicketTimeEntries.Remove(unsynced);
                await db.SaveChangesAsync(ct);
                return Ok(await RecomputeAsync(ticket, connector, ct));
            }
        }

        await EnsureEntryBelongsToTicketAsync(ticket, connector, entryId, ct);
        var result = await connector.DeleteTimeEntryAsync(entryId, ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the deletion.");

        // Drop the portal's mirror too, or the entry reappears as an unsynced ghost.
        var local = await db.TicketTimeEntries.Where(t => t.TicketId == id && t.ExternalEntryId == entryId).ToListAsync(ct);
        if (local.Count > 0) { db.TicketTimeEntries.RemoveRange(local); await db.SaveChangesAsync(ct); }

        return Ok(await RecomputeAsync(ticket, connector, ct));
    }

    // ---- helpers ----

    private async Task<Ticket> LoadAsync(Guid id, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        return await scopeQuery.FindAsync(db.Tickets, id, uid, Permissions.TicketsLogTime, ct)
            ?? throw new NotFoundException("Ticket");
    }

    /// <summary>
    /// Confirms a provider-side time entry actually belongs to this ticket before it is edited or
    /// deleted. Without this the entry id is simply forwarded to the PSA, so passing another
    /// ticket's entry id would edit or delete THAT ticket's time — including a ticket the caller
    /// cannot otherwise see, and potentially another customer's.
    ///
    /// Checked against the provider rather than the portal's mirror on purpose: the mirror can lag
    /// a sync, and a stale mirror would reject a legitimate edit. The provider owns the ticket's
    /// time, so it is also the right thing to ask.
    /// </summary>
    private async Task EnsureEntryBelongsToTicketAsync(
        Ticket ticket, IServiceManagementConnector connector, string entryId, CancellationToken ct)
    {
        var entries = await connector.GetTimeEntriesAsync(ticket.ExternalTicketId!, ct);
        if (!entries.Any(e => e.ExternalId == entryId))
            throw new NotFoundException("Time entry");
    }

    private async Task<(Ticket ticket, IServiceManagementConnector connector)> LoadSyncedAsync(Guid id, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new ValidationFailedException("This ticket is not yet synced to the PSA, so time cannot be logged.");
        return (ticket, await connectors.ResolveAsync(ticket.PsaConnectionId, ct));
    }

    // Re-read the ticket's entries from the PSA (source of truth) and rewrite the portal aggregates.
    private async Task<object> RecomputeAsync(Ticket ticket, IServiceManagementConnector connector, CancellationToken ct)
    {
        var entries = await connector.GetTimeEntriesAsync(ticket.ExternalTicketId!, ct);
        ticket.TimeWorkedHours = entries.Sum(e => e.Hours);
        ticket.BillableHours = entries.Where(e => e.Billable).Sum(e => e.Hours);
        ticket.NonBillableHours = entries.Where(e => !e.Billable).Sum(e => e.Hours);
        await db.SaveChangesAsync(ct);
        return new
        {
            count = entries.Count,
            timeWorkedHours = ticket.TimeWorkedHours,
            billableHours = ticket.BillableHours,
            nonBillableHours = ticket.NonBillableHours,
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static BillableOption ParseBillable(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "donotbill" or "do not bill" or "nonbillable" => BillableOption.DoNotBill,
        "nocharge" or "no charge" => BillableOption.NoCharge,
        _ => BillableOption.Billable,
    };

    public sealed record LogTimeRequest(
        [Range(0.01, 1000, ErrorMessage = "Hours must be between 0.01 and 1000.")] decimal Hours,
        string? Billable,
        [StringLength(2000)] string? Notes,
        string? WorkType,
        string? WorkRole,
        // The conversation note this time was logged with (reply + time in one send), so the
        // thread can show the hours on the reply itself. Silently dropped if it isn't a note
        // on THIS ticket — a bad link is worse than no link.
        Guid? NoteId = null);

    public sealed record UpdateTimeRequest(
        [Range(0.01, 1000)] decimal? Hours,
        string? Billable,
        [StringLength(2000)] string? Notes);
}
