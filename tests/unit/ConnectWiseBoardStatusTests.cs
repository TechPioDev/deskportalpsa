using Desk.Connectors.ConnectWise;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Application.Mapping;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Desk.Tests.Unit.Certification;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using BoardStatus = Desk.Tests.Unit.Certification.FakeConnectWiseServer.BoardStatus;

namespace Desk.Tests.Unit;

/// <summary>
/// A status asked for on a ConnectWise ticket is found on the ticket's own board.
///
/// In ConnectWise every board has its own statuses with its own ids. A mapping made by id says
/// "status 17" for every ticket of the connection, and 17 exists on one board. On 13 October 2026
/// the owner closed ticket 563 from the portal: its note and its hour were saved, and the status
/// was refused, because 563 is on the board "Techpio" and 17 belongs to "NOC".
///
/// What these hold, against a server as strict about a status id as the real one: an id from
/// another board closes the ticket with this board's own closing status; where it is not a closing
/// status it is found by its name; an id the board has is used as it is, and no other board is
/// read; an id no board has, and a status with no counterpart here, are refused with the board's
/// own list and nothing is sent; a status switched off on the board is passed over; a server that
/// sends no flags is still answered by name; and the portal keeps the name the PSA now holds, not
/// the number it was asked by.
/// </summary>
public sealed class ConnectWiseBoardStatusTests
{
    private const long Noc = 3, Techpio = 8;
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed record Kit(FakeConnectWiseServer Server, ConnectWiseConnector Cw, long TicketId);

    /// <summary>Two boards as production has them: the ids the mapping names live on NOC, the ticket is on Techpio.</summary>
    private static Kit Build(params BoardStatus[] techpio)
    {
        var clock = new TestClock();
        var server = new FakeConnectWiseServer(clock);
        server.SetBoard(Noc, "NOC",
            new BoardStatus(16, "New", Closes: false), new BoardStatus(7, "InProgress", Closes: false),
            new BoardStatus(36, "Waiting on vendor", Closes: false), new BoardStatus(17, "Completed", Closes: true));
        server.SetBoard(Techpio, "Techpio", techpio.Length > 0 ? techpio :
        [
            new BoardStatus(40, "New", Closes: false), new BoardStatus(41, "In Progress", Closes: false),
            new BoardStatus(42, "Signed off", Closes: true),
        ]);
        server.SeedTicket(new Dictionary<string, object?>
        {
            ["id"] = 563L, ["summary"] = "test3",
            ["board"] = new Dictionary<string, object?> { ["id"] = Techpio, ["name"] = "Techpio" },
            ["status"] = new Dictionary<string, object?> { ["id"] = 40L, ["name"] = "New" },
        });
        var cw = new ConnectWiseConnector(new HttpClient(server) { BaseAddress = new Uri("https://cw.local/v4_6_release/apis/3.0/") },
            new ConnectWiseConnectorConfig
            {
                BaseUrl = "https://cw.local/v4_6_release/apis/3.0/",
                Credentials = new ConnectWiseCredentials("acme", "pub", "priv", "client-guid"),
            }, clock);
        return new Kit(server, cw, 563);
    }

    private static Task<UpdateTicketResult> SetAsync(Kit k, string status)
        => k.Cw.UpdateTicketAsync("563", new UnifiedTicketUpdate { Status = status, IdempotencyKey = Guid.NewGuid().ToString("N") });

    private static bool Patched(Kit k) => k.Server.Paths.Any(p => p.StartsWith("PATCH "));

    [Fact]
    public async Task An_id_from_another_board_closes_the_ticket_with_this_boards_own_closing_status()
    {
        var k = Build();

        var result = await SetAsync(k, "17");

        result.Success.Should().BeTrue();
        k.Server.StatusIdOf(k.TicketId).Should().Be(42, "17 is NOC's Completed, which closes a ticket; Techpio's status that closes one is 42");
        result.AppliedStatus.Should().Be("Signed off");
    }

    [Fact]
    public async Task An_id_from_another_board_that_does_not_close_is_found_here_by_its_name()
    {
        var k = Build();

        var result = await SetAsync(k, "7");

        k.Server.StatusIdOf(k.TicketId).Should().Be(41, "NOC's 7 is InProgress, and Techpio calls the same thing In Progress");
        result.AppliedStatus.Should().Be("In Progress");
    }

    [Fact]
    public async Task An_id_this_board_has_is_used_as_it_is_and_no_other_board_is_read()
    {
        var k = Build();

        var result = await SetAsync(k, "41");

        k.Server.StatusIdOf(k.TicketId).Should().Be(41);
        result.AppliedStatus.Should().Be("In Progress");
        k.Server.Paths.Should().NotContain("GET service/boards", "the other boards are read only when the id is not on this one");
        k.Server.Paths.Should().NotContain($"GET service/boards/{Noc}/statuses");
    }

