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
public sealed class TicketTimeWriter(DeskDbContext db, IConnectionAdminService admin, IAuditWriter? audit = null)
{
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

    /// <summary>Pushes a portal record to the PSA and stamps the outcome on it either way.</summary>
    public async Task<bool> PushAsync(TicketTimeEntry record, Ticket ticket, IServiceManagementConnector connector, CancellationToken ct)
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
            // A provider REJECTION is a failed push, not an unhandled fault: keep what it said, now.
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
