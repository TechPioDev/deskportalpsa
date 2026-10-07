using Desk.Application.Admin;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>The ticket's worked / billable / non-billable totals after a change, as the time panel reports them.</summary>
public sealed record TimeTotals(int Count, decimal TimeWorkedHours, decimal BillableHours, decimal NonBillableHours);

/// <summary>The entry written, and the ticket's totals when they were recomputed (null when the push is deferred or failed).</summary>
public sealed record TimeLogResult(TicketTimeEntry Entry, TimeTotals? Totals);

/// <summary>
/// Writes a ticket's time the one way the portal writes it: a portal row first (so a rejected push
/// is still kept, for a retry), then the push to the PSA when the ticket has one, then the ticket's
/// totals recomputed from the system of record. The time panel's controller and a stopped work
/// session (Phase 6) both log time through here, so an hour logged by a clock and an hour typed in
/// are the same kind of hour.
/// </summary>
public sealed class TicketTimeWriter(DeskDbContext db, IConnectionAdminService admin, IAuditWriter? audit = null, Sync.OutboundQueue? outbound = null)
{
    /// <summary>How a push went: taken, not answered (it may be tried again), or refused.</summary>
    public sealed record PushResult(bool Ok, bool Transient, string? Error);

    /// <summary>A user's identity in the given PSA, or null when they have none (then the connection's default resource is used).</summary>
    public async Task<string?> TechnicianIdAsync(Guid? userId, Guid psaConnectionId, CancellationToken ct)
        => userId is not { } uid ? null
            : await db.UserPsaIdentities.AsNoTracking()
                .Where(i => i.AppUserId == uid && i.PsaConnectionId == psaConnectionId)
                .Select(i => i.ExternalTechnicianId)
                .FirstOrDefaultAsync(ct);

