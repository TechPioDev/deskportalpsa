using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

public class TicketSyncTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private static async Task<DeskDbContext> SeedConnectionAsync(string dbName)
    {
        var db = TestDbContextFactory.ForPlatform(dbName);
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static TicketSyncService Service(DeskDbContext db, TestClock clock, ISyncEventStore? events = null)
        => new(db, new MappingEngine(), events ?? new SyncEventStore(db, clock), clock, new RecordingActivity());

    private static UnifiedTicket Incoming(
        string extId, string title, string status, DateTimeOffset? createdAt = null,
        string? queue = null) => new()
    {
        ExternalId = extId, Title = title, Status = status, Priority = "1",
        RequesterExternalId = "1", RequesterName = "Acme", RequesterEmail = "a@acme.test",
        CreatedAt = createdAt, QueueOrBoard = queue,
    };

    [Fact]
    public async Task First_sync_creates_the_ticket_and_the_client_company()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);

        var outcome = await Service(db, clock).UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), []);

        outcome.Should().Be(TicketSyncOutcome.Created);
        var ticket = await db.Tickets.SingleAsync();
        ticket.ExternalTicketId.Should().Be("500");
        ticket.PsaStatus.Should().Be("5");
        (await db.ClientCompanies.CountAsync()).Should().Be(1); // company auto-created
    }

    [Fact]
    public async Task Resync_with_no_changes_is_skipped_as_unchanged()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);
        var svc = Service(db, clock);

        await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), []);
        var second = await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), []);

        second.Should().Be(TicketSyncOutcome.SkippedUnchanged);
    }

    /// <summary>Captures what was logged, so "it says so out loud" can actually be asserted.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<TicketSyncService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,
            Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (level == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, ex));
        }
    }

    [Fact]
    public async Task A_provider_value_that_nothing_maps_is_reported_rather_than_passed_through_silently()
    {
        // The failure this makes visible: an unmapped value falls through to the provider's own
        // value, which looks exactly like a mapping that passed it through on purpose. ConnectWise
        // ran that way on every status and priority of every ticket, with no error anywhere, and it
        // took reading the database to notice.
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(Guid.NewGuid().ToString());
        // Priority IS mapped, status is not — so the assertion also shows a mapped value stays quiet.
        db.FieldMappings.Add(new FieldMapping
        {
            MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
            Scope = MappingScope.ConnectionOverride, Direction = MappingDirection.Bidirectional,
            PortalField = "priority", ExternalField = "priority", PortalValue = "NORMAL", ExternalValue = "1",
        });
        await db.SaveChangesAsync();
        var log = new CapturingLogger();
        var rules = await db.FieldMappings.ToListAsync();
        var svc = new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, clock), clock,
            new RecordingActivity(), log);

        await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "Scheduled"), rules);

        log.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Scheduled").And.Contain("status");
        (await db.Tickets.SingleAsync()).PortalStatus
            .Should().Be("Scheduled", "the raw value is still used — this reports, it does not change behaviour");
    }

    [Fact]
    public async Task The_same_unmapped_value_is_reported_once_not_on_every_ticket()
    {
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(Guid.NewGuid().ToString());
        var log = new CapturingLogger();
        var svc = new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, clock), clock,
            new RecordingActivity(), log);

        // A desk with a thousand tickets in one unmapped state must not write a thousand lines.
        await svc.UpsertFromProviderAsync(Conn, Incoming("501", "A", "Scheduled"), []);
        await svc.UpsertFromProviderAsync(Conn, Incoming("502", "B", "Scheduled"), []);

        log.Warnings.Count(w => w.Contains("Scheduled")).Should().Be(1);
    }

    [Fact]
    public async Task Moving_a_ticket_to_another_queue_reaches_the_portal()
    {
        // A queue move changes nothing else about a ticket, so without the queue in the hash the
        // upsert reports "unchanged" and the portal goes on showing the queue the ticket left. The
        // same gap meant a change to a queue MAPPING never reached tickets already imported —
        // re-pointing two rules on the live connection moved exactly zero of 124 tickets.
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(Guid.NewGuid().ToString());
        var svc = Service(db, clock);

        await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5", queue: "Level I Support"), []);
        var outcome = await svc.UpsertFromProviderAsync(
            Conn, Incoming("500", "Printer", "5", queue: "Level II Support"), []);

        outcome.Should().Be(TicketSyncOutcome.Updated, "the queue is a change, not a no-op");
        (await db.Tickets.SingleAsync()).QueueOrBoard.Should().Be("Level II Support");
    }

    [Fact]
    public async Task A_raise_date_arriving_later_reaches_a_ticket_that_is_otherwise_unchanged()
    {
        // How the ConnectWise date columns stayed empty across a full re-import. The connector was
        // reading the raise date correctly by then, but every existing row hashed identically to its
        // stored state and short-circuited as unchanged before the write. The import reported
        // success, the tickets were all there, and the column was null on every one of them.
        //
        // The date is what changed, so the date has to be in the hash that decides whether anything
        // changed. This also means the backfill needs no migration: the first sync after the field
        // joins the hash rewrites the rows that were missing it.
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);
        var svc = Service(db, clock);

        // Imported before the raise date was captured.
        await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), []);
        (await db.Tickets.SingleAsync()).PsaCreatedAt.Should().BeNull();

        // The same ticket, same everything, now carrying the date the provider had all along.
        var raised = new DateTimeOffset(2026, 1, 9, 8, 30, 0, TimeSpan.Zero);
        var outcome = await svc.UpsertFromProviderAsync(
            Conn, Incoming("500", "Printer", "5", raised), []);

        outcome.Should().Be(TicketSyncOutcome.Updated, "the raise date is a change, not a no-op");
        (await db.Tickets.SingleAsync()).PsaCreatedAt.Should().Be(raised);
    }

    [Fact]
    public async Task Changed_ticket_updates_in_place()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);
        var svc = Service(db, clock);

        await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), []);
        var outcome = await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer jammed", "1"), []);

        outcome.Should().Be(TicketSyncOutcome.Updated);
        (await db.Tickets.SingleAsync()).Title.Should().Be("Printer jammed");
    }

    [Fact]
    public async Task Inbound_status_is_translated_by_the_mapping_rules()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);

        var rules = new List<FieldMapping>
        {
            new()
            {
                MspOrganizationId = Org, Provider = ProviderType.AutotaskPsa, Scope = MappingScope.ProviderDefault,
                PortalField = "status", ExternalField = "status", PortalValue = "RESOLVED", ExternalValue = "5",
                Direction = MappingDirection.Bidirectional,
            },
        };

        await Service(db, clock).UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), rules);

        var ticket = await db.Tickets.SingleAsync();
        ticket.PortalStatus.Should().Be("RESOLVED"); // translated
        ticket.PsaStatus.Should().Be("5");           // raw preserved
    }

    [Fact]
    public async Task Portal_originated_change_is_skipped_as_echo()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);
        var events = new SyncEventStore(db, clock);
        var svc = Service(db, clock, events);

        // Establish the ticket.
        await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "5"), []);
        var ticket = await db.Tickets.SingleAsync();

        // Simulate the portal having just pushed the *next* state (status -> "1"), recording its hash.
        // Through the SAME canonical function production uses on both sides: a hand-rolled copy here
        // would pass while echo suppression was broken, which is exactly what it did.
        var portalHash = UpdateHasher.ForTicketState(
            status: "1", priority: "1", category: null, title: "Printer", description: null,
            resolvedAt: null, closedAt: null, slaDueAt: null,
            contactName: "Acme", contactEmail: "a@acme.test");
        await events.TryRegisterAsync(new SyncEventRegistration
        {
            MspOrganizationId = Org, PsaConnectionId = Conn, TicketId = ticket.Id,
            EventType = "ticket.updated", IdempotencyKey = "portal-x", SourceMarker = SyncSource.Portal,
            PayloadHash = portalHash, OccurredAt = clock.GetUtcNow(),
        });

        // The provider now echoes that same change back — must be skipped.
        var outcome = await svc.UpsertFromProviderAsync(Conn, Incoming("500", "Printer", "1"), []);
        outcome.Should().Be(TicketSyncOutcome.SkippedEcho);
    }

    [Fact]
    public async Task A_ticket_imported_before_contacts_were_captured_gains_its_contact_on_the_next_sync()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedConnectionAsync(dbName);
        var svc = Service(db, clock, new SyncEventStore(db, clock));

        // As every existing ticket was stored: placeholders, because the connector sent no contact.
        await svc.UpsertFromProviderAsync(Conn, Incoming("600", "VPN down", "5") with { RequesterName = null, RequesterEmail = null }, []);
        var before = await db.Tickets.SingleAsync();
        Desk.Domain.Tickets.TicketContact.IsReachable(before).Should().BeFalse();

        // Nothing else about the ticket changes - only the contact now arrives. Without the contact
        // in the update hash this would short-circuit as "unchanged" and never be stored.
        var outcome = await svc.UpsertFromProviderAsync(Conn, Incoming("600", "VPN down", "5") with { RequesterName = "Priya Nair", RequesterEmail = "priya@acme.test" }, []);

        outcome.Should().Be(TicketSyncOutcome.Updated);
        var after = await db.Tickets.AsNoTracking().SingleAsync();
        (after.RequesterName, after.RequesterEmail).Should().Be(("Priya Nair", "priya@acme.test"));
        Desk.Domain.Tickets.TicketContact.IsReachable(after).Should().BeTrue();

        // And a later sync that names nobody does not wipe the contact already known.
        await svc.UpsertFromProviderAsync(Conn, Incoming("600", "VPN down", "1") with { RequesterName = null, RequesterEmail = null }, []);
        (await db.Tickets.AsNoTracking().SingleAsync()).RequesterEmail.Should().Be("priya@acme.test");
    }

    [Theory]
    [InlineData("unknown@unknown", false, false)]
    [InlineData("", false, false)]
    [InlineData("not-an-address", false, false)]
    [InlineData("priya@acme.test", false, true)]
    [InlineData("unknown@unknown", true, true)] // raised by a client portal user, who reads replies here
    public void A_public_reply_is_offered_only_when_someone_would_receive_it(string email, bool raisedInPortal, bool reachable)
    {
        var t = new Desk.Domain.Tickets.Ticket
        {
            RequesterName = "Unknown", RequesterEmail = email, Title = "t",
            RequesterUserId = raisedInPortal ? Guid.NewGuid() : null,
        };

        Desk.Domain.Tickets.TicketContact.IsReachable(t).Should().Be(reachable);
    }
}
