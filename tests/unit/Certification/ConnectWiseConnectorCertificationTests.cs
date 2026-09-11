using System.Net;
using Desk.Connectors.ConnectWise;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// Runs the shared certification suite against the REAL ConnectWiseConnector via an in-memory fake
/// ConnectWise server. Passing the same suite as the mock and Autotask connectors is the
/// cross-provider contract-compliance proof.
/// </summary>
public sealed class ConnectWiseConnectorCertificationTests : ConnectorCertificationSuite
{
    private const string Secret = "cw-webhook-secret";

    private ConnectWiseConnector Build(FakeConnectWiseServer server)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://cw.local/v4_6_release/apis/3.0/") };
        var config = new ConnectWiseConnectorConfig
        {
            BaseUrl = "https://cw.local/v4_6_release/apis/3.0/",
            Credentials = new ConnectWiseCredentials("acme", "pub", "priv", "client-guid"),
            WebhookSecret = Secret,
        };
        return new ConnectWiseConnector(http, config, Clock);
    }

    protected override IServiceManagementConnector CreateConnector() => Build(new FakeConnectWiseServer(Clock));

    /// <summary>
    /// The value discovery offers has to be the value tickets arrive carrying.
    ///
    /// This is the contract that quietly broke: the mapping UI saves whatever discovery gives it as
    /// the rule's external value, and the sync compares that rule against the value on an incoming
    /// ticket. Discovery offered status ids while tickets arrived carrying status names, so every
    /// rule an administrator saved was compared against something it could never equal and not one
    /// ConnectWise ticket was ever status-mapped. Nothing failed — the portal displayed the
    /// provider's raw status, which looks like a mapping that simply passed the value through.
    ///
    /// Asserted as a round trip rather than as "options are names", because the requirement is that
    /// the two sides agree, not what they agree on.
    /// </summary>
    /// <summary>A ticket sitting on the board already, as ConnectWise would return it.</summary>
    private static FakeConnectWiseServer WithExistingTicket(TimeProvider clock)
    {
        var server = new FakeConnectWiseServer(clock);
        server.SeedTicket(new Dictionary<string, object?>
        {
            ["summary"] = "Printer offline",
            ["status"] = new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "New" },
            ["priority"] = new Dictionary<string, object?> { ["id"] = 3L, ["name"] = "High" },
            ["board"] = new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Service Desk" },
        });
        return server;
    }

    [Fact]
    public async Task A_rule_saved_from_discovery_can_match_an_incoming_ticket()
    {
        var connector = Build(WithExistingTicket(Clock));

        var offered = (await connector.GetStatusesAsync()).Select(o => o.SyncValue).ToList();
        var ticket = (await connector.GetTicketsAsync(new TicketFilter())).Items.Single();

        offered.Should().Contain(ticket.Status,
            "a mapping rule is saved with the sync value and compared against the ticket's");
    }

    [Fact]
    public async Task A_board_is_filtered_by_id_and_reported_by_name()
    {
        // The case the two representations exist for. Boards feed the import filter, which becomes a
        // query condition on board/id — a name there is an invalid query, not a visible error — while
        // a synced ticket carries the board NAME. One list served both jobs before this, so whichever
        // caller lost got the wrong string.
        var connector = Build(new FakeConnectWiseServer(Clock));

        var boards = await connector.GetQueuesOrBoardsAsync();

        boards.Should().OnlyContain(o => o.Value.All(char.IsDigit), "the filter sends board/id");
        boards.Should().OnlyContain(o => !o.SyncValue.All(char.IsDigit), "a ticket carries the name");
    }

    [Fact]
    public async Task Statuses_from_every_board_are_offered_not_just_the_first()
    {
        // A status that only exists on a second board is still a status tickets arrive in, and an
        // administrator cannot map what the page never shows them.
        var connector = Build(new FakeConnectWiseServer(Clock));

        var offered = (await connector.GetStatusesAsync()).Select(o => o.Value).ToList();

        offered.Should().Contain("Scheduled", "it exists only on the second board");
        offered.Should().Contain("Closed", "the first board's statuses are still there");
        offered.Count(v => v == "New").Should().Be(1, "both boards define New; it is one option");
    }

    [Fact]
    public async Task The_same_holds_for_priority()
    {
        var connector = Build(WithExistingTicket(Clock));

        var offered = (await connector.GetPrioritiesAsync()).Select(o => o.SyncValue).ToList();
        var ticket = (await connector.GetTicketsAsync(new TicketFilter())).Items.Single();

        offered.Should().Contain(ticket.Priority);

        // The other half of the same option: what the provider is SENT stays an id, because the
        // import filter builds a query condition on it.
        (await connector.GetPrioritiesAsync()).Should().OnlyContain(o => o.Value.All(char.IsDigit));
    }

    protected override IServiceManagementConnector CreateFailingConnector(ConnectorFailureKind kind)
    {
        var status = kind switch
        {
            ConnectorFailureKind.Authentication => HttpStatusCode.Unauthorized,
            ConnectorFailureKind.PermissionDenied => HttpStatusCode.Forbidden,
            ConnectorFailureKind.RateLimited => (HttpStatusCode)429,
            _ => HttpStatusCode.InternalServerError,
        };
        return Build(new FakeConnectWiseServer(Clock) { ForceStatus = status });
    }

    protected override string SeededOrganizationId => "1";
    protected override string WebhookSecret => Secret;

    /// <summary>
    /// The live finding this pins: a customer contact's notes rendered as the MSP's own words.
    /// Side detection must follow ATTRIBUTION — whichever of member/contact supplies the display
    /// name — because CW can return an empty member stub (no name) on contact-authored notes, so
    /// checking only `member != null` claims every note for the MSP.
    /// </summary>
    [Fact]
    public async Task Note_author_side_follows_whoever_supplies_the_name()
    {
        var server = new FakeConnectWiseServer(Clock);
        server.SeedNote(777, new() // technician note: member named
        {
            ["text"] = "Working on it.",
            ["member"] = new Dictionary<string, object?> { ["id"] = 20L, ["name"] = "Tech One" },
        });
        server.SeedNote(777, new() // customer portal note: contact named, no member
        {
            ["text"] = "Still broken.",
            ["contact"] = new Dictionary<string, object?> { ["id"] = 10L, ["name"] = "Harpal Singh" },
        });
        server.SeedNote(777, new() // the live quirk: empty member stub alongside the real contact
        {
            ["text"] = "Broken again.",
            ["member"] = new Dictionary<string, object?> { ["id"] = 0L },
            ["contact"] = new Dictionary<string, object?> { ["id"] = 10L, ["name"] = "Harpal Singh" },
        });
        var c = Build(server);

        var notes = await c.GetNotesAsync("777");

        notes.Should().HaveCount(3);
        notes[0].FromClient.Should().BeFalse("a named member wrote it");
        notes[0].AuthorName.Should().Be("Tech One");
        notes[1].FromClient.Should().BeTrue("the contact is the only author CW names");
        notes[1].AuthorName.Should().Be("Harpal Singh");
        notes[2].FromClient.Should().BeTrue("an empty member stub is not an author — the named contact is");
        notes[2].AuthorName.Should().Be("Harpal Singh");

        // The author's id follows the same attribution: the member's, in the numeric form a ticket's
        // owner carries, and only when a member is the one named - never a contact's, never a stub's.
        notes[0].AuthorExternalId.Should().Be("20");
        notes[1].AuthorExternalId.Should().BeNull();
        notes[2].AuthorExternalId.Should().BeNull("the stub names nobody, so it is no author");
    }

    /// <summary>
    /// CW validates status against the ticket's BOARD on create. A mapped status is typically the
    /// verbose global name ("New (not responded)") while a board names it tersely ("New") — the
    /// connector must resolve one to the other, or every portal create on that board fails.
    /// </summary>
    [Fact]
    public async Task Create_resolves_a_verbose_status_against_the_boards_own_names()
    {
        var c = CreateConnector();
        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "board-scoped status",
            ExternalCompanyId = SeededOrganizationId,
            QueueOrBoard = "1",
            Status = "New (not responded)", // no board carries this literal name
            IdempotencyKey = "status-resolve",
        });

        created.Success.Should().BeTrue();
        (await c.GetTicketAsync(created.ExternalId!))!.Status.Should().NotBeNull();
    }

    /// <summary>
    /// A rejection whose body is not the documented {message, errors[]} shape must still reach the
    /// admin. Live ConnectWise answered a bad connection with plain text, the parser returned null,
    /// and the UI showed only "ConnectWise rejected the request (400)" — a status code the admin
    /// cannot act on, while the provider's actual reason was in hand the whole time.
    /// </summary>
    [Theory]
    [InlineData("Invalid or missing clientId")]           // plain text, not JSON at all
    [InlineData("{\"code\":\"InvalidCredentials\"}")]     // JSON, but no message/errors member
    public async Task A_rejection_body_in_an_unexpected_shape_still_reaches_the_admin(string body)
    {
        var c = Build(new FakeConnectWiseServer(Clock) { ForceStatus = HttpStatusCode.BadRequest, ForceBody = body });

        var act = async () => await c.TestConnectionAsync();

        (await act.Should().ThrowAsync<ConnectorException>())
            .Which.Message.Should().Contain(body);
    }
}
