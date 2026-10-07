using Desk.Connectors.Autotask;
using Desk.Connectors.ConnectWise;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Desk.Tests.Unit.Certification;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Sending a time entry to the PSA a second time.
///
/// A push whose answer was lost - a timeout, a dropped connection - may have been carried out. The
/// portal marked it Failed and "Retry" posted it again: two entries in the PSA, and the customer
/// billed the same hour twice. Nothing checked first. Now the PSA is asked whether it has it.
/// </summary>
public class TimeEntryRetryTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private sealed record World(AdminHarness H, Ticket Ticket, TicketTimeEntry Entry, StubConnector Connector, TicketTimeWriter Writer);

    private static async Task<World> WorldAsync(TimeEntrySyncStatus status = TimeEntrySyncStatus.Failed)
    {
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        var ticket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
            Title = "t", RequesterName = "r", RequesterEmail = "r@a.test", ExternalTicketId = "7814",
        };
        var entry = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, Hours = 1m, Billable = true, Notes = "Replaced the switch",
            Source = TimeEntrySource.Portal, SyncStatus = status, SyncError = status == TimeEntrySyncStatus.Failed ? "Autotask request timed out." : null,
            EntryDate = h.Clock.GetUtcNow(), TechnicianExternalId = "29682887",
        };
        // Another hour on the same ticket that is already in the PSA: not a candidate for this one.
        var other = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, Hours = 1m, Billable = true, Notes = "Replaced the switch",
            Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced, ExternalEntryId = "7001", EntryDate = h.Clock.GetUtcNow(),
        };
        h.Db.AddRange(ticket, entry, other);
        await h.Db.SaveChangesAsync();
        var writer = new TicketTimeWriter(h.Db, null!, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock));
        return new World(h, ticket, entry, new StubConnector(), writer);
    }

    [Fact]
    public async Task A_retried_entry_the_PSA_already_has_is_linked_not_sent_again()
    {
        // The scenario as it was put: the portal sends 60 minutes, Autotask stores it as 7282, the
        // answer never arrives. The next attempt must recognise 7282 as the same entry.
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        w.Connector.TimeEntryAlreadyThere = "7282";

        var pushed = await w.Writer.PushAsync(w.Entry, w.Ticket, w.Connector, default);

        pushed.Should().BeTrue();
        w.Connector.PushedTime.Should().BeEmpty("it is already there; sending it again is the duplicate");
        var row = await w.H.Db.TicketTimeEntries.AsNoTracking().SingleAsync(t => t.Id == w.Entry.Id);
        (row.ExternalEntryId, row.SyncStatus, row.SyncError).Should().Be(("7282", TimeEntrySyncStatus.Synced, null));
        (await w.H.Db.AuditLog.SingleAsync(a => a.Action == "ticket.time.reconciled")).DetailJson.Should().Contain("7282");
    }

    [Fact]
    public async Task The_PSA_is_asked_about_this_entry_since_it_was_first_tried_leaving_out_what_is_already_linked()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        w.Connector.TimeEntryAlreadyThere = "7282";

        await w.Writer.PushAsync(w.Entry, w.Ticket, w.Connector, default);

        var (ticketId, asked, since, linked) = w.Connector.TimeEntryLookups.Single();
        ticketId.Should().Be("7814");
        (asked.Hours, asked.Notes, asked.MemberIdentifier, asked.Billable).Should().Be((1m, "Replaced the switch", "29682887", BillableOption.Billable));
        since.Should().Be(w.Entry.CreatedAt, "nothing in the PSA from before the portal first tried can be this entry");
        linked.Should().Equal(["7001"], "an entry that already belongs to another portal hour is not this one");
    }

    [Fact]
    public async Task A_retried_entry_the_PSA_does_not_have_is_sent()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        w.Connector.NextTimeEntryResult = new CreateTimeEntryResult(true, "7300", null);

        var pushed = await w.Writer.PushAsync(w.Entry, w.Ticket, w.Connector, default);

        pushed.Should().BeTrue();
        w.Connector.TimeEntryLookups.Should().ContainSingle();
        w.Connector.PushedTime.Should().ContainSingle();
        (await w.H.Db.TicketTimeEntries.AsNoTracking().SingleAsync(t => t.Id == w.Entry.Id)).ExternalEntryId.Should().Be("7300");
    }

    [Fact]
    public async Task When_the_PSA_cannot_be_asked_nothing_is_sent()
    {
        // Not knowing is not the same as "it is not there". A duplicate on an invoice is worse
        // than an hour that waits until the PSA answers.
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        w.Connector.FindTimeEntryFailure = new ConnectorException(ConnectorFailureKind.Timeout, "Autotask request timed out.");
        w.Connector.NextTimeEntryResult = new CreateTimeEntryResult(true, "7300", null);

        var pushed = await w.Writer.PushAsync(w.Entry, w.Ticket, w.Connector, default);

        pushed.Should().BeFalse();
        w.Connector.PushedTime.Should().BeEmpty();
        var row = await w.H.Db.TicketTimeEntries.AsNoTracking().SingleAsync(t => t.Id == w.Entry.Id);
        (row.SyncStatus, row.ExternalEntryId).Should().Be((TimeEntrySyncStatus.Failed, null));
        row.SyncError.Should().Contain("not sent again");
    }

    [Fact]
    public async Task A_first_push_is_sent_without_asking()
    {
        // Nothing has been tried yet, so there is nothing it could already be.
        var w = await WorldAsync(TimeEntrySyncStatus.Pending);
        await using var _ = w.H.Db;
        w.Connector.NextTimeEntryResult = new CreateTimeEntryResult(true, "7300", null);

        await w.Writer.PushAsync(w.Entry, w.Ticket, w.Connector, default);

        w.Connector.TimeEntryLookups.Should().BeEmpty();
        w.Connector.PushedTime.Should().ContainSingle();
    }

    // ---- what each connector recognises as its own ---------------------------------------------

    private static readonly UnifiedTimeEntryCreateRequest AnHour =
        new(1m, null, null, BillableOption.Billable, "Replaced the switch", null);

    private static async Task<(AutotaskConnector Connector, FakeAutotaskServer Server, string Ticket, TestClock Clock)> AutotaskAsync()
    {
        var clock = new TestClock();
        var server = new FakeAutotaskServer(clock);
        var c = new AutotaskConnector(new HttpClient(server) { BaseAddress = new Uri("https://at.local/ATServicesRest/") },
            new AutotaskConnectorConfig
            {
                BaseUrl = "https://at.local/ATServicesRest/",
                Credentials = new AutotaskCredentials("code", "user", "secret"),
                DefaultTimeEntryResourceId = 20, DefaultTimeEntryRoleId = 55,
            }, clock);
        var ticket = (await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "time", IdempotencyKey = "k", ExternalCompanyId = "1" })).ExternalId!;
        return (c, server, ticket, clock);
    }

    [Fact]
    public async Task Autotask_recognises_the_entry_an_unanswered_push_left_behind()
    {
        var (c, _, ticket, clock) = await AutotaskAsync();
        var firstTried = clock.GetUtcNow();
        var created = await c.AddTimeEntryAsync(ticket, AnHour);      // carried out; imagine the answer never arrived

        (await c.FindTimeEntryAsync(ticket, AnHour, firstTried, [])).Should().Be(created.ExternalId);
        // With no notes, Autotask is sent a placeholder: the same request is still recognised.
        var bare = AnHour with { Notes = null };
        var createdBare = await c.AddTimeEntryAsync(ticket, bare);
        (await c.FindTimeEntryAsync(ticket, bare, firstTried, [created.ExternalId!])).Should().Be(createdBare.ExternalId);
    }

    [Fact]
    public async Task Autotask_does_not_mistake_another_entry_for_it()
    {
        var (c, _, ticket, clock) = await AutotaskAsync();
        var created = await c.AddTimeEntryAsync(ticket, AnHour);
        var since = clock.GetUtcNow();

        (await c.FindTimeEntryAsync(ticket, AnHour with { Hours = 2m }, since, [])).Should().BeNull("different hours");
        (await c.FindTimeEntryAsync(ticket, AnHour with { Notes = "Something else" }, since, [])).Should().BeNull("different notes");
        (await c.FindTimeEntryAsync(ticket, AnHour with { Billable = BillableOption.DoNotBill }, since, [])).Should().BeNull("charged differently");
        (await c.FindTimeEntryAsync(ticket, AnHour, since, [created.ExternalId!])).Should().BeNull("it already belongs to another portal hour");
        (await c.FindTimeEntryAsync(ticket, AnHour, since.AddHours(3), [])).Should().BeNull("it was in the PSA before the portal first tried");
        (await c.FindTimeEntryAsync("999999", AnHour, since, [])).Should().BeNull("another ticket");
    }

    [Fact]
    public async Task ConnectWise_recognises_the_entry_an_unanswered_push_left_behind_and_no_other()
    {
        var clock = new TestClock();
        var server = new FakeConnectWiseServer(clock);
        var c = new ConnectWiseConnector(new HttpClient(server) { BaseAddress = new Uri("https://cw.local/v4_6_release/apis/3.0/") },
            new ConnectWiseConnectorConfig
            {
                BaseUrl = "https://cw.local/v4_6_release/apis/3.0/",
                Credentials = new ConnectWiseCredentials("acme", "pub", "priv", "client-guid"),
            }, clock);
        var firstTried = clock.GetUtcNow();
        var created = await c.AddTimeEntryAsync("77", AnHour);

        (await c.FindTimeEntryAsync("77", AnHour, firstTried, [])).Should().Be(created.ExternalId);
        (await c.FindTimeEntryAsync("77", AnHour with { Hours = 2m }, firstTried, [])).Should().BeNull();
        (await c.FindTimeEntryAsync("77", AnHour with { Billable = BillableOption.NoCharge }, firstTried, [])).Should().BeNull();
        (await c.FindTimeEntryAsync("77", AnHour, firstTried, [created.ExternalId!])).Should().BeNull();
        (await c.FindTimeEntryAsync("77", AnHour, firstTried.AddHours(1), [])).Should().BeNull();
        (await c.FindTimeEntryAsync("78", AnHour, firstTried, [])).Should().BeNull();
    }
}