    /// <summary>Resolves a work-type id to its label once, so the row stays readable later. A label is cosmetic; it never fails a log.</summary>
    public async Task<string?> WorkTypeLabelAsync(Ticket ticket, string? workTypeId, CancellationToken ct)
    {
        if (workTypeId is null) return null;
        try
        {
            if (ticket.PsaConnectionId is not { } labelConnectionId) return null;
            var fields = await admin.GetFieldsAsync(labelConnectionId, ct);
            return fields.WorkTypes.FirstOrDefault(o => o.Value == workTypeId)?.Label;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Creates the portal row for an hour of work on a ticket, pushes it when the ticket has a PSA,
    /// recomputes the ticket's totals and audits it. The row is returned either way: a rejected push
    /// leaves it Failed with the provider's words, still logged, still retryable.
    /// </summary>
    public async Task<TimeLogResult> LogAsync(Ticket ticket, IServiceManagementConnector? connector, Guid? userId,
        decimal hours, bool billable, string? notes, string? workTypeId, string? workRoleId, Guid? noteId, DateTimeOffset entryDate,
        object? auditExtra, CancellationToken ct, Guid? workSessionId = null, bool pushNow = true)
    {
        var record = new TicketTimeEntry
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticket.Id,
            PsaConnectionId = ticket.PsaConnectionId,
            NoteId = noteId,
            Hours = hours,
            Billable = billable,
            Notes = notes,
            WorkTypeId = workTypeId,
            WorkTypeLabel = await WorkTypeLabelAsync(ticket, workTypeId, ct),
            WorkRoleId = workRoleId,
            Source = TimeEntrySource.Portal,
            SyncStatus = TimeEntrySyncStatus.Pending,
            EntryDate = entryDate,
            WorkSessionId = workSessionId,
            // Stamped at creation, not at push time: a retry days later by someone else must not rewrite whose hour it was.
            TechnicianExternalId = ticket.PsaConnectionId is { } tc ? await TechnicianIdAsync(userId, tc, ct) : null,
            AppUserId = userId,
        };
        db.TicketTimeEntries.Add(record);
        await db.SaveChangesAsync(ct);

        if (ticket.Origin != TicketOrigin.Psa)
        {
            // Recorded, not pending: there is no provider for it to be waiting on.
            record.SyncStatus = TimeEntrySyncStatus.Synced;
            await db.SaveChangesAsync(ct);
            await AuditAsync(ticket, new { entryId = record.Id, record.Hours, record.Billable, workedAt = record.EntryDate }, auditExtra, ct);
            return new TimeLogResult(record, await RecomputeLocalAsync(ticket, ct));
        }
        if (!HasPsa(ticket) || !pushNow || connector is null)
        {
            // A PSA ticket not yet synced, or a caller who pushes later (a stopped clock pushes once the
            // person's gate is released): the entry waits as Pending, never "recorded" by itself.
            await AuditAsync(ticket, new { entryId = record.Id, record.Hours, record.Billable, pushed = false, deferred = true }, auditExtra, ct);
            return new TimeLogResult(record, null);
        }

        var pushed = await PushAsync(record, ticket, connector, ct);
        await AuditAsync(ticket, new { entryId = record.Id, record.Hours, record.Billable, pushed }, auditExtra, ct);
        return new TimeLogResult(record, pushed ? await RecomputeAsync(ticket, connector, ct) : null);
    }

    /// <summary>A ticket whose hours a PSA holds: synced, with a connection and the provider's id.</summary>
    public static bool HasPsa(Ticket ticket) => ticket.Origin == TicketOrigin.Psa && ticket.PsaConnectionId is not null && !string.IsNullOrEmpty(ticket.ExternalTicketId);

    private Task AuditAsync(Ticket ticket, object detail, object? extra, CancellationToken ct)
        => audit is null ? Task.CompletedTask
            : audit.WriteAsync("ticket.time.logged", "Ticket", ticket.Id.ToString(), extra is null ? detail : new { time = detail, context = extra }, ct);

    /// <summary>
    /// Pushes a portal record to the PSA and stamps the outcome on it either way. Where the PSA
    /// could not be reached the hour is not left as failed for somebody to notice: it waits, and is
    /// tried again by the outbound queue.
    /// </summary>
    public async Task<bool> PushAsync(TicketTimeEntry record, Ticket ticket, IServiceManagementConnector connector, CancellationToken ct)
    {
        var pushed = await PushDetailedAsync(record, ticket, connector, ct);
        if (pushed.Ok || !pushed.Transient || outbound is null || !HasPsa(ticket)) return pushed.Ok;

        // Once only: an hour already waiting is not put in the queue a second time.
        var waiting = await db.OutboundOperations.AnyAsync(o => o.Kind == Desk.Domain.Sync.OutboundKind.TimeEntry
            && o.TargetId == record.Id && o.State == Desk.Domain.Sync.OutboundState.Pending, ct);
        if (!waiting)
        {
            var by = record.AppUserId is { } author
                ? await db.AppUsers.AsNoTracking().Where(u => u.Id == author).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
            var op = outbound.Enqueue(ticket, Desk.Domain.Sync.OutboundKind.TimeEntry, $"{record.Hours:0.##} h logged",
                new Sync.OutboundTime(record.Id), record.Id, Guid.NewGuid().ToString("N"), record.AppUserId, null, by, pushed.Error, uncertain: true);
            record.SyncStatus = TimeEntrySyncStatus.Pending;
            await db.SaveChangesAsync(ct);
            await outbound.AuditQueuedAsync(op, ct);
        }
        return false;
    }

    /// <summary>The push itself, saying how it went. <paramref name="fromQueue"/>: a retry by the outbound queue, which keeps the hour waiting itself.</summary>
    public async Task<PushResult> PushDetailedAsync(
        TicketTimeEntry record, Ticket ticket, IServiceManagementConnector connector, CancellationToken ct, bool fromQueue = false)
    {
        // A worklog read FROM the PSA is the PSA's own entry. Sent to it, it would come back as a
        // second one, and that one would be read and sent again. Nothing asks for this; were
        // something to, it is refused here, where every push passes.
        if (record.Source == TimeEntrySource.Provider)
            throw new InvalidOperationException("A time entry read from the PSA is never sent to it.");

        var request = new UnifiedTimeEntryCreateRequest(record.Hours, record.WorkTypeId, record.WorkRoleId,
            record.Billable ? BillableOption.Billable : BillableOption.DoNotBill,
            record.Notes, MemberIdentifier: record.TechnicianExternalId);

        // A second try. The first one failed as far as the portal could tell - but a push whose
        // answer was lost may have been carried out, and sent again it is a second entry: the
        // customer billed the same hour twice. So the PSA is asked first whether it has it.
        // "Failed", or still waiting after a try that went wrong: either way it has been sent before.
        if ((record.SyncStatus == TimeEntrySyncStatus.Failed || record.SyncError is not null) && string.IsNullOrEmpty(record.ExternalEntryId))
        {
            try
            {
                var linked = await db.TicketTimeEntries.AsNoTracking()
                    .Where(t => t.TicketId == ticket.Id && t.Id != record.Id && t.ExternalEntryId != null)
                    .Select(t => t.ExternalEntryId!)
                    .ToListAsync(ct);
                if (await connector.FindTimeEntryAsync(ticket.ExternalTicketId!, request, record.CreatedAt, linked, ct) is { } already)
                {
                    record.ExternalEntryId = already;
                    record.SyncStatus = TimeEntrySyncStatus.Synced;
                    record.SyncError = null;
                    await db.SaveChangesAsync(ct);
                    if (audit is not null)
                        await audit.WriteAsync("ticket.time.reconciled", "Ticket", ticket.Id.ToString(),
                            new { entryId = record.Id, externalEntryId = already, record.Hours }, ct);
                    return new PushResult(true, false, null);
                }
            }
            catch (ConnectorException ex)
            {
                // Cannot tell whether it is there, so it is not sent: a duplicate on an invoice is
                // worse than an hour that waits for the PSA to answer.
                record.SyncError = $"Could not check whether the PSA already has this entry, so it was not sent again: {ex.Message}";
                await db.SaveChangesAsync(ct);
                return new PushResult(false, ex.IsTransient, record.SyncError);
            }
        }

        CreateTimeEntryResult result;
        var transient = false;
        try
        {
            result = await connector.AddTimeEntryAsync(ticket.ExternalTicketId!, request, ct);
        }
        catch (ConnectorException ex)
        {
            // A provider REJECTION is a failed push, not an unhandled fault: keep what it said, now.
            result = new CreateTimeEntryResult(false, null, ex.Message);
            transient = ex.IsTransient;
        }

        if (!result.Success)
        {
            // Waiting, where the queue is holding it; failed otherwise, for the retry on the ticket.
            record.SyncStatus = transient && fromQueue ? TimeEntrySyncStatus.Pending : TimeEntrySyncStatus.Failed;
            record.SyncError = result.Error;
            await db.SaveChangesAsync(ct);
            return new PushResult(false, transient, result.Error);
        }

        record.ExternalEntryId = result.ExternalId;
        record.SyncStatus = TimeEntrySyncStatus.Synced;
        record.SyncError = null;
        await db.SaveChangesAsync(ct);
        return new PushResult(true, false, null);
    }

    /// <summary>Re-reads the ticket's entries from the PSA (the system of record) and rewrites the portal aggregates.</summary>
    public async Task<TimeTotals> RecomputeAsync(Ticket ticket, IServiceManagementConnector connector, CancellationToken ct)
    {
        var entries = await connector.GetTimeEntriesAsync(ticket.ExternalTicketId!, ct);
        ticket.TimeWorkedHours = entries.Sum(e => e.Hours);
        ticket.BillableHours = entries.Where(e => e.Billable).Sum(e => e.Hours);
        ticket.NonBillableHours = entries.Where(e => !e.Billable).Sum(e => e.Hours);
        await db.SaveChangesAsync(ct);
        return new TimeTotals(entries.Count, ticket.TimeWorkedHours, ticket.BillableHours, ticket.NonBillableHours);
    }

    /// <summary>The same aggregates for a ticket with no provider, summed from the rows held here, which are the whole truth for it.</summary>
    public async Task<TimeTotals> RecomputeLocalAsync(Ticket ticket, CancellationToken ct)
    {
        var entries = await db.TicketTimeEntries.AsNoTracking()
            .Where(t => t.TicketId == ticket.Id)
            .Select(t => new { t.Hours, t.Billable })
            .ToListAsync(ct);
        ticket.TimeWorkedHours = entries.Sum(e => e.Hours);
        ticket.BillableHours = entries.Where(e => e.Billable).Sum(e => e.Hours);
        ticket.NonBillableHours = entries.Where(e => !e.Billable).Sum(e => e.Hours);
        await db.SaveChangesAsync(ct);
        return new TimeTotals(entries.Count, ticket.TimeWorkedHours, ticket.BillableHours, ticket.NonBillableHours);
    }
}
