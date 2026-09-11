using System.Linq.Expressions;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Notes and attachments imported before the author's id was kept - the work lists for filling it in
/// from the provider's own record.
///
/// Only rows that SHOULD have an id: provider-imported (a portal reply or upload is the portal's own
/// record), not a customer contact's, and not the sync's own byline for something the provider
/// generated ("AutotaskPsa automation"), which has no author to find.
/// </summary>
public static class AuthorBackfill
{
    private static readonly Expression<Func<TicketNote, bool>> NoteMissingAuthorId = n =>
        n.ImportedFromProvider && !n.AuthoredByClient && n.AuthorExternalId == null
        && !n.AuthorName.EndsWith(" automation");

    /// <summary>How many notes are still without an author id that should have one.</summary>
    public static Task<int> NotesCountAsync(DeskDbContext db, CancellationToken ct = default)
        => db.TicketNotes.AsNoTracking().CountAsync(NoteMissingAuthorId, ct);

    /// <summary>The held tickets carrying such notes, by connection, as the provider's ticket ids.</summary>
    public static async Task<Dictionary<Guid, List<string>>> NotesPendingAsync(DeskDbContext db, CancellationToken ct = default)
    {
        var rows = await db.TicketNotes.AsNoTracking()
            .Where(NoteMissingAuthorId)
            .Join(db.Tickets.AsNoTracking().Where(t => t.ExternalTicketId != null), n => n.TicketId, t => t.Id,
                (n, t) => new { t.PsaConnectionId, ExternalId = t.ExternalTicketId! })
            .Distinct()
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.PsaConnectionId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ExternalId).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Imported attachments without an author id that should have one, as the connection each is on.
    ///
    /// ConnectWise is left out: it names a document's owner by login identifier, not by the numeric
    /// member id its other person references carry, so its files have no id to fill - counting them
    /// would report a shortfall the next pass could never close.
    /// </summary>
    private static IQueryable<Guid> AttachmentsMissingAuthorId(DeskDbContext db) =>
        db.TicketAttachments.AsNoTracking()
            .Where(a => a.ImportedFromProvider && a.AuthorExternalId == null
                        && a.AuthorName != null && !a.AuthorName.EndsWith(" automation"))
            .Join(db.Tickets.AsNoTracking(), a => a.TicketId, t => t.Id, (a, t) => t.PsaConnectionId)
            .Join(db.PsaConnections.AsNoTracking().Where(c => c.Provider != ProviderType.ConnectWisePsa),
                connectionId => connectionId, c => c.Id, (connectionId, c) => connectionId);

    /// <summary>How many imported attachments are still without an author id that should have one.</summary>
    public static Task<int> AttachmentsCountAsync(DeskDbContext db, CancellationToken ct = default)
        => AttachmentsMissingAuthorId(db).CountAsync(ct);

    /// <summary>The connections holding such attachments. One sweep per connection covers all of its files.</summary>
    public static Task<List<Guid>> AttachmentConnectionsPendingAsync(DeskDbContext db, CancellationToken ct = default)
        => AttachmentsMissingAuthorId(db).Distinct().ToListAsync(ct);
}