    [Fact]
    public async Task An_id_no_board_has_is_refused_with_this_boards_list_and_nothing_is_sent()
    {
        var k = Build();

        var act = () => SetAsync(k, "999");

        var refused = (await act.Should().ThrowAsync<ConnectorException>()).Which;
        refused.Kind.Should().Be(ConnectorFailureKind.InvalidRequest);
        refused.Message.Should().Contain("Status id 999 does not exist").And.Contain("New, In Progress, Signed off");
        Patched(k).Should().BeFalse();
        k.Server.StatusIdOf(k.TicketId).Should().Be(40);
    }

    [Fact]
    public async Task A_status_with_no_counterpart_on_this_board_is_refused_and_not_guessed_at()
    {
        var k = Build();

        var act = () => SetAsync(k, "36");

        var refused = (await act.Should().ThrowAsync<ConnectorException>()).Which;
        refused.Message.Should().Contain("No status matching 'Waiting on vendor'").And.Contain("New, In Progress, Signed off");
        Patched(k).Should().BeFalse("a status that only looks close would move the ticket somewhere nobody asked for");
    }

    [Fact]
    public async Task A_status_switched_off_on_the_board_is_passed_over()
    {
        // "Completed (old)" matches the name NOC's 17 carries, and is switched off. ConnectWise
        // would refuse it; the board's working closing status is the one to use.
        var k = Build(
            new BoardStatus(40, "New", Closes: false),
            new BoardStatus(50, "Completed (old)", Closes: true, Inactive: true),
            new BoardStatus(42, "Signed off", Closes: true, Inactive: false));

        var result = await SetAsync(k, "17");

        k.Server.StatusIdOf(k.TicketId).Should().Be(42);
        result.AppliedStatus.Should().Be("Signed off");
    }

    [Fact]
    public async Task A_server_that_says_nothing_about_which_status_closes_is_still_answered_by_name()
    {
        // No closedStatus on either board: an older or a trimmed answer. The names still say enough.
        var clock = new TestClock();
        var k = Build(new BoardStatus(40, "New"), new BoardStatus(43, "Closed - done"));
        k.Server.SetBoard(Noc, "NOC", new BoardStatus(16, "New"), new BoardStatus(17, "Completed"));

        var result = await SetAsync(k, "17");

        k.Server.StatusIdOf(k.TicketId).Should().Be(43, "Completed is one of the words for done, and so is Closed");
        result.AppliedStatus.Should().Be("Closed - done");
        _ = clock;
    }

    [Fact]
    public async Task A_request_to_close_by_name_uses_the_boards_closing_status_when_no_name_fits()
    {
        var k = Build();

        var result = await SetAsync(k, "CLOSED");

        k.Server.StatusIdOf(k.TicketId).Should().Be(42);
        result.AppliedStatus.Should().Be("Signed off");
        k.Server.Paths.Should().NotContain("GET service/boards", "a name needs no other board");
    }

    [Fact]
    public async Task A_name_this_board_has_is_matched_as_before()
    {
        var k = Build();

        (await SetAsync(k, "IN_PROGRESS")).AppliedStatus.Should().Be("In Progress");
        k.Server.StatusIdOf(k.TicketId).Should().Be(41);
    }

    // ---- through the portal: the mapping by id that production has ------------------------------

    [Fact]
    public async Task Closing_from_the_portal_with_a_mapping_by_id_closes_the_ticket_and_keeps_the_PSAs_own_word()
    {
        var k = Build();
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "CWM", Provider = ProviderType.ConnectWisePsa,
            ApiEndpoint = "https://cw.local/v4_6_release/apis/3.0/", CredentialSecretRef = "mem://cw", IsEnabled = true,
        });
        h.Db.FieldMappings.Add(new FieldMapping
        {
            MspOrganizationId = Org, Provider = ProviderType.ConnectWisePsa, Scope = MappingScope.ConnectionOverride, PsaConnectionId = Conn,
            PortalField = "status", ExternalField = "status", PortalValue = "CLOSED", ExternalValue = "17", Direction = MappingDirection.Bidirectional,
        });
        var ticket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.ConnectWisePsa, Origin = TicketOrigin.Psa,
            ExternalTicketId = "563", Title = "test3", RequesterName = "Training Admin1", RequesterEmail = "admin@xyz.test",
            QueueOrBoard = "Techpio", PortalStatus = "NEW", PsaStatus = "New", PortalPriority = "NORMAL", SyncStatus = TicketSyncStatus.Synced,
        };
        h.Db.Tickets.Add(ticket);
        await h.Db.SaveChangesAsync();

        var status = await new TicketStatusWriter(h.Db, new Resolver(k.Cw), new MappingEngine()).SetAsync(ticket, "CLOSED");

        status.Should().Be("CLOSED");
        k.Server.StatusIdOf(k.TicketId).Should().Be(42);
        var saved = await h.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id);
        (saved.PortalStatus, saved.PsaStatus).Should().Be(("CLOSED", "Signed off"), "not the number the mapping asked by");
    }
}
