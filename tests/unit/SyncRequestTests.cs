using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
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
/// "Sync now", asked for and then run by the worker.
///
/// It used to run inside the web request that asked. A first import of a large PSA takes minutes,
/// and a request that long is cut off by whatever stands in front of the API: the person saw an
/// error, and the sync went on unseen. Asking is now a note on the connection. These hold what the
/// note means - asked twice is asked once, a paused connection is not asked - and what the worker
/// does with it: runs it once, carries on while there is more to read, waits for a run that has
/// the connection, and does not come back every few seconds to a PSA that stopped answering.
/// </summary>
public class SyncRequestTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly SyncOptions Small = new() { PageSize = 2, MaxPagesPerRun = 2, FailuresInARow = 3 };

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed class World
    {
        public required string DbName { get; init; }
        public required TestClock Clock { get; init; }
        public required StubConnector Connector { get; init; }

        /// <summary>A unit of work of its own, as each request and each turn of the worker has.</summary>
        public DeskDbContext Db() => TestDbContextFactory.ForPlatform(DbName);

        public async Task<SyncRequestDto> AskAsync(bool full = false, string? by = "Asha Admin")
        {
            await using var db = Db();
            return await new SyncRequestService(db, Clock).RequestAsync(Conn, full, by);
        }

        /// <summary>One turn of the worker for this connection.</summary>
        public async Task<RequestedSyncOutcome> WorkAsync()
        {
            await using var db = Db();
            var runner = new ConnectionSyncRunner(db, new FakeResolver(Connector),
                new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, Clock), Clock, new RecordingActivity()),
                new InMemoryObjectStorage(new AttachmentStorageOptions(), Clock), new HeuristicMalwareScanner(), Clock, options: Small);
            return await new RequestedSyncRunner(db, runner).RunAsync(Conn);
        }

        public async Task<T> ReadAsync<T>(Func<DeskDbContext, Task<T>> read)
        {
            await using var db = Db();
            return await read(db);
        }

        public Task<PsaConnection> ConnectionAsync() => ReadAsync(db => db.PsaConnections.AsNoTracking().SingleAsync(c => c.Id == Conn));
        public Task<List<SyncRun>> RunsAsync() => ReadAsync(db => db.SyncRuns.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync());
        public Task<int> TicketsAsync() => ReadAsync(db => db.Tickets.AsNoTracking().CountAsync());
        public Task<List<Guid>> PendingAsync() => ReadAsync(db => SyncRequests.Pending(db.PsaConnections.AsNoTracking()).Select(c => c.Id).ToListAsync());
    }

    private static async Task<World> WorldAsync(int tickets = 0, Action<PsaConnection>? configure = null)
    {
        var clock = new TestClock();
        var w = new World { DbName = Guid.NewGuid().ToString(), Clock = clock, Connector = new StubConnector { Paged = true } };
        var connection = new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", SyncAttachments = false, IsEnabled = true,
        };
        configure?.Invoke(connection);
        await using (var db = w.Db())
        {
            db.PsaConnections.Add(connection);
            await db.SaveChangesAsync();
        }
        for (var i = 1; i <= tickets; i++)
            w.Connector.Tickets.Add(new UnifiedTicket
            {
                ExternalId = i.ToString(), Title = "Ticket " + i, Status = "New", Priority = "Medium",
                RequesterExternalId = "co-1", CompanyName = "Acme", ModifiedAt = clock.GetUtcNow().AddDays(-1),
            });
        return w;
    }

    // ---- asking --------------------------------------------------------------------------------

    [Fact]
    public async Task Asking_is_a_note_on_the_connection_and_runs_nothing()
    {
        var w = await WorldAsync(tickets: 3);

        var asked = await w.AskAsync();

        asked.Should().Be(new SyncRequestDto(Conn, w.Clock.GetUtcNow(), false));
        var c = await w.ConnectionAsync();
        (c.SyncRequestedAt, c.SyncRequestedFull, c.SyncRequestedBy).Should().Be((w.Clock.GetUtcNow(), false, "Asha Admin"));
        (await w.PendingAsync()).Should().Equal(Conn);
        (await w.RunsAsync()).Should().BeEmpty("nothing ran in the request that asked");
        w.Connector.TicketRequests.Should().BeEmpty("and the PSA was not called");
    }

    [Fact]
    public async Task Asking_twice_is_asking_once_and_asking_for_everything_is_not_made_less_by_asking_again()
    {
        var w = await WorldAsync();
        var first = await w.AskAsync();

        w.Clock.Advance(TimeSpan.FromSeconds(3));
        (await w.AskAsync(by: "Someone Else")).Should().Be(first, "the same request, still waiting");
        (await w.ConnectionAsync()).SyncRequestedBy.Should().Be("Asha Admin");

        // Everything, after what changed: a new request, with its own time.
        w.Clock.Advance(TimeSpan.FromSeconds(3));
        var full = await w.AskAsync(full: true);
        (full.Full, full.RequestedAt).Should().Be((true, w.Clock.GetUtcNow()));

        w.Clock.Advance(TimeSpan.FromSeconds(3));
        (await w.AskAsync(full: false)).Should().Be(full, "it stays a request for everything");
    }

    [Fact]
    public async Task A_paused_or_archived_connection_cannot_be_asked_and_another_organizations_is_not_found()
    {
        var paused = await WorldAsync(configure: c => c.SyncPausedAt = DateTimeOffset.UtcNow);
        (await ((Func<Task>)(() => paused.AskAsync())).Should().ThrowAsync<ValidationFailedException>()).WithMessage("*paused*");

        var archived = await WorldAsync(configure: c => c.ArchivedAt = DateTimeOffset.UtcNow);
        (await ((Func<Task>)(() => archived.AskAsync())).Should().ThrowAsync<ValidationFailedException>()).WithMessage("*archived*");
        (await archived.ConnectionAsync()).SyncRequestedAt.Should().BeNull();

        var w = await WorldAsync();
        await using var theirs = TestDbContextFactory.ForTenant(w.DbName, Guid.NewGuid());
        await ((Func<Task>)(() => new SyncRequestService(theirs, w.Clock).RequestAsync(Conn, false, "x"))).Should().ThrowAsync<NotFoundException>();
        (await w.ConnectionAsync()).SyncRequestedAt.Should().BeNull("nothing was recorded about it");
    }

    [Fact]
    public async Task Pausing_or_archiving_withdraws_a_sync_that_was_asked_for()
    {
        foreach (var archive in new[] { false, true })
        {
            var h = AdminHarness.Create(Org);
            var connection = new PsaConnection
            {
                MspOrganizationId = Org, Name = "AT " + archive, Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://x",
                CredentialSecretRef = "mem://x", IsEnabled = true, Status = ConnectionStatus.Healthy,
            };
            h.Db.PsaConnections.Add(connection);
            await h.Db.SaveChangesAsync();
            await new SyncRequestService(h.Db, h.Clock).RequestAsync(connection.Id, full: true, "Asha Admin");
            var service = new ConnectionAdminService(h.Db, h.Secrets, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock),
                new FakeResolver(new StubConnector()), new ConnectionFieldCache(), new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), h.Clock);
            (await service.ListAsync()).Single().SyncRequestedAt.Should().Be(h.Clock.GetUtcNow(), "the card says a sync has been asked for");

            if (archive) await service.ArchiveAsync(connection.Id); else await service.PauseSyncAsync(connection.Id);

            var after = await h.Db.PsaConnections.AsNoTracking().SingleAsync();
            (after.SyncRequestedAt, after.SyncRequestedFull, after.SyncRequestedBy).Should().Be((null, false, null),
                "left standing, it would start by itself the moment the connection was resumed or restored");
        }
    }

    // ---- the worker ----------------------------------------------------------------------------

    [Fact]
    public async Task The_worker_runs_what_was_asked_for_once_and_records_who_asked()
    {
        var w = await WorldAsync(tickets: 3);
        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.NothingAsked);
        (await w.RunsAsync()).Should().BeEmpty("nobody asked");

        await w.AskAsync();
        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Done);

        (await w.TicketsAsync()).Should().Be(3);
        var run = (await w.RunsAsync()).Should().ContainSingle().Subject;
        (run.Trigger, run.Status, run.RequestedBy, run.Created).Should().Be((SyncRunTrigger.Manual, SyncRunStatus.Succeeded, "Asha Admin", 3));
        var c = await w.ConnectionAsync();
        (c.SyncRequestedAt, c.SyncRequestedFull, c.SyncRequestedBy).Should().Be((null, false, null));
        (await w.PendingAsync()).Should().BeEmpty();

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.NothingAsked);
        (await w.RunsAsync()).Should().ContainSingle("it ran once");
    }

    [Fact]
    public async Task A_read_too_long_for_one_run_is_carried_on_by_the_worker_and_not_started_again()
    {
        // Nine tickets, two a page, two pages a run: three runs. The request stands until the read
        // is done - as a continuation. Asked for again as "everything" each time, the second run
        // would start from the first page and the read would never end.
        var w = await WorldAsync(tickets: 9);
        await w.AskAsync(full: true);

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.More);
        var midway = await w.ConnectionAsync();
        (midway.SyncRequestedAt, midway.SyncRequestedFull).Should().Be((w.Clock.GetUtcNow(), false), "still asked for, and from here on a continuation");
        (await w.TicketsAsync()).Should().Be(4);

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.More);
        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Done);

        (await w.TicketsAsync()).Should().Be(9);
        (await w.RunsAsync()).Select(r => (r.Trigger, r.Status)).Should().Equal(
            (SyncRunTrigger.ManualFull, SyncRunStatus.Partial), (SyncRunTrigger.Manual, SyncRunStatus.Partial), (SyncRunTrigger.Manual, SyncRunStatus.Succeeded));
        w.Connector.TicketRequests.Select(r => r.Cursor).Should().Equal(null, "2", "4", "6", "8");
        (await w.ConnectionAsync()).SyncRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_request_waits_for_a_run_that_already_has_the_connection()
    {
        var w = await WorldAsync(tickets: 2);
        await using (var db = w.Db())
        {
            // The schedule's run, under way in another process.
            db.SyncRuns.Add(new SyncRun
            {
                MspOrganizationId = Org, PsaConnectionId = Conn, Trigger = SyncRunTrigger.Scheduled, Status = SyncRunStatus.Running,
                StartedAt = w.Clock.GetUtcNow(), LeaseExpiresAt = w.Clock.GetUtcNow().AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }
        await w.AskAsync();

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Busy);
        (await w.ConnectionAsync()).SyncRequestedAt.Should().NotBeNull("it is still asked for");
        (await w.TicketsAsync()).Should().Be(0);

        // That run ends. The next turn of the worker takes the request.
        await using (var db = w.Db())
        {
            var running = await db.SyncRuns.SingleAsync();
            (running.Status, running.FinishedAt) = (SyncRunStatus.Succeeded, w.Clock.GetUtcNow());
            await db.SaveChangesAsync();
        }
        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Done);
        (await w.TicketsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_run_that_fails_spends_the_request_and_is_not_tried_again_every_few_seconds()
    {
        var w = await WorldAsync(tickets: 2);
        w.Connector.OnTicketRequest = _ => new ConnectorException(ConnectorFailureKind.Authentication, "The PSA rejected the credentials.");
        await w.AskAsync();

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Failed);

        var c = await w.ConnectionAsync();
        c.SyncRequestedAt.Should().BeNull("asking again is a person's to do, once they have seen why");
        (c.Status, c.LastErrorKind).Should().Be((ConnectionStatus.Degraded, ConnectionStates.Authentication));
        (await w.RunsAsync()).Should().ContainSingle().Which.Status.Should().Be(SyncRunStatus.Failed);
        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.NothingAsked);
        w.Connector.TicketRequests.Should().HaveCount(1, "the PSA was asked once");
    }

    [Fact]
    public async Task A_run_the_PSA_stopped_answering_is_left_to_the_schedule_and_not_gone_back_to_at_once()
    {
        // Six tickets in one run; the PSA limits the rate on every ticket's notes. After three in a
        // row the run stops early with more to read. That is not "carry straight on": coming back
        // in five seconds is what the PSA just asked not to be done.
        var w = await WorldAsync(tickets: 6);
        w.Connector.NoteReadFailure = new ConnectorException(ConnectorFailureKind.RateLimited, "Slow down.");
        await w.AskAsync();

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Done);

        var run = (await w.RunsAsync()).Should().ContainSingle().Subject;
        (run.Status, run.FailedRecords).Should().Be((SyncRunStatus.Partial, 3));
        (await w.ConnectionAsync()).SyncRequestedAt.Should().BeNull("the schedule carries on from the same place, at its own pace");
        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.NothingAsked);
    }

    [Fact]
    public async Task Everything_asked_for_while_an_ordinary_sync_runs_is_run_after_it()
    {
        var w = await WorldAsync(tickets: 2);
        await w.AskAsync();
        // While the worker is reading from the PSA, someone presses "Re-sync all".
        var asked = false;
        w.Connector.OnTicketRequest = _ =>
        {
            if (asked) return null;
            asked = true;
            w.Clock.Advance(TimeSpan.FromSeconds(20));
            w.AskAsync(full: true, by: "Bilal Admin").GetAwaiter().GetResult();
            return null;
        };

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Done);

        var c = await w.ConnectionAsync();
        (c.SyncRequestedAt, c.SyncRequestedFull, c.SyncRequestedBy).Should().Be((w.Clock.GetUtcNow(), true, "Bilal Admin"), "the run that just ended was not this request");

        (await w.WorkAsync()).Should().Be(RequestedSyncOutcome.Done);
        (await w.RunsAsync()).Select(r => (r.Trigger, r.RequestedBy)).Should().Equal((SyncRunTrigger.Manual, "Asha Admin"), (SyncRunTrigger.ManualFull, "Bilal Admin"));
        (await w.ConnectionAsync()).SyncRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_connections_sync_state_says_a_sync_has_been_asked_for_until_it_is_done()
    {
        var w = await WorldAsync(tickets: 1);
        async Task<SyncStateDto> StateAsync()
        {
            var h = AdminHarness.Create(Org, w.DbName);
            await using var db = TestDbContextFactory.ForTenant(w.DbName, Org);
            return await new SyncHealthService(db, new AuditWriter(db, h.User, h.Tenant, w.Clock), w.Clock).StateAsync(Conn);
        }
        (await StateAsync()).Should().Match<SyncStateDto>(s => s.RequestedAt == null && !s.RequestedFull);

        await w.AskAsync(full: true);
        (await StateAsync()).Should().Match<SyncStateDto>(s => s.RequestedAt == w.Clock.GetUtcNow() && s.RequestedFull && !s.Running);

        await w.WorkAsync();
        var done = await StateAsync();
        (done.RequestedAt, done.RequestedFull).Should().Be((null, false));
        done.Runs.Should().ContainSingle().Which.Trigger.Should().Be(nameof(SyncRunTrigger.ManualFull));
    }
}
