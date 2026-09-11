using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.PsaCore.Contracts;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Orchestrates a full inbound sync for one connection: resolve its connector, page tickets
/// (incremental from the last successful sync cursor), translate + upsert each via the sync engine,
/// then stamp the connection's health and cursor. On failure the connection is marked Degraded with
/// the error recorded, and the exception is rethrown for the caller to surface.
/// </summary>
public sealed class ConnectionSyncRunner(
    DeskDbContext db,
    IConnectorResolver resolver,
    ITicketSyncService sync,
    IObjectStorage storage,
    IMalwareScanner scanner,
    TimeProvider clock,
    Microsoft.Extensions.Logging.ILogger<ConnectionSyncRunner>? logger = null) : IConnectionSyncRunner
{
    // Safety cap so a runaway cursor cannot loop forever. It is also, at PageSize 100, a ceiling of
    // 5,000 tickets a run — so reaching it is reported rather than treated as a normal finish. An
    // unreported cap is the same silent truncation that let the import stop at 100 for months.
    private const int MaxPages = 50;

    public async Task<SyncRunResult> RunAsync(Guid psaConnectionId, bool full = false, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        // Two-way sync off means nothing flows back from the provider: portal → PSA writes still
        // happen, but an inbound run must not touch the projection.
        if (!connection.TwoWaySync)
            return new SyncRunResult(0, 0, 0, 0, 0);

        int fetched = 0, created = 0, updated = 0, skipped = 0, pages = 0, notes = 0, notesRemoved = 0, files = 0, filesRemoved = 0;
        // External ids seen this run, for providers whose attachments can only be read per ticket.
        var touched = new List<string>();
        string? cursor = null;
        try
        {
            // Resolving the connector (which reads and decrypts stored credentials) is inside this
            // try, not before it: a failure here is exactly as much a sync failure as one mid-page,
            // and connections that can never even resolve their connector — e.g. credentials the
            // secret store lost — must still be marked Degraded with a reason, not left showing
            // stale "Healthy" status forever because the code that records failure never ran.
            var connector = await resolver.ResolveAsync(psaConnectionId, ct);
            // Asked once per run, not per ticket: it decides whether time aggregates are worth pulling.
            var capabilities = await connector.GetCapabilitiesAsync(ct);
            var rules = await db.FieldMappings.AsNoTracking()
                .Where(m => m.Provider == connection.Provider && m.IsActive)
                .ToListAsync(ct);

            do
            {
                var page = await connector.GetTicketsAsync(
                    new TicketFilter
                    {
                        ModifiedSince = full ? null : connection.LastSuccessfulSyncAt,
                        PageSize = 100,
                        Cursor = cursor,
                        CompanyIds = Csv(connection.FilterCompanyIds),
                        QueueOrBoardIds = Csv(connection.FilterQueueIds),
                        AssignedResourceIds = Csv(connection.FilterResourceIds),
                        IncludeClosed = connection.ImportClosedTickets,
                        ActiveWithinDays = connection.FilterActiveWithinDays,
                    }, ct);
                pages++;
                foreach (var ticket in page.Items)
                {
                    // Client-side guard: providers express filters differently (and some not at all),
                    // so re-apply them here to keep behaviour identical across connectors.
                    if (!Passes(connection, ticket)) { skipped++; continue; }
                    // Brand-new tickets are only created when auto-import is on; existing ones still update.
                    if (!connection.AutoImportNewTickets && !await KnownAsync(psaConnectionId, ticket.ExternalId, ct))
                    { skipped++; continue; }
                    fetched++;
                    touched.Add(ticket.ExternalId);
                    switch (await sync.UpsertFromProviderAsync(psaConnectionId, ticket, rules, ct))
                    {
                        case TicketSyncOutcome.Created: created++; break;
                        case TicketSyncOutcome.Updated: updated++; break;
                        default: skipped++; break;
                    }

                    if (connection.ImportNotes)
                    {
                        var (addedNotes, removedNotes) = await ImportNotesAsync(connection, connector, ticket.ExternalId, ct);
                        notes += addedNotes;
                        notesRemoved += removedNotes;
                    }
                    await ResolveAssigneeNameAsync(psaConnectionId, connector, ticket.ExternalId, ct);

                    // Time logged provider-side never reaches the portal's stored totals otherwise:
                    // they were only rewritten when time was logged from here, so a technician's own
                    // entry left the dashboards under-reporting.
                    //
                    // Keyed off the ticket being fetched at all, NOT off the upsert outcome: adding a
                    // time entry bumps the provider's activity date (so an incremental page returns
                    // the ticket) but changes none of the fields in the update hash, so the upsert
                    // reports "unchanged" and a stricter guard here skipped every refresh.
                    if (capabilities.SupportsTimeEntries)
                        await RefreshTimeTotalsAsync(psaConnectionId, connector, ticket.ExternalId, ct);
                }
                cursor = page.NextCursor;
                if (!page.HasMore) { cursor = null; break; }
            } while (cursor is not null && pages < MaxPages);

            // Still more to read, but out of pages. The import is incomplete, and saying so is the
            // difference between a known limit and data quietly missing.
            if (cursor is not null && logger is not null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,
                    "Sync of connection {ConnectionId} stopped at the {MaxPages}-page safety cap with more "
                    + "tickets still to read; this run is incomplete",
                    psaConnectionId, MaxPages);

            // Attachments are swept separately, and deliberately outside the ticket loop: providers
            // do not reliably touch a ticket's modified timestamp when a file is attached, so an
            // incremental ticket page would miss them entirely. One dated query covers the tenant.
            //
            // Providers that cannot answer that query — ConnectWise indexes documents per record —
            // fall back to reading the tickets this run actually touched. That misses files added to
            // a quiet ticket, which is why the sweep is preferred wherever it exists.
            if (connection.SyncAttachments && capabilities.SupportsAttachmentDownload)
                (files, filesRemoved) = capabilities.SupportsAttachmentSweep
                    ? await ImportAttachmentsAsync(connection, connector, full ? null : connection.LastSuccessfulSyncAt, ct)
                    : await ImportAttachmentsPerTicketAsync(connection, connector, touched, ct);

            connection.LastSuccessfulSyncAt = clock.GetUtcNow();
            connection.LastHealthCheckAt = clock.GetUtcNow();
            connection.Status = ConnectionStatus.Healthy;
            connection.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            connection.Status = ConnectionStatus.Degraded;
            connection.LastError = ex.Message;
            connection.LastHealthCheckAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            throw;
        }

        return new SyncRunResult(fetched, created, updated, skipped, pages, notes, files, filesRemoved, notesRemoved);
    }

    public async Task<int> RefreshNotesAsync(Guid psaConnectionId, IReadOnlyCollection<string> externalTicketIds, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        // The same gates a sync applies: nothing is read back when inbound sync or notes are off.
        if (!connection.TwoWaySync || !connection.ImportNotes || externalTicketIds.Count == 0) return 0;

        var connector = await resolver.ResolveAsync(psaConnectionId, ct);
        var read = 0;
        foreach (var externalId in externalTicketIds)
        {
            // Through the sync's own note import, so a refreshed thread is indistinguishable from a
            // synced one - no second copy of the heal and dedup rules to drift from the first.
            await ImportNotesAsync(connection, connector, externalId, ct);
            read++;
        }
        return read;
    }

    public async Task<(int Added, int Removed)> RefreshAttachmentsAsync(Guid psaConnectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        if (!connection.TwoWaySync || !connection.SyncAttachments) return (0, 0);

        var connector = await resolver.ResolveAsync(psaConnectionId, ct);
        var capabilities = await connector.GetCapabilitiesAsync(ct);
        // Only a provider with a tenant-wide sweep: one undated read covers every file it holds, with
        // no import window in the way. The per-ticket providers report no author id to fill.
        if (!capabilities.SupportsAttachmentDownload || !capabilities.SupportsAttachmentSweep) return (0, 0);

        return await ImportAttachmentsAsync(connection, connector, since: null, ct);
    }

    /// <summary>
    /// Mirrors the provider's notes into the portal thread — internal ones included, carrying
    /// IsPublic=false. Deduplication is by the provider's own note id, which doubles as echo
    /// suppression: a reply written in the portal already stored that id when the provider accepted
    /// it, so it is recognised rather than duplicated.
    /// Storing an internal note is safe because visibility is enforced at READ time: the client
    /// ticket paths filter to IsPublic, so a private note reaches staff screens only. Filtering at
    /// sync instead (the old behaviour) hid half the thread from technicians.
    /// </summary>
    private async Task<(int Added, int Removed)> ImportNotesAsync(PsaConnection connection, IServiceManagementConnector connector, string externalTicketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(
            t => t.PsaConnectionId == connection.Id && t.ExternalTicketId == externalTicketId, ct);
        if (ticket is null) return (0, 0);

        IReadOnlyList<UnifiedTicketNote> incoming;
        // A read that throws leaves an UNKNOWN list, not an empty one — returning here also means
        // nothing is reconciled, so a rate-limited ticket never loses its thread.
        try { incoming = await connector.GetNotesAsync(externalTicketId, ct); }
        catch (ConnectorException) { return (0, 0); } // one ticket's notes must not fail the whole run

        // TIME-ENTRY notes. Both PSAs show a time entry's notes in the ticket's own note stream —
        // ConnectWise's "All notes" view is ticket notes PLUS time-entry notes — but the ticket-notes
        // API returns only the former, which is how a technician's note written through a time entry
        // never reached the portal. Imported as INTERNAL: the provider's own UI treats them that way,
        // and a time note can carry candid detail no client should see. The te- id prefix keeps them
        // from ever colliding with real note ids.
        var timeNotesFetched = false;
        // A failed read leaves this false, so previously imported time notes are shielded from
        // reconciliation below rather than mistaken for deletions.
        if ((await connector.GetCapabilitiesAsync(ct)).SupportsTimeEntries
            && await TimeEntriesAsync(connector, externalTicketId, ct) is { } entries)
        {
            {
                // A time entry logged FROM the portal carries the reply that logged it — that text is
                // already in the thread as the reply itself, so importing it back would double every
                // portal reply that logged time.
                var portalOrigin = (await db.TicketTimeEntries
                        .Where(t => t.TicketId == ticket.Id && t.Source == TimeEntrySource.Portal && t.ExternalEntryId != null)
                        .Select(t => t.ExternalEntryId!)
                        .ToListAsync(ct))
                    .ToHashSet();

                var merged = new List<UnifiedTicketNote>(incoming);
                foreach (var e in entries)
                {
                    if (string.IsNullOrEmpty(e.ExternalId)) continue;
                    if (portalOrigin.Contains(e.ExternalId)) continue;
                    // Both halves as one body — the provider splits them, the reader does not care
                    // which field the text was filed in. An entry with ONLY internal notes still
                    // counts; requiring a summary is what made those vanish.
                    var body = TimeEntryNarrative.Compose(e.Notes, e.InternalNotes);
                    if (string.IsNullOrWhiteSpace(body)) continue;
                    merged.Add(new UnifiedTicketNote(
                        $"te-{e.ExternalId}", e.TechnicianName ?? "", body, IsPublic: false, e.EntryDate,
                        // A time entry's author is the resource it is filed under.
                        AuthorExternalId: string.IsNullOrWhiteSpace(e.TechnicianExternalId) ? null : e.TechnicianExternalId));
                }
                incoming = merged;
                timeNotesFetched = true;
            }
        }

        var existing = await db.TicketNotes
            .Where(n => n.TicketId == ticket.Id && n.ExternalNoteId != null)
            .ToListAsync(ct);
        var known = existing.Select(n => n.ExternalNoteId!).ToHashSet();

        // Heal the side of notes imported before FromClient existed (they were ALL stored as
        // staff-authored) — and any later PSA-side correction. Provider-imported rows only:
        // a portal reply's byline is the portal's own record, never the provider's to rewrite.
        var healed = 0;
        foreach (var n in incoming)
        {
            if (string.IsNullOrEmpty(n.ExternalId)) continue;
            var row = existing.FirstOrDefault(e => e.ExternalNoteId == n.ExternalId && e.ImportedFromProvider);
            if (row is null) continue;
            if (row.AuthoredByClient != n.FromClient)
            {
                row.AuthoredByClient = n.FromClient;
                healed++;
            }
            // The body too. The insert loop below skips IDs it already holds, so a note imported
            // by an earlier, narrower reader keeps that reading forever — a time entry stored as
            // "See Internal Notes" stays a pointer to nothing even after the import learned to
            // fetch the internal half. The provider owns the text of rows it authored; a portal
            // reply is never touched here because those are not ImportedFromProvider.
            if (row.Body != n.Body)
            {
                row.Body = n.Body;
                healed++;
            }
            // And who wrote it, as an id. A note imported before the id was kept gets it here, the
            // next time its ticket is read - so the existing thread backfills from the provider's own
            // record rather than from a guess on names.
            if (row.AuthorExternalId != n.AuthorExternalId)
            {
                row.AuthorExternalId = n.AuthorExternalId;
                healed++;
            }
        }

        var added = 0;
        foreach (var n in incoming)
        {
            if (string.IsNullOrEmpty(n.ExternalId) || known.Contains(n.ExternalId)) continue;
            // Machine-generated notes have no human author; skip unless explicitly wanted.
            if (!connection.ImportSystemNotes && string.IsNullOrWhiteSpace(n.AuthorName)) continue;

            db.TicketNotes.Add(new TicketNote
            {
                MspOrganizationId = ticket.MspOrganizationId,
                TicketId = ticket.Id,
                ExternalNoteId = n.ExternalId,
                // An empty author means the provider generated the note itself (workflow/SLA); name it
                // after the provider rather than leaving a blank byline in the thread.
                AuthorName = string.IsNullOrWhiteSpace(n.AuthorName) ? $"{connection.Provider} automation" : n.AuthorName,
                AuthorExternalId = n.AuthorExternalId,
                // The provider's word on which SIDE wrote it — a customer contact's note must land
                // on the client side of the thread, not read as the MSP's own words.
                AuthoredByClient = n.FromClient,
                ImportedFromProvider = true,
                Body = n.Body,
                IsPublic = n.IsPublic,
                NoteCreatedAt = n.CreatedAt,
            });
            known.Add(n.ExternalId);
            added++;
        }

        var removed = await ReconcileDeletedNotesAsync(ticket.Id, incoming, timeNotesFetched, ct);
        if (added > 0 || removed > 0 || healed > 0) await db.SaveChangesAsync(ct);
        return (added, removed);
    }

    /// <summary>
    /// Drops imported notes the provider no longer returns. Unlike attachments there is no dated
    /// sweep to get wrong: notes are always read one ticket at a time, so a successful read is the
    /// complete public thread for that ticket and anything missing from it has been deleted.
    ///
    /// Replies written in the portal are never removed. They carry a provider note id from being
    /// pushed out, but the portal is where they originated — erasing a customer's own message
    /// because a technician deleted the PSA's copy would destroy the only record of it.
    /// The comparison uses every note the provider returned, not the filtered subset, so a note
    /// skipped by the system-note setting is never mistaken for a deleted one.
    /// </summary>
    private async Task<int> ReconcileDeletedNotesAsync(
        Guid ticketId, IReadOnlyList<UnifiedTicketNote> incoming, bool timeNotesFetched, CancellationToken ct)
    {
        var stillPresent = incoming
            .Select(n => n.ExternalId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();

        var orphans = await db.TicketNotes
            .Where(n => n.TicketId == ticketId
                        && n.ExternalNoteId != null
                        // Provider-origin only. AuthoredByClient used to stand in for this, which
                        // held exactly as long as no imported note was ever client-authored — now
                        // that customer-contact notes import with their real side, origin needs its
                        // own flag or every one of them would be shielded from deletion forever.
                        && n.ImportedFromProvider
                        // When the time-entry read failed, its notes are missing from `incoming` for
                        // that reason alone — absence there is not evidence of deletion.
                        && (timeNotesFetched || !n.ExternalNoteId.StartsWith("te-"))
                        && !stillPresent.Contains(n.ExternalNoteId))
            .ToListAsync(ct);
        if (orphans.Count == 0) return 0;

        db.TicketNotes.RemoveRange(orphans);
        return orphans.Count;
    }

    /// <summary>
    /// Drops imported files the provider no longer has. Only rows that CAME from the provider are
    /// touched: a portal upload is the customer's own copy and the portal is its origin, so removing
    /// it because a technician deleted the PSA's copy would destroy data nothing else holds.
    /// </summary>
    private async Task<int> ReconcileDeletionsAsync(IReadOnlyList<Guid> ticketIds, HashSet<string> stillPresent, CancellationToken ct)
    {
        if (ticketIds.Count == 0) return 0;

        var orphans = await db.TicketAttachments
            .Where(a => ticketIds.Contains(a.TicketId)
                        && a.ImportedFromProvider
                        && a.ExternalAttachmentId != null
                        && !stillPresent.Contains(a.ExternalAttachmentId))
            .ToListAsync(ct);
        if (orphans.Count == 0) return 0;

        foreach (var orphan in orphans)
        {
            // Drop the bytes as well as the row: leaving them would keep a withdrawn document
            // retrievable by anyone who kept a signed URL.
            if (!string.IsNullOrEmpty(orphan.StorageObjectKey))
            {
                try { await storage.DeleteAsync(orphan.StorageObjectKey, ct); }
                catch (Exception) { /* the row still goes, so the file stops being reachable */ }
            }
            db.TicketAttachments.Remove(orphan);
        }
        return orphans.Count;
    }

    // Resolved once per run and reused: the provider's resource list does not change mid-sync, and
    // a per-ticket lookup would cost a request for every row.
    private Dictionary<string, string>? _technicianNames;

    /// <summary>
    /// Puts a readable name against the provider's assignee id, so the ticket can say who is working
    /// on it rather than showing a bare numeric resource id.
    /// </summary>
    private async Task ResolveAssigneeNameAsync(Guid connectionId, IServiceManagementConnector connector, string externalTicketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(
            t => t.PsaConnectionId == connectionId && t.ExternalTicketId == externalTicketId, ct);
        if (ticket?.AssignedTechnicianExternalId is not { Length: > 0 } assignee)
        {
            if (ticket is not null && ticket.AssignedTechnicianName is not null)
            {
                ticket.AssignedTechnicianName = null; // unassigned provider-side: drop the stale name
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        if (_technicianNames is null)
        {
            _technicianNames = [];
            try
            {
                foreach (var t in await connector.GetTechniciansAsync(ct))
                    _technicianNames[t.ExternalId] = t.DisplayName;
            }
            catch (ConnectorException) { /* the id still shows; the name is the nicety */ }
        }

        var name = _technicianNames.GetValueOrDefault(assignee);
        if (name == ticket.AssignedTechnicianName) return;
        ticket.AssignedTechnicianName = name;
        await db.SaveChangesAsync(ct);
    }

    // One ticket's time entries, read once for this run. Two things want them - the time-entry
    // notes and the worked/billable totals - and asking twice made the read the single largest
    // call volume in a full sync: 270 requests for 135 tickets, each one identical to the one
    // beside it.
    //
    // Null means the read FAILED, which is not the same as a ticket having no time. The callers
    // depend on that difference: notes shield previously imported time notes from reconciliation
    // rather than deleting them, and the totals are left alone rather than rewritten to zero.
    //
    // Cached per runner instance, and a runner is built per run inside its own scope, so this is
    // one run's snapshot - never a stale one carried into the next.
    private readonly Dictionary<string, IReadOnlyList<UnifiedTimeEntry>?> _timeEntriesThisRun = [];

    private async Task<IReadOnlyList<UnifiedTimeEntry>?> TimeEntriesAsync(
        IServiceManagementConnector connector, string externalTicketId, CancellationToken ct)
    {
        if (_timeEntriesThisRun.TryGetValue(externalTicketId, out var cached)) return cached;

        IReadOnlyList<UnifiedTimeEntry>? entries;
        try { entries = await connector.GetTimeEntriesAsync(externalTicketId, ct); }
        catch (ConnectorException) { entries = null; }

        _timeEntriesThisRun[externalTicketId] = entries;
        return entries;
    }

    /// <summary>Rewrites one ticket's worked/billable totals from the PSA, which owns the truth.</summary>
    private async Task RefreshTimeTotalsAsync(Guid connectionId, IServiceManagementConnector connector, string externalTicketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(
            t => t.PsaConnectionId == connectionId && t.ExternalTicketId == externalTicketId, ct);
        if (ticket is null) return;

        // Null is a failed read, not an empty ticket: returning here leaves the stored totals
        // alone, where rewriting them would zero a ticket's hours because one request failed.
        if (await TimeEntriesAsync(connector, externalTicketId, ct) is not { } entries) return;

        ticket.TimeWorkedHours = entries.Sum(e => e.Hours);
        ticket.BillableHours = entries.Where(e => e.Billable).Sum(e => e.Hours);
        ticket.NonBillableHours = entries.Where(e => !e.Billable).Sum(e => e.Hours);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Per-ticket attachment import, for providers with no dated tenant-wide query. Reuses the same
    /// dedup, scan and storage path as the sweep by handing it the rows it would have produced.
    /// </summary>
    private async Task<(int Added, int Removed)> ImportAttachmentsPerTicketAsync(PsaConnection connection, IServiceManagementConnector connector, IReadOnlyList<string> externalTicketIds, CancellationToken ct)
    {
        var refs = new List<ProviderAttachmentRef>();
        // Only tickets that were actually read successfully may be reconciled: a ticket whose read
        // threw has an unknown file list, and treating that as "no files" would delete every one.
        var complete = new List<string>();
        foreach (var externalTicketId in externalTicketIds.Distinct())
        {
            try
            {
                foreach (var file in await connector.GetAttachmentsAsync(externalTicketId, ct))
                    refs.Add(new ProviderAttachmentRef(externalTicketId, file));
                complete.Add(externalTicketId);
            }
            catch (ConnectorException) { /* one ticket's files must not fail the run */ }
        }
        if (complete.Count == 0) return (0, 0);
        return await StoreAttachmentsAsync(connection, connector, refs, complete, ct);
    }

    /// <summary>
    /// Mirrors the provider's attachments into the portal, bytes included. Deduplication is by the
    /// provider's own attachment id, which — exactly as with notes — also suppresses the echo of a
    /// portal upload that was already pushed out and recorded with that id.
    ///
    /// Imported bytes are scanned before they are stored, on the same footing as a customer upload:
    /// a PSA is not a trusted source, and a technician can attach anything to a ticket.
    /// </summary>
    private async Task<(int Added, int Removed)> ImportAttachmentsAsync(PsaConnection connection, IServiceManagementConnector connector, DateTimeOffset? since, CancellationToken ct)
    {
        IReadOnlyList<ProviderAttachmentRef> incoming;
        try { incoming = await connector.GetRecentAttachmentsAsync(since, ct); }
        catch (ConnectorException) { return (0, 0); } // files must not fail the whole run

        // A DATED sweep returns only recent files, so a file's absence from it says nothing about
        // whether it still exists — reconciling against that would delete the entire back catalogue.
        // Only a full sweep sees everything, and only then can deletions be inferred.
        IReadOnlyList<string>? reconcilable = since is null
            ? await db.Tickets
                .Where(t => t.PsaConnectionId == connection.Id && t.ExternalTicketId != null)
                .Select(t => t.ExternalTicketId!)
                .ToListAsync(ct)
            : null;

        if (incoming.Count == 0 && reconcilable is null) return (0, 0);
        return await StoreAttachmentsAsync(connection, connector, incoming, reconcilable, ct);
    }

    /// <summary>
    /// Dedups, downloads, scans and stores a set of provider attachments, then reconciles deletions.
    ///
    /// <paramref name="reconcilableTicketIds"/> names the tickets whose incoming list is COMPLETE.
    /// For those, an imported file the provider no longer reports has been deleted there and is
    /// removed here too — otherwise a document withdrawn in the PSA stays downloadable by the
    /// customer indefinitely. Null means nothing may be reconciled.
    /// </summary>
    private async Task<(int Added, int Removed)> StoreAttachmentsAsync(
        PsaConnection connection,
        IServiceManagementConnector connector,
        IReadOnlyList<ProviderAttachmentRef> incoming,
        IReadOnlyList<string>? reconcilableTicketIds,
        CancellationToken ct)
    {
        // Only tickets this connection already projects. A file on a ticket we do not import is not
        // ours to store, and the sweep is deliberately tenant-wide.
        var wanted = incoming.Select(r => r.TicketExternalId)
            .Concat(reconcilableTicketIds ?? [])
            .Distinct().ToList();
        var tickets = await db.Tickets
            .Where(t => t.PsaConnectionId == connection.Id && t.ExternalTicketId != null && wanted.Contains(t.ExternalTicketId))
            .Select(t => new { t.Id, t.ExternalTicketId, t.MspOrganizationId })
            .ToListAsync(ct);
        if (tickets.Count == 0) return (0, 0);
        var byExternalId = tickets.ToDictionary(t => t.ExternalTicketId!, t => t);

        var ticketIds = tickets.Select(t => t.Id).ToList();
        // Provider note id -> portal note, so an imported file lands under the reply it belongs to
        // instead of in an undifferentiated pile at the bottom of the ticket.
        var noteIdByExternalId = await db.TicketNotes
            .Where(n => ticketIds.Contains(n.TicketId) && n.ExternalNoteId != null)
            .ToDictionaryAsync(n => n.ExternalNoteId!, n => n.Id, ct);

        var existing = await db.TicketAttachments
            .Where(a => ticketIds.Contains(a.TicketId) && a.ExternalAttachmentId != null)
            .ToListAsync(ct);
        var known = existing.Select(a => a.ExternalAttachmentId!).ToHashSet();

        // Heal who attached files already held, as notes do: a row imported before the author id was
        // kept gets it the next time the provider reports the file. Provider-imported rows only - a
        // portal upload is the portal's own record, whatever the provider stamped on its copy.
        var importedById = existing
            .Where(a => a.ImportedFromProvider)
            .GroupBy(a => a.ExternalAttachmentId!)
            .ToDictionary(g => g.Key, g => g.First());
        var healed = 0;
        foreach (var file in incoming.Select(r => r.Attachment))
        {
            if (string.IsNullOrEmpty(file.ExternalId) || !importedById.TryGetValue(file.ExternalId, out var row)) continue;
            if (row.AuthorExternalId == file.AuthorExternalId) continue;
            row.AuthorExternalId = file.AuthorExternalId;
            healed++;
        }

        var added = 0;
        foreach (var (externalTicketId, file) in incoming.Select(r => (r.TicketExternalId, r.Attachment)))
        {
            if (string.IsNullOrEmpty(file.ExternalId) || known.Contains(file.ExternalId)) continue;
            if (!byExternalId.TryGetValue(externalTicketId, out var ticket)) continue;

            DownloadedAttachment? payload;
            try { payload = await connector.DownloadAttachmentAsync(externalTicketId, file.ExternalId, ct); }
            catch (ConnectorException) { continue; }
            // No bytes means the provider cannot serve this file. Recording metadata alone would put
            // an undownloadable row in the customer's list, so skip it and retry on the next run.
            if (payload is null || payload.Content.Length == 0) continue;

            var scan = await scanner.ScanAsync(payload.Content, payload.FileName, ct);
            var record = new TicketAttachment
            {
                MspOrganizationId = ticket.MspOrganizationId,
                TicketId = ticket.Id,
                ExternalAttachmentId = file.ExternalId,
                TicketNoteId = file.ExternalNoteId is { } n && noteIdByExternalId.TryGetValue(n, out var localNote)
                    ? localNote
                    : null,
                OriginalFileName = payload.FileName,
                ContentType = payload.ContentType,
                SizeBytes = payload.Content.LongLength,
                StorageObjectKey = string.Empty,
                UploadedAt = file.CreatedAt ?? clock.GetUtcNow(),
                AuthorName = string.IsNullOrWhiteSpace(file.AuthorName) ? $"{connection.Provider} automation" : file.AuthorName,
                AuthorExternalId = file.AuthorExternalId,
                ImportedFromProvider = true,
            };

            if (scan.IsClean)
            {
                var key = $"att/{ticket.Id}/{Guid.NewGuid():N}{Path.GetExtension(payload.FileName)}";
                await storage.PutAsync(key, payload.Content, payload.ContentType, ct);
                record.StorageObjectKey = key;
                record.ScanStatus = AttachmentScanStatus.Clean;
            }
            else
            {
                record.ScanStatus = AttachmentScanStatus.Quarantined;
                record.ScanDetail = scan.Detail;
            }

            db.TicketAttachments.Add(record);
            known.Add(file.ExternalId);
            added++;
        }

        var reconcilable = tickets
            .Where(t => reconcilableTicketIds?.Contains(t.ExternalTicketId!) == true)
            .Select(t => t.Id)
            .ToList();
        var stillPresent = incoming
            .Select(r => r.Attachment.ExternalId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();
        var removed = await ReconcileDeletionsAsync(reconcilable, stillPresent!, ct);

        if (added > 0 || removed > 0 || healed > 0) await db.SaveChangesAsync(ct);
        return (added, removed);
    }

    private static IReadOnlyList<string> Csv(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Re-applies the connection's import filters to a fetched ticket (provider-agnostic).</summary>
    private static bool Passes(PsaConnection c, UnifiedTicket t)
    {
        var closed = t.ClosedAt is not null || t.ResolvedAt is not null;
        if (closed && !c.ImportClosedTickets) return false;
        if (!closed && !c.ImportOpenTickets) return false;

        var queues = Csv(c.FilterQueueIds);
        if (queues.Count > 0 && !queues.Contains(t.QueueOrBoard ?? "", StringComparer.OrdinalIgnoreCase)) return false;

        var resources = Csv(c.FilterResourceIds);
        if (resources.Count > 0 && !resources.Contains(t.AssignedTechnicianExternalId ?? "", StringComparer.OrdinalIgnoreCase)) return false;

        var companies = Csv(c.FilterCompanyIds);
        if (companies.Count > 0 && !companies.Contains(t.RequesterExternalId ?? "", StringComparer.OrdinalIgnoreCase)) return false;

        if (c.FilterActiveWithinDays is > 0 and { } days)
        {
            var last = t.ModifiedAt ?? t.CreatedAt;
            if (last is not null && last < DateTimeOffset.UtcNow.AddDays(-days)) return false;
        }
        return true;
    }

    private Task<bool> KnownAsync(Guid connectionId, string externalId, CancellationToken ct)
        => db.Tickets.AnyAsync(t => t.PsaConnectionId == connectionId && t.ExternalTicketId == externalId, ct);
}
