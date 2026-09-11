using System.Linq.Expressions;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Staff notes imported before the author's id was kept, and the held tickets they sit on - the work
/// list for re-reading those threads from the provider.
///
/// Only notes that SHOULD have an author id: provider-imported (a portal reply is the portal's own
/// record), staff-side (a customer contact is not a resource), and not the sync's own byline for a
/// system note ("AutotaskPsa automation"), which has no author to find.
/// </summary>
public static class NoteAuthorBackfill
{
    private static readonly Expression<Func<TicketNote, bool>> MissingAuthorId = n =>
        n.ImportedFromProvider && !n.AuthoredByClient && n.AuthorExternalId == null
        && !n.AuthorName.EndsWith(" automation");

    /// <summary>How many notes are still without an author id that should have one.</summary>
    public static Task<int> CountAsync(DeskDbContext db, CancellationToken ct = default)
        => db.TicketNotes.AsNoTracking().CountAsync(MissingAuthorId, ct);

    /// <summary>The held tickets carrying such notes, by connection, as the provider's ticket ids.</summary>
    public static async Task<Dictionary<Guid, List<string>>> PendingAsync(DeskDbContext db, CancellationToken ct = default)
    {
        var rows = await db.TicketNotes.AsNoTracking()
            .Where(MissingAuthorId)
            .Join(db.Tickets.AsNoTracking().Where(t => t.ExternalTicketId != null), n => n.TicketId, t => t.Id,
                (n, t) => new { t.PsaConnectionId, ExternalId = t.ExternalTicketId! })
            .Distinct()
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.PsaConnectionId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ExternalId).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }
}
