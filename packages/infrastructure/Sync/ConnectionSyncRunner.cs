using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.PsaCore.Contracts;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Runs one connection's inbound sync: takes the connection's lock, reads the tickets changed since
/// its cursor a page at a time, applies each, and records what happened.
///
/// Four rules it keeps, each of which it used to break:
/// <list type="bullet">
/// <item>One run per connection at a time (<see cref="SyncRunCoordinator"/>).</item>
/// <item>The cursor moves to when the read STARTED, and only once the whole read has finished. A
/// read that runs out of pages is continued by the next run from where it stopped.</item>
/// <item>A record that cannot be read or applied costs that record, not the run: it is kept in
/// <see cref="SyncFailure"/> and tried again.</item>
/// <item>A failure is never silent. What was not read is on record until it has been.</item>
/// </list>
/// If the run itself fails the connection is marked Degraded with the reason and the exception is
/// rethrown for the caller to surface.
/// </summary>
public sealed class ConnectionSyncRunner(
    DeskDbContext db,
    IConnectorResolver resolver,
    ITicketSyncService sync,
    IObjectStorage storage,
    IMalwareScanner scanner,
    TimeProvider clock,
    Microsoft.Extensions.Logging.ILogger<ConnectionSyncRunner>? logger = null,
    SyncOptions? options = null) : IConnectionSyncRunner
{
    private readonly SyncOptions _options = options ?? SyncOptions.Default;
    private readonly SyncRunCoordinator _runs = new(db, clock, options ?? SyncOptions.Default);
    private readonly SyncFailureStore _failures = new(db, clock);

    // ---- the run in progress -------------------------------------------------------------------
    // Fields, not parameters: the helpers below record their own failures, and they are reached by
    // a dozen paths. All null or empty outside RunAsync, where the helpers then record nothing.
    private PsaConnection? _connection;
    private SyncRun? _run;
    private SyncCursor? _cursor;
    private Dictionary<(string ExternalId, string Operation), SyncFailure> _open = [];
    private readonly Dictionary<string, int> _failedInARow = [];
    private readonly HashSet<string> _switchedOff = [];
    private readonly List<string> _notices = [];
    private bool _stopEarly;
    private int _failed, _recovered;
    // Tickets with a file that could not be downloaded this run: their attachments are still owed.
    private readonly HashSet<string> _filesFailed = [];

    private sealed class Tally
    {
        public int Fetched, Created, Updated, Skipped, Pages, Notes, NotesRemoved, Files, FilesRemoved, Retried;
        public readonly List<string> Touched = [];
    }

    public Task<SyncRunResult> RunAsync(Guid psaConnectionId, bool full = false, CancellationToken ct = default)
        => RunAsync(psaConnectionId, new SyncRunRequest(full), ct);

    public async Task<SyncRunResult> RunAsync(Guid psaConnectionId, SyncRunRequest request, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        // Two-way sync off means nothing flows back from the provider: portal → PSA writes still
        // happen, but an inbound run must not touch the projection. Paused and archived say the
        // same thing for a different reason.
        if (!connection.TwoWaySync || connection.SyncPausedAt is not null || connection.ArchivedAt is not null)
            return new SyncRunResult(0, 0, 0, 0, 0);

        var trigger = request.Full ? SyncRunTrigger.ManualFull : request.Manual ? SyncRunTrigger.Manual : SyncRunTrigger.Scheduled;
        var run = await _runs.TryStartAsync(connection, trigger, request.RequestedBy, ct)
            ?? throw new ConflictException(
                "A sync is already running for this connection. It will finish by itself; there is nothing to start again.",
                new { connectionId = psaConnectionId });
        var runId = run.Id;
        (_connection, _run, _cursor) = (connection, run, null);
        (_stopEarly, _failed, _recovered) = (false, 0, 0);
        _failedInARow.Clear();
        _switchedOff.Clear();
        _notices.Clear();
        _filesFailed.Clear();
        var tally = new Tally();
        var more = false;
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
            var rules = await ConnectionMappingRules.LoadAsync(
                db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
            _open = await _failures.OpenAsync(psaConnectionId, ct);

            _cursor = await db.SyncCursors.FirstOrDefaultAsync(
                c => c.PsaConnectionId == psaConnectionId && c.Entity == SyncCursor.Tickets, ct);
            if (_cursor is null)
            {
                _cursor = new SyncCursor
                {
                    MspOrganizationId = connection.MspOrganizationId, PsaConnectionId = psaConnectionId, Entity = SyncCursor.Tickets,
                };
                db.SyncCursors.Add(_cursor);
            }

            // What this run reads.
            //  - A read that ran out of pages last time is carried on from where it stopped, with the
            //    filter it had.
            //  - A full run starts from nothing, and replaces any read that was under way.
            //  - Otherwise: everything changed since the watermark. A connection that has none yet
            //    falls back to the old single cursor, further back by an hour because that cursor was
            //    the END of a run and says nothing for what changed during it.
            var resuming = _cursor.Continuation is not null && !request.Full;
            if (!resuming)
            {
                _cursor.Continuation = null;
                _cursor.ContinuationSince = request.Full
                    ? null
                    : _cursor.Watermark ?? connection.LastSuccessfulSyncAt - _options.FirstRunOverlap;
                _cursor.ContinuationStartedAt = run.StartedAt;
                _cursor.ContinuationFull = request.Full;
                _cursor.ContinuationPages = 0;
            }
            var since = _cursor.ContinuationSince;
            var fullRead = _cursor.ContinuationFull;
            await db.SaveChangesAsync(ct);

            // Earlier failures first. They are the oldest work the connection owes.
            await RetryFailuresAsync(connector, capabilities, rules, tally, ct);

            var pagesThisRun = 0;
            var next = _cursor.Continuation;
            while (!_stopEarly)
            {
                PaginatedResult<UnifiedTicket> page;
                try
                {
                    page = await connector.GetTicketsAsync(
                        new TicketFilter
                        {
                            ModifiedSince = since,
                            PageSize = _options.PageSize,
                            Cursor = next,
                            CompanyIds = Csv(_connection!.FilterCompanyIds),
                            QueueOrBoardIds = Csv(_connection.FilterQueueIds),
                            AssignedResourceIds = Csv(_connection.FilterResourceIds),
                            IncludeClosed = _connection.ImportClosedTickets,
                            ActiveWithinDays = _connection.FilterActiveWithinDays,
                        }, ct);
                }
                catch (ConnectorException ex) when (resuming && pagesThisRun == 0 && next is not null
                    && ex.Kind is ConnectorFailureKind.InvalidRequest or ConnectorFailureKind.NotFound or ConnectorFailureKind.ProviderError)
                {
                    // The provider no longer honours the place the last run stopped: a page cursor is
                    // the provider's to expire. The read starts again from its first page, with the
                    // same filter. Nothing is lost - what was applied stays applied - only re-read.
                    Warn("Sync of connection {ConnectionId} could not resume from its saved position ({Kind}); reading again from the first page",
                        psaConnectionId, ex.Kind);
                    next = null;
                    resuming = false;
                    _cursor!.Continuation = null;
                    _cursor.ContinuationPages = 0;
                    continue;
                }
                pagesThisRun++;
                tally.Pages++;

                foreach (var ticket in page.Items)
                {
                    if (_stopEarly) break;
                    // Client-side guard: providers express filters differently (and some not at all),
                    // so re-apply them here to keep behaviour identical across connectors.
                    if (!Passes(_connection!, ticket)) { tally.Skipped++; continue; }
                    // Brand-new tickets are only created when auto-import is on; existing ones still update.
                    if (!_connection!.AutoImportNewTickets && !await KnownAsync(psaConnectionId, ticket.ExternalId, ct))
                    { tally.Skipped++; continue; }
                    await ApplyOneAsync(connector, capabilities, rules, ticket, tally, ct);
                }

                // Stopped part-way through this page: the saved position stays at its start, so the
                // next run reads the page again. Re-reading is free; skipping is not.
                if (_stopEarly) { more = true; break; }

                next = page.HasMore ? page.NextCursor : null;
                _cursor!.Continuation = next;
                _cursor.ContinuationPages++;
                Progress(tally);
                await db.SaveChangesAsync(ct);

                if (next is null) break;
                if (pagesThisRun >= _options.MaxPagesPerRun) { more = true; break; }
            }
            more |= _stopEarly;

            if (more && !_stopEarly)
                Info("Sync of connection {ConnectionId} read {Pages} pages this run and has more to read; the next run continues",
                    psaConnectionId, pagesThisRun);

            // Attachments are swept separately, and deliberately outside the ticket loop: providers
            // do not reliably touch a ticket's modified timestamp when a file is attached, so an
            // incremental ticket page would miss them entirely. One dated query covers the tenant.
            //
            // Providers that cannot answer that query — ConnectWise indexes documents per record —
            // fall back to reading the tickets this run actually touched. That misses files added to
            // a quiet ticket, which is why the sweep is preferred wherever it exists.
            //
            // The sweep waits for the ticket read to finish: it only stores files for tickets the
            // portal holds, and a full one removes files a complete list no longer contains.
            if (!_stopEarly && _connection!.SyncAttachments && capabilities.SupportsAttachmentDownload)
            {
                if (!capabilities.SupportsAttachmentSweep)
                    (tally.Files, tally.FilesRemoved) = await ImportAttachmentsPerTicketAsync(_connection, connector, tally.Touched, ct);
                else if (!more)
                    (tally.Files, tally.FilesRemoved) = await ImportAttachmentsAsync(_connection, connector, fullRead ? null : since, ct);
            }

            var now = clock.GetUtcNow();
            if (!more)
            {
                // The whole read is done, so everything changed before it STARTED has been read.
                // Not "now": a ticket changed while the last page was being read has a modified time
                // before now, and a cursor at now would never ask for it again.
                _cursor!.Watermark = (_cursor.ContinuationStartedAt ?? _run!.StartedAt) - _options.Overlap;
                _cursor.Continuation = null;
                _cursor.ContinuationSince = null;
                _cursor.ContinuationStartedAt = null;
                _cursor.ContinuationFull = false;
                _cursor.ContinuationPages = 0;
                _connection!.LastSuccessfulSyncAt = now;
            }
            _connection!.LastHealthCheckAt = now;
            _connection.Status = ConnectionStatus.Healthy;
            _connection.LastError = null;
            _connection.LastErrorKind = null;

            Progress(tally);
            _run!.Status = more ? SyncRunStatus.Partial : SyncRunStatus.Succeeded;
            _run.FinishedAt = now;
            _run.Notice = _notices.Count == 0 ? null : Shorten(string.Join(" ", _notices));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Recorded on a clean slate, and whatever the caller's token says. The failure may have
            // BEEN a save, in which case the same unit of work would refuse this one too and the
            // connection would go on showing its last good state.
            await RecordRunFailureAsync(psaConnectionId, runId, tally, ex);
            throw;
        }
        finally
        {
            // Outside a run the helpers record nothing: a refresh of one ticket's notes is not a run.
            (_connection, _run, _cursor) = (null, null, null);
            _open = [];
        }

        return new SyncRunResult(tally.Fetched, tally.Created, tally.Updated, tally.Skipped, tally.Pages,
            tally.Notes, tally.Files, tally.FilesRemoved, tally.NotesRemoved, MoreToRead: more, Failed: _failed, Recovered: _recovered);
    }

    /// <summary>
    /// One ticket: save it, then read what hangs off it. Whatever goes wrong here is this ticket's
    /// problem alone - it is recorded and the run moves on. One ticket that could not be saved used
    /// to stop the run, and so every run after it.
    /// </summary>
    private async Task ApplyOneAsync(
        IServiceManagementConnector connector, ProviderCapabilities capabilities, IReadOnlyList<FieldMapping> rules,
        UnifiedTicket ticket, Tally tally, CancellationToken ct)
    {
        var connectionId = _connection!.Id;
        try
        {
            tally.Fetched++;
            tally.Touched.Add(ticket.ExternalId);
            switch (await sync.UpsertFromProviderAsync(connectionId, ticket, rules, ct))
            {
                case TicketSyncOutcome.Created: tally.Created++; break;
                case TicketSyncOutcome.Updated: tally.Updated++; break;
                default: tally.Skipped++; break;
            }
            Succeeded(ticket.ExternalId, SyncFailure.Operations.Apply);

            if (_connection.ImportNotes)
            {
                var (addedNotes, removedNotes) = await ImportNotesAsync(_connection, connector, ticket.ExternalId, ct);
                tally.Notes += addedNotes;
                tally.NotesRemoved += removedNotes;
            }
            await ResolveAssigneeNameAsync(connectionId, connector, ticket.ExternalId, ct);

            // Time logged provider-side never reaches the portal's stored totals otherwise:
            // they were only rewritten when time was logged from here, so a technician's own
            // entry left the dashboards under-reporting.
            //
            // Keyed off the ticket being fetched at all, NOT off the upsert outcome: adding a
            // time entry bumps the provider's activity date (so an incremental page returns
            // the ticket) but changes none of the fields in the update hash, so the upsert
            // reports "unchanged" and a stricter guard here skipped every refresh.
            if (capabilities.SupportsTimeEntries)
                await RefreshTimeTotalsAsync(connectionId, connector, ticket.ExternalId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !StopsTheRun(ex))
        {
            // The unit of work may be holding the very change that was refused. Start it again
            // before recording anything, or the record of the failure is refused with it.
            await ResetAsync(ct);
            _failures.Record(_connection!, _open, ticket.ExternalId, SyncFailure.Operations.Apply, ex);
            _failed++;
            await db.SaveChangesAsync(ct);
            Warn("Ticket {ExternalId} of connection {ConnectionId} could not be applied ({Category}); it will be tried again",
                ticket.ExternalId, connectionId, SyncFailureStore.Category(ex));
        }
    }

    /// <summary>
    /// Tries again the failures whose time has come. Each is read fresh from the PSA and put through
    /// the same path as any other ticket, so a success clears itself and a failure counts once more.
    /// </summary>
    private async Task RetryFailuresAsync(
        IServiceManagementConnector connector, ProviderCapabilities capabilities, IReadOnlyList<FieldMapping> rules,
        Tally tally, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = _open.Values
            .Where(f => f.Status == SyncFailureStatus.Pending && f.NextAttemptAt <= now)
            .OrderBy(f => f.NextAttemptAt)
            .Select(f => f.ExternalId)
            .Distinct()
            .Take(_options.RetriesPerRun)
            .ToList();

        foreach (var externalId in due)
        {
            if (_stopEarly) break;
            tally.Retried++;
            var before = _failed;
            UnifiedTicket? ticket;
            try
            {
                ticket = await connector.GetTicketAsync(externalId, ct);
            }
            catch (ConnectorException ex) when (!StopsTheRun(ex))
            {
                await FailedAsync(externalId, SyncFailure.Operations.Apply, ex, ct);
                continue;
            }

            if (ticket is null)
            {
                // Gone from the PSA. There is nothing left to read, so nothing is still owed.
                foreach (var key in _open.Keys.Where(k => k.ExternalId == externalId).ToList())
                    _failures.Resolve(_open, key.ExternalId, key.Operation);
                await db.SaveChangesAsync(ct);
                continue;
            }

            await ApplyOneAsync(connector, capabilities, rules, ticket, tally, ct);
            // Files owed for this ticket. A provider with no tenant-wide sweep reads every ticket
            // this run touched at the end, this one included; one with a sweep would not look at
            // this ticket's files again unless they were new, so they are read here.
            if (capabilities.SupportsAttachmentSweep && _open.ContainsKey((externalId, SyncFailure.Operations.Attachments))
                && _connection!.SyncAttachments && capabilities.SupportsAttachmentDownload)
                await ImportAttachmentsPerTicketAsync(_connection, connector, [externalId], ct);
            if (_failed == before) _recovered++;
        }
    }

    // ---- what the helpers report ---------------------------------------------------------------

    /// <summary>
    /// A read for one ticket failed. Kept, so it is tried again and can be seen - it used to be
    /// swallowed, and the run reported healthy with the notes or the hours simply not read.
    /// </summary>
    private async Task FailedAsync(string externalTicketId, string operation, ConnectorException error, CancellationToken ct)
    {
        if (_connection is null || _run is null) return; // a refresh outside a run: nothing to record against
        if (StopsTheRun(error))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();

        _failures.Record(_connection, _open, externalTicketId, operation, error);
        _failed++;
        await db.SaveChangesAsync(ct);

        var inARow = _failedInARow[operation] = _failedInARow.GetValueOrDefault(operation) + 1;
        if (inARow < _options.FailuresInARow) return;

        if (error.IsTransient)
        {
            // The PSA is not answering, or is asking us to slow down. Asking for the next five
            // hundred tickets' notes will not go differently: stop, and carry on next run.
            if (!_stopEarly)
                _notices.Add($"The run stopped early: the PSA failed {inARow} requests in a row ({error.Kind}). It carries on from the same place next time.");
            _stopEarly = true;
        }
        else if (_switchedOff.Add(operation))
        {
            // A refusal, not an outage: the same answer for every ticket. One line says so, in
            // place of a failure against each of them.
            _notices.Add($"Reading {operation} was stopped for the rest of this run after {inARow} refusals in a row ({error.Kind}): {SyncFailureStore.Describe(error)}");
        }
    }

    private void Succeeded(string externalTicketId, string operation)
    {
        _failedInARow[operation] = 0;
        if (_open.Count > 0) _failures.Resolve(_open, externalTicketId, operation);
    }

    /// <summary>The connection itself is refused: no ticket will fare better, so the run ends.</summary>
    private static bool StopsTheRun(Exception error)
        => error is ConnectorException { Kind: ConnectorFailureKind.Authentication };

    private void Progress(Tally t)
    {
        if (_run is null) return;
        _runs.Extend(_run);
        (_run.Fetched, _run.Created, _run.Updated, _run.Skipped, _run.Pages) = (t.Fetched, t.Created, t.Updated, t.Skipped, t.Pages);
        (_run.Notes, _run.NotesRemoved, _run.Attachments, _run.AttachmentsRemoved) = (t.Notes, t.NotesRemoved, t.Files, t.FilesRemoved);
        (_run.FailedRecords, _run.Retried, _run.Recovered) = (_failed, t.Retried, _recovered);
    }

    /// <summary>Drops everything the unit of work is holding and loads the run's own rows again.</summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        var (connectionId, runId, cursorId) = (_connection!.Id, _run!.Id, _cursor?.Id);
        db.ChangeTracker.Clear();
        _connection = await db.PsaConnections.FirstAsync(c => c.Id == connectionId, ct);
        _run = await db.SyncRuns.FirstAsync(r => r.Id == runId, ct);
        _cursor = cursorId is { } id ? await db.SyncCursors.FirstOrDefaultAsync(c => c.Id == id, ct) : null;
        _open = await _failures.OpenAsync(connectionId, ct);
    }

    private async Task RecordRunFailureAsync(Guid connectionId, Guid runId, Tally tally, Exception error)
    {
        var none = CancellationToken.None;
        try
        {
            db.ChangeTracker.Clear();
            var now = clock.GetUtcNow();
            var stopped = error is OperationCanceledException;
            var run = await db.SyncRuns.FirstOrDefaultAsync(r => r.Id == runId, none);
            if (run is not null)
            {
                _run = run;
                Progress(tally);
                // Stopped is not failed: the process is shutting down, and the next one carries on.
                run.Status = stopped ? SyncRunStatus.Abandoned : SyncRunStatus.Failed;
                run.FinishedAt = now;
                run.LeaseExpiresAt = now;
                run.Error = stopped ? "The run was stopped before it finished." : Shorten(RunError(error));
            }
            if (!stopped && await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, none) is { } connection)
            {
                connection.Status = ConnectionStatus.Degraded;
                connection.LastError = error.Message;
                // Kept beside the message: "the PSA rejected the credentials" is the one failure
                // the next run cannot get past, and the one that must not be retried every cycle.
                connection.LastErrorKind = error is ConnectorException kind ? kind.Kind.ToString() : null;
                connection.LastHealthCheckAt = now;
            }
            await db.SaveChangesAsync(none);
        }
        catch (Exception recording)
        {
            // The caller is about to be given the real failure. This one must not take its place.
            if (logger is not null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, recording,
                    "The failure of a sync run for connection {ConnectionId} could not be recorded", connectionId);
        }
    }

    /// <summary>A failed run's reason, for an administrator: the message where it was written for one, the kind otherwise.</summary>
    private static string RunError(Exception error)
        => error is ConnectorException or DeskException ? error.Message : SyncFailureStore.Describe(error);

    private static string Shorten(string text) => text.Length <= 1000 ? text : text[..1000];

    private void Warn(string message, params object?[] args)
    {
        if (logger is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, message, args);
    }

    private void Info(string message, params object?[] args)
    {
        if (logger is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, message, args);
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

        // Refused for every ticket so far this run: asking again for this one changes nothing.
        if (_switchedOff.Contains(SyncFailure.Operations.Notes)) return (0, 0);

        IReadOnlyList<UnifiedTicketNote> incoming;
        // A read that throws leaves an UNKNOWN list, not an empty one — returning here also means
        // nothing is reconciled, so a rate-limited ticket never loses its thread.
        try { incoming = await connector.GetNotesAsync(externalTicketId, ct); }
        catch (ConnectorException ex)
        {
            // One ticket's notes must not fail the whole run - and must not be forgotten either.
            await FailedAsync(externalTicketId, SyncFailure.Operations.Notes, ex, ct);
            return (0, 0);
        }
        Succeeded(externalTicketId, SyncFailure.Operations.Notes);

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

        IReadOnlyList<UnifiedTimeEntry>? entries = null;
        if (!_switchedOff.Contains(SyncFailure.Operations.Time))
        {
            try
            {
                entries = await connector.GetTimeEntriesAsync(externalTicketId, ct);
                Succeeded(externalTicketId, SyncFailure.Operations.Time);
            }
            catch (ConnectorException ex)
            {
                await FailedAsync(externalTicketId, SyncFailure.Operations.Time, ex, ct);
            }
        }

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
            if (_switchedOff.Contains(SyncFailure.Operations.Attachments) || _stopEarly) break;
            try
            {
                foreach (var file in await connector.GetAttachmentsAsync(externalTicketId, ct))
                    refs.Add(new ProviderAttachmentRef(externalTicketId, file));
                complete.Add(externalTicketId);
            }
            catch (ConnectorException ex)
            {
                // One ticket's files must not fail the run - and are owed until they are read.
                await FailedAsync(externalTicketId, SyncFailure.Operations.Attachments, ex, ct);
            }
        }
        if (complete.Count == 0) return (0, 0);
        var stored = await StoreAttachmentsAsync(connection, connector, refs, complete, ct);
        // Settled only where the list was read AND every file in it arrived.
        foreach (var externalTicketId in complete)
            if (!_filesFailed.Contains(externalTicketId))
                Succeeded(externalTicketId, SyncFailure.Operations.Attachments);
        return stored;
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
        ProviderAttachmentSweep sweep;
        try { sweep = await connector.GetRecentAttachmentsAsync(since, ct); }
        catch (ConnectorException ex)
        {
            // Files must not fail the whole run. But a sweep that was not read is said, not passed
            // over: the next one asks from the same point only if this run does not finish clean.
            if (_run is not null && StopsTheRun(ex)) throw;
            if (_run is not null) _notices.Add($"The attachment sweep could not be read ({ex.Kind}); files added since the last run are not in yet.");
            return (0, 0);
        }
        var incoming = sweep.Items;

        // A DATED sweep returns only recent files, so a file's absence from it says nothing about
        // whether it still exists — reconciling against that would delete the entire back catalogue.
        // Only a full sweep sees everything, and only then can deletions be inferred.
        //
        // And only when the provider says the list is ALL of them. A full sweep that stopped early is
        // a dated sweep in everything but name: the files past where it stopped are missing from the
        // list and still exist. They are imported from what was read; nothing is removed.
        if (since is null && !sweep.Complete && logger is not null)
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,
                "Attachment sweep of connection {ConnectionId} returned an incomplete list ({Count} files); "
                + "files deleted in the PSA are not being removed this run",
                connection.Id, incoming.Count);
        IReadOnlyList<string>? reconcilable = since is null && sweep.Complete
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
            catch (ConnectorException ex)
            {
                // Owed, and on record: a dated sweep will not offer this file again once the cursor
                // has moved past the day it was attached.
                _filesFailed.Add(externalTicketId);
                await FailedAsync(externalTicketId, SyncFailure.Operations.Attachments, ex, ct);
                continue;
            }
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

        // By id, which is what the filter holds and what the provider was asked for. It used to be
        // compared with the queue's NAME alone, so a connection limited to any queue imported
        // nothing at all. The name is used only for a provider that reports no id: where there is an
        // id it alone decides, or a board that happens to be CALLED "8" would pass as queue 8.
        var queues = Csv(c.FilterQueueIds);
        if (queues.Count > 0 && !queues.Contains(t.QueueOrBoardId ?? t.QueueOrBoard ?? "", StringComparer.OrdinalIgnoreCase)) return false;

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
