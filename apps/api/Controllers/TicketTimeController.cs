using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
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
    ITicketScopeQuery scopeQuery, ICurrentUser user, IAuditWriter? audit = null) : ControllerBase
{
    /// <summary>How far back a board ticket's time may be dated. Further than this is a correction, not a log.</summary>
    public const int MaxBackdateDays = 30;

    [HttpGet("{id:guid}/time-options")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> TimeOptions(Guid id, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        // A board with no provider has no work types or roles to choose from: hours are simply hours.
        if (ticket.PsaConnectionId is not { } optionsConnectionId)
            return Ok(new { workTypes = Array.Empty<object>(), workRoles = Array.Empty<object>(), defaultWorkTypeId = (string?)null, defaultWorkRoleId = (string?)null });
        // Read from the cached field set (discovered when the connection was configured).
        var fields = await admin.GetFieldsAsync(optionsConnectionId, ct);
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
        var myTechnician = ticket.PsaConnectionId is { } mc ? await MyTechnicianIdAsync(mc, ct) : null;
        var lead = user.HasPermission(Permissions.BoardsManage);
        bool MayChange(TicketTimeEntry? origin, string? technicianId) => MayChangeEntry(origin, technicianId, myTechnician, lead);
        TimeRow Local(TicketTimeEntry l) => LocalRowAs(l, Who(l, l.TechnicianExternalId, l.TechnicianName), MayChange(l, l.TechnicianExternalId));

        if (string.IsNullOrEmpty(ticket.ExternalTicketId) || ticket.PsaConnectionId is not { } listConnectionId)
            return Ok(local.OrderByDescending(l => l.EntryDate).Select(Local));

        var connector = await connectors.ResolveAsync(listConnectionId, ct);
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
                nameof(TimeEntrySyncStatus.Synced), null, MayChange(origin, e.TechnicianExternalId));
        }).ToList();

        // Anything the PSA rejected or has not accepted yet: it exists only here.
        rows.AddRange(local
            .Where(l => l.SyncStatus != TimeEntrySyncStatus.Synced)
            .Select(Local));

        return Ok(rows.OrderByDescending(r => r.EntryDate));
    }

    private static TimeRow LocalRowAs(TicketTimeEntry l, (string? Id, string? Name) who, bool mayChange) => new(
        l.ExternalEntryId ?? l.Id.ToString(), l.Hours, l.Billable,
        l.Billable ? nameof(BillableOption.Billable) : nameof(BillableOption.DoNotBill),
        l.EntryDate, l.Notes, who.Id, who.Name, l.WorkTypeLabel,
        l.Source.ToString(), l.SyncStatus.ToString(), l.SyncError, mayChange);

    /// <summary>One row of the time panel, whichever side it came from.</summary>
    /// <param name="MayChange">Whether THIS caller may edit or delete it: their own, or they lead boards.</param>
    public sealed record TimeRow(
        string ExternalId, decimal Hours, bool Billable, string BillableOption, DateTimeOffset EntryDate,
        string? Notes, string? Technician, string? TechnicianName, string? WorkType,
        string Source, string SyncStatus, string? SyncError, bool MayChange = false);

    /// <summary>
    /// A person's own time is theirs to correct; someone else's is not, unless they lead the boards.
    /// "Own" is whoever logged it here, or - for an hour entered straight into the PSA, which the
    /// portal never saw - the person whose PSA identity it carries.
    /// </summary>
    private bool MayChangeEntry(TicketTimeEntry? mirror, string? technicianId, string? myTechnician, bool lead)
        => lead
           || (mirror?.AppUserId is { } author ? author == user.UserId
               : technicianId is not null && technicianId == myTechnician);

    private async Task EnsureMayChangeAsync(Ticket ticket, TicketTimeEntry? mirror, string? technicianId, CancellationToken ct)
    {
        var mine = ticket.PsaConnectionId is { } c ? await MyTechnicianIdAsync(c, ct) : null;
        if (!MayChangeEntry(mirror, technicianId, mine, user.HasPermission(Permissions.BoardsManage)))
            throw new ForbiddenException("Someone else logged this time. Ask them, or a board lead, to change it.");
    }

    private Task AuditAsync(Ticket ticket, string action, object detail, CancellationToken ct)
        => audit is null ? Task.CompletedTask : audit.WriteAsync(action, "Ticket", ticket.Id.ToString(), detail, ct);

    [HttpPost("{id:guid}/time")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> LogTime(Guid id, [FromBody] LogTimeRequest req, CancellationToken ct)
    {
        var (ticket, connector) = await LoadSyncedAsync(id, ct);
        var billable = ParseBillable(req.Billable);
        var now = DateTimeOffset.UtcNow;
        if (req.WorkedAt is { } worked)
        {
            // A PSA dates its own entries when they arrive; a back-dated copy here would disagree with it.
            if (connector is not null)
                throw new ValidationFailedException("This ticket's time is dated by the PSA. Log it there to date it earlier.");
            if (worked > now.AddMinutes(10))
                throw new ValidationFailedException("Time cannot be logged for work that has not happened yet.");
            if (worked < now.AddDays(-MaxBackdateDays))
                throw new ValidationFailedException($"Time can be dated up to {MaxBackdateDays} days back.");
        }

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
            EntryDate = req.WorkedAt ?? now,
            // Stamped at CREATION, not read again at push time: a retry can happen days later and
            // by a different person, and the hour belongs to whoever did the work. Null when this
            // user has no identity on this connection — the connector then falls back to the
            // connection's default resource, which is the behaviour every entry had before.
            TechnicianExternalId = ticket.PsaConnectionId is { } tc ? await MyTechnicianIdAsync(tc, ct) : null,
            // Who logged it HERE, which is the only attribution that survives for a technician the
            // PSA has never heard of. Stamped at creation for the same reason as the line above: a
            // retry days later by someone else must not rewrite whose hour it was.
            AppUserId = user.UserId,
        };
        db.TicketTimeEntries.Add(record);
        await db.SaveChangesAsync(ct);

        if (connector is null)
        {
            // Recorded, not pending: there is no provider for it to be waiting on, and "not
            // recorded" on a ticket that will never be pushed reads as a failure that never happened.
            record.SyncStatus = TimeEntrySyncStatus.Synced;
            await db.SaveChangesAsync(ct);
            await AuditAsync(ticket, "ticket.time.logged", new { entryId = record.Id, record.Hours, record.Billable, workedAt = record.EntryDate }, ct);
            return Ok(await RecomputeLocalAsync(ticket, ct));
        }

        var pushed = await PushAsync(record, ticket, connector, ct);
        // Recorded either way: a rejected entry is still logged work, kept here for a retry.
        await AuditAsync(ticket, "ticket.time.logged", new { entryId = record.Id, record.Hours, record.Billable, pushed }, ct);
        if (!pushed)
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
        if (connector is null)
            throw new ValidationFailedException("This ticket is on an internal board, so its time is recorded here and there is nothing to retry.");
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
            if (ticket.PsaConnectionId is not { } labelConnectionId) return null;
            var fields = await admin.GetFieldsAsync(labelConnectionId, ct);
            return fields.WorkTypes.FirstOrDefault(o => o.Value == workTypeId)?.Label;
        }
        catch (Exception) { return null; } // a label is cosmetic; never fail a time log over it
    }

    [HttpPut("{id:guid}/time/{entryId}")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Update(Guid id, string entryId, [FromBody] UpdateTimeRequest req, CancellationToken ct)
    {
        var (ticket, connector) = await LoadSyncedAsync(id, ct);
        if (connector is null)
            return Ok(await UpdateLocalEntryAsync(ticket, entryId, req, ct));
        var existing = await EnsureEntryBelongsToTicketAsync(ticket, connector, entryId, ct);
        var mirror = await db.TicketTimeEntries.FirstOrDefaultAsync(t => t.TicketId == id && t.ExternalEntryId == entryId, ct);
        await EnsureMayChangeAsync(ticket, mirror, existing.TechnicianExternalId, ct);
        var result = await connector.UpdateTimeEntryAsync(entryId,
            new UnifiedTimeEntryUpdate(req.Hours, req.Billable is null ? null : ParseBillable(req.Billable), req.Notes), ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the change.");
        // Keep the portal's copy in step. Left alone it kept the old hours, and the daily figures and
        // client reviews read it.
        if (mirror is not null)
        {
            if (req.Hours is { } h) mirror.Hours = h;
            if (req.Billable is not null) mirror.Billable = ParseBillable(req.Billable) == BillableOption.Billable;
            if (req.Notes is not null) mirror.Notes = req.Notes;
        }
        await AuditAsync(ticket, "ticket.time.edited", new
        {
            entryId,
            from = new { existing.Hours, existing.Billable },
            to = new { hours = req.Hours ?? existing.Hours, billable = req.Billable is null ? existing.Billable : ParseBillable(req.Billable) == BillableOption.Billable },
        }, ct);
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
                await EnsureMayChangeAsync(ticket, unsynced, unsynced.TechnicianExternalId, ct);
                db.TicketTimeEntries.Remove(unsynced);
                await db.SaveChangesAsync(ct);
                await AuditAsync(ticket, "ticket.time.deleted", new { entryId, unsynced.Hours, unsynced.Billable }, ct);
                return Ok(connector is null
                    ? await RecomputeLocalAsync(ticket, ct)
                    : await RecomputeAsync(ticket, connector, ct));
            }
        }

        if (connector is null)
            throw new NotFoundException("Time entry");

        var gone = await EnsureEntryBelongsToTicketAsync(ticket, connector, entryId, ct);
        var copy = await db.TicketTimeEntries.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == id && t.ExternalEntryId == entryId, ct);
        await EnsureMayChangeAsync(ticket, copy, gone.TechnicianExternalId, ct);
        var result = await connector.DeleteTimeEntryAsync(entryId, ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the deletion.");

        // Drop the portal's mirror too, or the entry reappears as an unsynced ghost.
        var local = await db.TicketTimeEntries.Where(t => t.TicketId == id && t.ExternalEntryId == entryId).ToListAsync(ct);
        if (local.Count > 0) { db.TicketTimeEntries.RemoveRange(local); await db.SaveChangesAsync(ct); }
        await AuditAsync(ticket, "ticket.time.deleted", new { entryId, gone.Hours, gone.Billable }, ct);

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
    private async Task<UnifiedTimeEntry> EnsureEntryBelongsToTicketAsync(
        Ticket ticket, IServiceManagementConnector connector, string entryId, CancellationToken ct)
    {
        var entries = await connector.GetTimeEntriesAsync(ticket.ExternalTicketId!, ct);
        return entries.FirstOrDefault(e => e.ExternalId == entryId)
            ?? throw new NotFoundException("Time entry");
    }

    /// <summary>
    /// The ticket plus, when it belongs to a PSA, the connector its time must reach. A ticket on the
    /// team's own board returns a null connector: the hours are recorded here and pushed nowhere,
    /// which is what makes internal work loggable at all.
    /// </summary>
    private async Task<(Ticket ticket, IServiceManagementConnector? connector)> LoadSyncedAsync(Guid id, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        if (ticket.Origin != TicketOrigin.Psa || ticket.PsaConnectionId is not { } connectionId)
            return (ticket, null);
        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new ValidationFailedException("This ticket is not yet synced to the PSA, so time cannot be logged.");
        return (ticket, await connectors.ResolveAsync(connectionId, ct));
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

    /// <summary>
    /// The same aggregates for a ticket with no provider, summed from the rows held here — which are
    /// the whole truth for such a ticket. Technician productivity reads these totals, so an internal
    /// ticket's hours count exactly as a client ticket's do.
    /// </summary>
    private async Task<object> RecomputeLocalAsync(Ticket ticket, CancellationToken ct)
    {
        var entries = await db.TicketTimeEntries.AsNoTracking()
            .Where(t => t.TicketId == ticket.Id)
            .Select(t => new { t.Hours, t.Billable })
            .ToListAsync(ct);
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

    /// <summary>Editing an hour that lives only here: no provider holds a copy to keep in step.</summary>
    private async Task<object> UpdateLocalEntryAsync(Ticket ticket, string entryId, UpdateTimeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(entryId, out var localId)) throw new NotFoundException("Time entry");
        var entry = await db.TicketTimeEntries.FirstOrDefaultAsync(t => t.Id == localId && t.TicketId == ticket.Id, ct)
            ?? throw new NotFoundException("Time entry");
        await EnsureMayChangeAsync(ticket, entry, entry.TechnicianExternalId, ct);
        var from = new { entry.Hours, entry.Billable };
        if (req.Hours is { } hours) entry.Hours = hours;
        if (req.Billable is not null) entry.Billable = ParseBillable(req.Billable) == BillableOption.Billable;
        if (req.Notes is not null) entry.Notes = req.Notes;
        await db.SaveChangesAsync(ct);
        await AuditAsync(ticket, "ticket.time.edited", new { entryId, from, to = new { entry.Hours, entry.Billable } }, ct);
        return await RecomputeLocalAsync(ticket, ct);
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
        Guid? NoteId = null,
        // When the work was done, for a board ticket only, up to MaxBackdateDays back. Null is now.
        DateTimeOffset? WorkedAt = null);

    public sealed record UpdateTimeRequest(
        [Range(0.01, 1000)] decimal? Hours,
        string? Billable,
        [StringLength(2000)] string? Notes);
}
