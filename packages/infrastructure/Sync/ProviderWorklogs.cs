using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Time a technician entered in the PSA itself, kept in the portal as worklogs.
///
/// Until this, such time reached the portal as a ticket's totals and as an internal note, and as
/// nothing that could be counted for a person or a day: the portal's time entries were only the
/// ones logged from the portal. An hour worked and written down in the PSA was not recorded work
/// here.
///
/// What is kept for each: that it came from the PSA (<see cref="TimeEntrySource.Provider"/>), the
/// connection, the PSA's own id for it, the login it is filed under and that login's name, the
/// date and the hours as the PSA holds them, whether it is billable, the PSA's word for the kind of
/// work, and what was written on it. Nothing is rounded, moved to another day or re-attributed.
///
/// One PSA entry is one worklog however often it is read. An entry is known by the PSA's id for it
/// on its connection: a second read finds the first, a unique index refuses a second row should
/// two reads ever race, and an entry that was logged FROM the portal and is now read back is the
/// portal's own row and is left exactly as it is, not copied.
///
/// A worklog read from the PSA is never sent to it. It is stored as already in step, nothing looks
/// for it to send, and <see cref="TicketTimeWriter.PushAsync"/> refuses one outright.
///
/// Whose it is, is not stored. It is filed under a PSA login, and it is the portal user's that
/// login is linked to for as long as the link stands: linking a login later credits its past
/// time, and unlinking takes it away, without touching a row.
/// </summary>
public static class ProviderWorklogs
{
    public sealed record Outcome(int Added, int Updated, int Removed)
    {
        public bool Changed => Added + Updated + Removed > 0;
    }

    /// <summary>
    /// Brings one ticket's worklogs into step with what its PSA holds. <paramref name="entries"/>
    /// is everything the PSA has for the ticket, read to the end: an entry of the PSA's that is no
    /// longer in it has been deleted there and is removed here. Call it only with a read that
    /// succeeded. Nothing is saved; the caller saves with the rest of what it changed.
    /// </summary>
    public static async Task<Outcome> ReconcileAsync(
        DeskDbContext db, Ticket ticket, IReadOnlyList<UnifiedTimeEntry> entries, CancellationToken ct = default)
    {
        if (ticket.PsaConnectionId is not { } connectionId) return new Outcome(0, 0, 0);

        var held = await db.TicketTimeEntries.Where(e => e.TicketId == ticket.Id).ToListAsync(ct);
        var known = new Dictionary<string, TicketTimeEntry>(StringComparer.Ordinal);
        foreach (var row in held.Where(e => !string.IsNullOrEmpty(e.ExternalEntryId)))
            // Logged here and pushed out comes first: where both somehow exist, the PSA's entry is the portal's own.
            if (!known.TryGetValue(row.ExternalEntryId!, out var first) || (first.Source != TimeEntrySource.Portal && row.Source == TimeEntrySource.Portal))
                known[row.ExternalEntryId!] = row;

        var (added, updated) = (0, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.ExternalId) || !seen.Add(e.ExternalId)) continue;

            if (known.TryGetValue(e.ExternalId, out var row))
            {
                // The portal's own entry, read back. It is one worklog already: who logged it, the
                // reply it was logged with and the clock it came from are the portal's record.
                if (row.Source == TimeEntrySource.Portal) continue;
                if (Apply(row, e)) updated++;
                continue;
            }

            var worklog = new TicketTimeEntry
            {
                MspOrganizationId = ticket.MspOrganizationId, TicketId = ticket.Id, PsaConnectionId = connectionId,
                ExternalEntryId = Limit(e.ExternalId, 100),
                Source = TimeEntrySource.Provider,
                // The PSA holds it: there is nothing to send.
                SyncStatus = TimeEntrySyncStatus.Synced,
                // Nobody here logged it. Whose it is follows the login, through its link.
                AppUserId = null,
            };
            Apply(worklog, e);
            db.TicketTimeEntries.Add(worklog);
            added++;
        }

        // Deleted in the PSA since it was read. Only the PSA's own: a portal entry the PSA no longer
        // has is the portal's to account for, as it was before.
        var gone = held.Where(r => r.Source == TimeEntrySource.Provider && (string.IsNullOrEmpty(r.ExternalEntryId) || !seen.Contains(r.ExternalEntryId!))).ToList();
        db.TicketTimeEntries.RemoveRange(gone);
        return new Outcome(added, updated, gone.Count);
    }

    /// <summary>Writes what the PSA holds onto a worklog of the PSA's. True where anything was different.</summary>
    private static bool Apply(TicketTimeEntry row, UnifiedTimeEntry e)
    {
        var notes = TimeEntryNarrative.Compose(e.Notes, e.InternalNotes);
        var login = string.IsNullOrWhiteSpace(e.TechnicianExternalId) ? null : e.TechnicianExternalId.Trim();
        var name = Limit(string.IsNullOrWhiteSpace(e.TechnicianName) ? null : e.TechnicianName.Trim(), 200);
        var workType = Limit(string.IsNullOrWhiteSpace(e.WorkType) ? null : e.WorkType.Trim(), 200);
        var changed = row.Hours != e.Hours || row.Billable != e.Billable || row.EntryDate != e.EntryDate
            || row.Notes != notes || row.TechnicianExternalId != login || row.TechnicianName != name || row.WorkTypeLabel != workType;
        (row.Hours, row.Billable, row.EntryDate, row.Notes) = (e.Hours, e.Billable, e.EntryDate, notes);
        (row.TechnicianExternalId, row.TechnicianName, row.WorkTypeLabel) = (login, name, workType);
        return changed;
    }

    private static string? Limit(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
