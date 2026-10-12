using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// How a connection's inbound sync keeps its place, shares its connection, and deals with what it
/// could not read.
///
/// Before this the whole of a connection's sync state was one timestamp, set to the moment a run
/// ended; a run stopped at fifty pages and moved that timestamp anyway; nothing stopped two runs
/// working one connection; one ticket that could not be saved stopped every run; and a rate limit
/// on a ticket's notes was swallowed with the run reported healthy. None of it had a test: the stub
/// every sync test used returned one page and never failed.
/// </summary>
public class SyncEngineTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly SyncOptions Small = new() { PageSize = 2, MaxPagesPerRun = 2, FailuresInARow = 3 };

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    /// <summary>The real ticket sync, except for the tickets it is told it cannot save.</summary>
    private sealed class PickySync(ITicketSyncService inner) : ITicketSyncService
    {
        public HashSet<string> Refuses { get; } = [];
        public Task<TicketSyncOutcome> UpsertFromProviderAsync(
            Guid psaConnectionId, UnifiedTicket ticket, IReadOnlyList<FieldMapping> mappingRules, CancellationToken ct = default)
            => Refuses.Contains(ticket.ExternalId)
                ? throw new InvalidOperationException("SELECT secret FROM somewhere -- a developer's message, not an administrator's")
                : inner.UpsertFromProviderAsync(psaConnectionId, ticket, mappingRules, ct);
    }

    private sealed class World
    {
        public required string DbName { get; init; }
        public required TestClock Clock { get; init; }
        public required StubConnector Connector { get; init; }
        public required InMemoryObjectStorage Storage { get; init; }
        public HashSet<string> Refuses { get; } = [];

        /// <summary>A unit of work of its own, as each run and each request has.</summary>
        public DeskDbContext Db() => TestDbContextFactory.ForPlatform(DbName);

        public async Task<SyncRunResult> RunAsync(SyncOptions? options = null, bool full = false, bool manual = false)
        {
            await using var db = Db();
            return await Runner(db, options).RunAsync(Conn, new SyncRunRequest(full, manual, manual ? "Asha Admin" : null));
        }

        public ConnectionSyncRunner Runner(DeskDbContext db, SyncOptions? options = null)
        {
            var sync = new PickySync(new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, Clock), Clock, new RecordingActivity()));
            foreach (var id in Refuses) sync.Refuses.Add(id);
            return new ConnectionSyncRunner(db, new FakeResolver(Connector), sync, Storage, new HeuristicMalwareScanner(), Clock,
                options: options ?? Small);
        }

        public async Task<T> ReadAsync<T>(Func<DeskDbContext, Task<T>> read)
        {
            await using var db = Db();
            return await read(db);
        }

        public Task<SyncCursor?> CursorAsync() => ReadAsync(db => db.SyncCursors.AsNoTracking().SingleOrDefaultAsync());
        public Task<List<SyncFailure>> FailuresAsync() => ReadAsync(db => db.SyncFailures.AsNoTracking().OrderBy(f => f.ExternalId).ToListAsync());
        public Task<List<SyncRun>> RunsAsync() => ReadAsync(db => db.SyncRuns.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync());
        public Task<List<string>> TicketsAsync() => ReadAsync(db => db.Tickets.AsNoTracking().OrderBy(t => t.ExternalTicketId).Select(t => t.ExternalTicketId!).ToListAsync());
        public Task<PsaConnection> ConnectionAsync() => ReadAsync(db => db.PsaConnections.AsNoTracking().SingleAsync());
    }

    private static async Task<World> WorldAsync(int tickets, Action<PsaConnection>? configure = null, bool sweeps = false)
    {
        var clock = new TestClock();
        var w = new World
        {
            DbName = Guid.NewGuid().ToString(), Clock = clock,
            Connector = new StubConnector { Paged = true, SupportsAttachmentSweep = sweeps },
            Storage = new InMemoryObjectStorage(new AttachmentStorageOptions(), clock),
        };
        var connection = new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", SyncAttachments = false,
        };
        configure?.Invoke(connection);
        await using (var db = w.Db())
        {
            db.PsaConnections.Add(connection);
            await db.SaveChangesAsync();
        }
        // Changed a day before the test begins: inside a first read, outside every later one.
        for (var i = 1; i <= tickets; i++)
            w.Connector.Tickets.Add(Ticket(i.ToString(), clock.GetUtcNow().AddDays(-1)));
        return w;
    }

    private static UnifiedTicket Ticket(string id, DateTimeOffset modifiedAt) => new()
    {
        ExternalId = id, Title = "Ticket " + id, Status = "New", Priority = "Medium",
        RequesterExternalId = "co-1", CompanyName = "Acme", ModifiedAt = modifiedAt,
    };

    private static ConnectorException Provider(ConnectorFailureKind kind, string message = "the PSA said no")
        => new(kind, message);

    // ---- the cursor ----------------------------------------------------------------------------

    [Fact]
    public async Task The_cursor_moves_to_when_the_run_started_not_to_when_it_ended()
    {
        var w = await WorldAsync(tickets: 2);
        var started = w.Clock.GetUtcNow();
        // The run takes ten minutes. A ticket changed five minutes in, after its page was read.
        w.Connector.OnTicketRequest = _ => { w.Clock.Advance(TimeSpan.FromMinutes(10)); return null; };
        var changedDuringTheRun = started.AddMinutes(5);

        await w.RunAsync();

        var cursor = await w.CursorAsync();
        cursor!.Watermark.Should().Be(started - Small.Overlap);
        cursor.Watermark.Should().BeBefore(changedDuringTheRun, "so the next run asks for it; a cursor at the END of the run was past it for good");

        await w.RunAsync();
        w.Connector.TicketRequests[^1].ModifiedSince.Should().Be(started - Small.Overlap);
    }

    [Fact]
    public async Task A_connection_with_no_cursor_yet_starts_an_hour_before_its_old_one()
    {
        // The old single timestamp was when a run ended, so it says nothing for what changed
        // during that run. The first run under cursors reaches back past it.
        var last = new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero);
        var w = await WorldAsync(tickets: 1, c => c.LastSuccessfulSyncAt = last);

        await w.RunAsync();

        w.Connector.TicketRequests[0].ModifiedSince.Should().Be(last - Small.FirstRunOverlap);
    }

    [Fact]
    public async Task A_read_larger_than_one_runs_budget_is_carried_on_by_the_next_run()
    {
        // Seven tickets, two a page, two pages a run. It used to stop at its page limit, log a
        // warning, and move the cursor to "now": the other three were never asked for again.
        var w = await WorldAsync(tickets: 7);
        var firstStarted = w.Clock.GetUtcNow();

        var first = await w.RunAsync();
        (first.Fetched, first.MoreToRead).Should().Be((4, true));
        var midway = await w.CursorAsync();
        (midway!.Continuation, midway.ContinuationPages, midway.Watermark).Should().Be(("4", 2, null), "the place it stopped is kept, and the cursor has not moved");
        (await w.ConnectionAsync()).LastSuccessfulSyncAt.Should().BeNull("the read has not finished");
        (await w.RunsAsync()).Single().Status.Should().Be(SyncRunStatus.Partial);

        w.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await w.RunAsync();

        (second.Fetched, second.MoreToRead).Should().Be((3, false));
        (await w.TicketsAsync()).Should().Equal("1", "2", "3", "4", "5", "6", "7");
        var done = await w.CursorAsync();
        (done!.Continuation, done.ContinuationPages).Should().Be((null, 0));
        done.Watermark.Should().Be(firstStarted - Small.Overlap, "from when the FIRST run of the read started: five minutes of changes happened since");
        w.Connector.TicketRequests.Select(r => r.Cursor).Should().Equal(null, "2", "4", "6");
        w.Connector.TicketRequests.Select(r => r.ModifiedSince).Distinct().Should().ContainSingle("every page of one read asks the same question");
    }

    [Fact]
    public async Task A_saved_position_the_provider_no_longer_honours_starts_the_read_again()
    {
        var w = await WorldAsync(tickets: 5);
        await w.RunAsync();
        // A page cursor is the provider's to expire. The saved one is refused; the fresh ones a
        // restarted read is handed are honoured as usual.
        var saved = true;
        w.Connector.OnTicketRequest = f =>
        {
            if (!saved || f.Cursor != "4") return null;
            saved = false;
            return Provider(ConnectorFailureKind.InvalidRequest, "cursor expired");
        };

        var run = await w.RunAsync(Small with { MaxPagesPerRun = 10 });

        run.MoreToRead.Should().BeFalse();
        (await w.TicketsAsync()).Should().Equal("1", "2", "3", "4", "5");
        w.Connector.TicketRequests.Select(r => r.Cursor).Should().Equal(null, "2", "4", null, "2", "4");
        (await w.ConnectionAsync()).Status.Should().Be(ConnectionStatus.Healthy);
    }

    [Fact]
    public async Task A_full_run_asks_for_everything_and_replaces_a_read_in_progress()
    {
        var w = await WorldAsync(tickets: 5);
        await w.RunAsync();                                  // stops midway, position saved

        await w.RunAsync(Small with { MaxPagesPerRun = 10 }, full: true);

        var request = w.Connector.TicketRequests[2];
        (request.Cursor, request.ModifiedSince).Should().Be((null, null));
        (await w.RunsAsync())[^1].Trigger.Should().Be(SyncRunTrigger.ManualFull);
    }

    [Fact]
    public async Task The_attachment_sweep_waits_for_the_ticket_read_to_finish()
    {
        // A sweep stores files only for tickets the portal holds, and a full one removes files a
        // complete list no longer has: neither is right while half the tickets are still unread.
        var w = await WorldAsync(tickets: 5, c => c.SyncAttachments = true, sweeps: true);

        await w.RunAsync();
        w.Connector.AttachmentSweeps.Should().Be(0);

        await w.RunAsync(Small with { MaxPagesPerRun = 10 });
        w.Connector.AttachmentSweeps.Should().Be(1);
    }

    // ---- one run at a time ---------------------------------------------------------------------

    [Fact]
    public async Task Two_runs_cannot_work_one_connection_at_once()
    {
        // The scheduled sync and "Sync now" could, each importing the same notes and files.
        var w = await WorldAsync(tickets: 2);
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        w.Connector.OnTicketRequest = _ => { reached.TrySetResult(); release.Task.GetAwaiter().GetResult(); return null; };

        var scheduled = Task.Run(() => w.RunAsync());
        await reached.Task;

        var manual = () => w.RunAsync(manual: true);
        (await manual.Should().ThrowAsync<ConflictException>()).WithMessage("*already running*");
        (await w.ConnectionAsync()).Status.Should().NotBe(ConnectionStatus.Degraded, "being told to wait is not a failure of the connection");

        release.SetResult();
        (await scheduled).Fetched.Should().Be(2);
        (await w.RunsAsync()).Should().ContainSingle();

        // And once it has finished, the next one starts.
        w.Connector.OnTicketRequest = null;
        (await w.RunAsync(manual: true)).Should().NotBeNull();
        var runs = await w.RunsAsync();
        (runs[^1].Trigger, runs[^1].RequestedBy).Should().Be((SyncRunTrigger.Manual, "Asha Admin"));
    }

    [Fact]
    public async Task A_run_whose_process_stopped_is_taken_over_once_its_lease_has_run_out()
    {
        var w = await WorldAsync(tickets: 1);
        await using (var db = w.Db())
        {
            db.SyncRuns.Add(new SyncRun
            {
                MspOrganizationId = Org, PsaConnectionId = Conn, Status = SyncRunStatus.Running,
                StartedAt = w.Clock.GetUtcNow(), LeaseExpiresAt = w.Clock.GetUtcNow().AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        // Still inside its lease: it may be alive.
        var tooSoon = () => w.RunAsync();
        await tooSoon.Should().ThrowAsync<ConflictException>();

        w.Clock.Advance(TimeSpan.FromMinutes(11));
        (await w.RunAsync()).Fetched.Should().Be(1);

        var runs = await w.RunsAsync();
        (runs[0].Status, runs[0].FinishedAt).Should().Be((SyncRunStatus.Abandoned, w.Clock.GetUtcNow()));
        runs[1].Status.Should().Be(SyncRunStatus.Succeeded);
    }

    // ---- what could not be read or applied -----------------------------------------------------

    [Fact]
    public async Task One_ticket_that_cannot_be_saved_does_not_stop_the_others()
    {
        // It used to stop the run - and so every run after it, the connection Degraded behind one record.
        var w = await WorldAsync(tickets: 3);
        w.Refuses.Add("2");

        var run = await w.RunAsync(Small with { MaxPagesPerRun = 10 });

        (run.Failed, run.MoreToRead).Should().Be((1, false));
        (await w.TicketsAsync()).Should().Equal("1", "3");
        var failure = (await w.FailuresAsync()).Single();
        (failure.ExternalId, failure.Operation, failure.Category, failure.Status, failure.Attempts)
            .Should().Be(("2", SyncFailure.Operations.Apply, "Apply", SyncFailureStatus.Pending, 1));
        failure.Message.Should().NotContain("SELECT", "an unexpected exception's text is a developer's, and is not shown");
        failure.NextAttemptAt.Should().Be(w.Clock.GetUtcNow().AddMinutes(5));
        (await w.ConnectionAsync()).Status.Should().Be(ConnectionStatus.Healthy);
        (await w.CursorAsync())!.Watermark.Should().NotBeNull("the failure is on record, so the cursor can move on without losing it");
        var record = (await w.RunsAsync()).Single();
        (record.Status, record.FailedRecords).Should().Be((SyncRunStatus.Succeeded, 1));
    }

    [Fact]
    public async Task A_failed_record_is_tried_again_and_clears_itself_when_it_goes_through()
    {
        var w = await WorldAsync(tickets: 3);
        w.Refuses.Add("2");
        await w.RunAsync(Small with { MaxPagesPerRun = 10 });

        // Before its time: left alone.
        w.Clock.Advance(TimeSpan.FromMinutes(1));
        (await w.RunAsync()).Recovered.Should().Be(0);
        (await w.FailuresAsync()).Single().Attempts.Should().Be(1);

        // Its time has come, and it still fails: one more attempt, a longer wait.
        w.Clock.Advance(TimeSpan.FromMinutes(5));
        await w.RunAsync();
        var again = (await w.FailuresAsync()).Single();
        (again.Attempts, again.NextAttemptAt).Should().Be((2, w.Clock.GetUtcNow().AddMinutes(10)));

        // Whatever was wrong is put right.
        w.Refuses.Clear();
        w.Clock.Advance(TimeSpan.FromMinutes(11));
        var run = await w.RunAsync();

        (run.Recovered, run.Failed).Should().Be((1, 0));
        (await w.TicketsAsync()).Should().Equal("1", "2", "3");
        var resolved = (await w.FailuresAsync()).Single();
        (resolved.Status, resolved.ResolvedAt).Should().Be((SyncFailureStatus.Resolved, w.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task After_six_attempts_a_failure_waits_for_a_person()
    {
        var w = await WorldAsync(tickets: 1);
        w.Refuses.Add("1");
        await w.RunAsync();
        for (var i = 0; i < 8; i++)
        {
            w.Clock.Advance(TimeSpan.FromHours(7));
            await w.RunAsync();
        }

        var failure = (await w.FailuresAsync()).Single();
        (failure.Attempts, failure.Status, failure.NextAttemptAt)
            .Should().Be((SyncFailureStore.MaxAttempts, SyncFailureStatus.NeedsReview, null));
    }

    [Fact]
    public async Task A_ticket_that_has_gone_from_the_PSA_owes_nothing()
    {
        var w = await WorldAsync(tickets: 2);
        w.Refuses.Add("2");
        await w.RunAsync();
        w.Connector.Tickets.RemoveAll(t => t.ExternalId == "2");

        w.Clock.Advance(TimeSpan.FromMinutes(6));
        await w.RunAsync();

        (await w.FailuresAsync()).Single().Status.Should().Be(SyncFailureStatus.Resolved);
    }

    [Fact]
    public async Task A_rate_limit_on_one_tickets_notes_is_kept_and_the_notes_are_read_later()
    {
        // It used to be swallowed: the run reported healthy, the cursor moved on, and those notes
        // were not read again until the ticket next changed - which might be never.
        var w = await WorldAsync(tickets: 2);
        w.Connector.Notes["2"] = [new UnifiedTicketNote("n-1", "Jane Tech", "Rebooted the switch", IsPublic: true, w.Clock.GetUtcNow())];
        w.Connector.NoteReadFailureFor["2"] = new ConnectorException(ConnectorFailureKind.RateLimited, "slow down") { RetryAfter = TimeSpan.FromMinutes(30) };

        var run = await w.RunAsync();

        (run.Failed, run.Notes).Should().Be((1, 0));
        (await w.TicketsAsync()).Should().Equal("1", "2");
        var failure = (await w.FailuresAsync()).Single();
        (failure.ExternalId, failure.Operation, failure.Category, failure.Status)
            .Should().Be(("2", SyncFailure.Operations.Notes, nameof(ConnectorFailureKind.RateLimited), SyncFailureStatus.Pending));
        failure.NextAttemptAt.Should().Be(w.Clock.GetUtcNow().AddMinutes(30), "never sooner than the PSA asked");

        w.Connector.NoteReadFailureFor.Clear();
        w.Clock.Advance(TimeSpan.FromMinutes(31));
        var later = await w.RunAsync();

        (later.Notes, later.Recovered).Should().Be((1, 1));
        (await w.ReadAsync(db => db.TicketNotes.AsNoTracking().Select(n => n.Body).SingleAsync())).Should().Be("Rebooted the switch");
        (await w.FailuresAsync()).Single().Status.Should().Be(SyncFailureStatus.Resolved);
    }

    [Fact]
    public async Task When_the_PSA_stops_answering_the_run_stops_and_carries_on_from_the_same_place()
    {
        // Three reads in a row timed out. The next five hundred will not go differently.
        var w = await WorldAsync(tickets: 6);
        w.Connector.NoteReadFailure = Provider(ConnectorFailureKind.Timeout, "timed out");

        var run = await w.RunAsync(Small with { PageSize = 10, MaxPagesPerRun = 10 });

        (run.Fetched, run.Failed, run.MoreToRead).Should().Be((3, 3, true));
        w.Connector.NoteReads.Should().Be(3, "it stopped asking");
        var record = (await w.RunsAsync()).Single();
        record.Status.Should().Be(SyncRunStatus.Partial);
        record.Notice.Should().Contain("stopped early").And.Contain("Timeout");
        (await w.CursorAsync())!.Watermark.Should().BeNull("the page was not finished, so the cursor has not moved past it");
        (await w.ConnectionAsync()).Status.Should().Be(ConnectionStatus.Healthy);

        // The PSA is back. The same page is read again and nothing is missing.
        w.Connector.NoteReadFailure = null;
        w.Clock.Advance(TimeSpan.FromMinutes(6));
        var next = await w.RunAsync(Small with { PageSize = 10, MaxPagesPerRun = 10 });

        next.MoreToRead.Should().BeFalse();
        (await w.TicketsAsync()).Should().Equal("1", "2", "3", "4", "5", "6");
        (await w.FailuresAsync()).Should().OnlyContain(f => f.Status == SyncFailureStatus.Resolved);
    }

    [Fact]
    public async Task A_refusal_for_every_ticket_is_said_once_not_filed_against_each_of_them()
    {
        // The API user may not read notes. That is one fact about the connection, not a failure of
        // each of five thousand tickets.
        var w = await WorldAsync(tickets: 6);
        w.Connector.NoteReadFailure = Provider(ConnectorFailureKind.PermissionDenied, "The API user cannot read TicketNotes.");

        var run = await w.RunAsync(Small with { PageSize = 10, MaxPagesPerRun = 10 });

        (run.Fetched, run.MoreToRead).Should().Be((6, false));
        w.Connector.NoteReads.Should().Be(3, "after three refusals in a row it stopped asking, for this run");
        (await w.TicketsAsync()).Should().HaveCount(6, "the tickets themselves are fine");
        var failures = await w.FailuresAsync();
        failures.Should().HaveCount(3).And.OnlyContain(f => f.Status == SyncFailureStatus.NeedsReview && f.NextAttemptAt == null,
            "a refusal is the same refusal next time: it waits for a person, it is not retried");
        var record = (await w.RunsAsync()).Single();
        record.Status.Should().Be(SyncRunStatus.Succeeded);
        record.Notice.Should().Contain("notes").And.Contain("PermissionDenied").And.Contain("cannot read TicketNotes");
    }

    [Fact]
    public async Task Rejected_credentials_fail_the_run_and_the_connection_says_why()
    {
        var w = await WorldAsync(tickets: 3);
        w.Connector.NoteReadFailure = Provider(ConnectorFailureKind.Authentication, "The API key was rejected.");

        var act = () => w.RunAsync();

        await act.Should().ThrowAsync<ConnectorException>();
        var connection = await w.ConnectionAsync();
        (connection.Status, connection.LastError).Should().Be((ConnectionStatus.Degraded, "The API key was rejected."));
        var record = (await w.RunsAsync()).Single();
        (record.Status, record.Error).Should().Be((SyncRunStatus.Failed, "The API key was rejected."));
        record.FinishedAt.Should().NotBeNull();
        (await w.CursorAsync())!.Watermark.Should().BeNull();

        // And it is not left holding the connection.
        w.Connector.NoteReadFailure = null;
        (await w.RunAsync()).Fetched.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_run_stopped_by_a_shutdown_is_not_a_failure_of_the_connection()
    {
        // The worker is being replaced by a deploy. The connection is fine; the next process carries on.
        var w = await WorldAsync(tickets: 4);
        await w.RunAsync();                                  // two pages in, position saved
        w.Connector.OnTicketRequest = _ => throw new OperationCanceledException();

        var act = () => w.RunAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
        var connection = await w.ConnectionAsync();
        (connection.Status, connection.LastError).Should().Be((ConnectionStatus.Healthy, null));
        var stopped = (await w.RunsAsync())[^1];
        (stopped.Status, stopped.Error).Should().Be((SyncRunStatus.Abandoned, "The run was stopped before it finished."));

        // Nothing is held, and nothing is lost: the next run starts at once and finishes the read.
        w.Connector.OnTicketRequest = null;
        (await w.RunAsync()).MoreToRead.Should().BeFalse();
        (await w.TicketsAsync()).Should().Equal("1", "2", "3", "4");
    }

    [Fact]
    public async Task A_file_that_could_not_be_downloaded_is_owed_and_fetched_later()
    {
        // A dated sweep offers a file once. If its download failed, nothing asked for it again.
        var w = await WorldAsync(tickets: 1, c => c.SyncAttachments = true, sweeps: true);
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];
        w.Connector.Attachments["1"] = [(new UnifiedAttachment("f-1", "diagram.png", "image/png", png.Length) { CreatedAt = w.Clock.GetUtcNow(), AuthorName = "Jane Tech" }, png)];
        w.Connector.DownloadFailsFor.Add("f-1");

        var run = await w.RunAsync();

        (run.Attachments, run.Failed).Should().Be((0, 1));
        var failure = (await w.FailuresAsync()).Single();
        (failure.Operation, failure.Category, failure.Status).Should().Be((SyncFailure.Operations.Attachments, nameof(ConnectorFailureKind.Timeout), SyncFailureStatus.Pending));

        w.Connector.DownloadFailsFor.Clear();
        w.Clock.Advance(TimeSpan.FromMinutes(6));
        await w.RunAsync();

        (await w.ReadAsync(db => db.TicketAttachments.AsNoTracking().Select(a => a.OriginalFileName).SingleAsync())).Should().Be("diagram.png");
        (await w.FailuresAsync()).Single().Status.Should().Be(SyncFailureStatus.Resolved);
    }

    [Fact]
    public async Task Every_run_leaves_a_record_of_what_it_did()
    {
        var w = await WorldAsync(tickets: 3);
        w.Connector.OnTicketRequest = _ => { w.Clock.Advance(TimeSpan.FromSeconds(20)); return null; };
        var started = w.Clock.GetUtcNow();

        await w.RunAsync(Small with { MaxPagesPerRun = 10 });

        var record = (await w.RunsAsync()).Single();
        (record.Trigger, record.Status, record.StartedAt, record.FinishedAt).Should().Be((SyncRunTrigger.Scheduled, SyncRunStatus.Succeeded, started, w.Clock.GetUtcNow()));
        (record.Fetched, record.Created, record.Pages, record.FailedRecords, record.Error).Should().Be((3, 3, 2, 0, null));
        record.MspOrganizationId.Should().Be(Org, "written under platform scope, so the organization is the connection's, set by hand");
    }

    [Fact]
    public void A_retry_waits_longer_each_time_and_never_less_than_the_PSA_asked()
    {
        Enumerable.Range(1, 8).Select(n => SyncFailureStore.Delay(n).TotalMinutes)
            .Should().Equal(5, 10, 20, 40, 80, 160, 320, 360);
        SyncFailureStore.Delay(1, TimeSpan.FromHours(2)).Should().Be(TimeSpan.FromHours(2));
        SyncFailureStore.Delay(4, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromMinutes(40));
    }
}
