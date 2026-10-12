using Desk.Connectors.Autotask;
using Desk.Connectors.ConnectWise;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// Proves cross-provider normalization: given an equivalent ticket in Autotask and ConnectWise,
/// both connectors produce a <see cref="UnifiedTicket"/> with the same portal-neutral shape, despite
/// wholly different wire formats (flat fields + numeric picklists vs nested {id,name} references,
/// "queue" vs "service board", "resource" vs "member"). This is the payoff of the connector layer.
/// </summary>
public class CrossProviderNormalizationTests
{
    private readonly TestClock _clock = new();

    private AutotaskConnector Autotask() =>
        new(new HttpClient(new FakeAutotaskServer(_clock)) { BaseAddress = new Uri("https://at.local/atservicesrest/") },
            new AutotaskConnectorConfig { BaseUrl = "https://at.local/atservicesrest/", Credentials = new("c", "u", "s") },
            _clock);

    private ConnectWiseConnector ConnectWise() =>
        new(new HttpClient(new FakeConnectWiseServer(_clock)) { BaseAddress = new Uri("https://cw.local/v4_6_release/apis/3.0/") },
            new ConnectWiseConnectorConfig { BaseUrl = "https://cw.local/v4_6_release/apis/3.0/", Credentials = new("a", "p", "k", "g") },
            _clock);

    [Fact]
    public async Task Both_providers_yield_the_same_unified_ticket_shape()
    {
        var at = Autotask();
        var cw = ConnectWise();

        var atCreated = await at.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Email down", ExternalCompanyId = "1", Priority = "High", IdempotencyKey = "a",
        });
        var cwCreated = await cw.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Email down", ExternalCompanyId = "1", Priority = "High", IdempotencyKey = "c",
        });

        var atTicket = await at.GetTicketAsync(atCreated.ExternalId!);
        var cwTicket = await cw.GetTicketAsync(cwCreated.ExternalId!);

        // Same normalized shape from two very different wire formats.
        atTicket!.Title.Should().Be(cwTicket!.Title).And.Be("Email down");
        atTicket.Priority.Should().Be(cwTicket.Priority).And.Be("High");
        atTicket.RequesterExternalId.Should().Be(cwTicket.RequesterExternalId).And.Be("1");
        atTicket.ExternalId.Should().NotBeNullOrEmpty();
        cwTicket.ExternalId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Both_providers_expose_queue_options_under_one_abstraction()
    {
        // Autotask "queue" and ConnectWise "service board" both surface as portal queues.
        (await Autotask().GetQueuesOrBoardsAsync()).Should().NotBeEmpty();
        (await ConnectWise().GetQueuesOrBoardsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Both_providers_report_capabilities()
    {
        var atCaps = await Autotask().GetCapabilitiesAsync();
        var cwCaps = await ConnectWise().GetCapabilitiesAsync();
        atCaps.SupportsQueues.Should().BeTrue();
        cwCaps.SupportsQueues.Should().BeTrue();
        // ConnectWise supports outbound webhooks (callbacks); Autotask does not — capability model captures the difference.
        cwCaps.SupportsOutboundWebhooks.Should().BeTrue();
        atCaps.SupportsOutboundWebhooks.Should().BeFalse();
    }

    /// <summary>
    /// A capability is what the connector does today, because the screens offer what it says. Both
    /// said "inbound webhooks" while understanding only the portal's own signed frame, which neither
    /// PSA can send; ConnectWise said "custom fields" and read none.
    /// </summary>
    [Fact]
    public async Task A_capability_is_claimed_only_where_the_connector_does_the_thing()
    {
        var atCaps = await Autotask().GetCapabilitiesAsync();
        var cwCaps = await ConnectWise().GetCapabilitiesAsync();

        (atCaps.SupportsInboundWebhooks, cwCaps.SupportsInboundWebhooks).Should().Be((false, false),
            "no provider-native callback is understood until the webhook slice");
        // Both read a ticket's own fields. ConnectWise reads which fields there are off a ticket,
        // so a tenant with no ticket yet lists none.
        (atCaps.SupportsCustomFields, cwCaps.SupportsCustomFields).Should().Be((true, true));
        (await ConnectWise().GetCustomFieldsAsync()).Should().BeEmpty();
        (await Autotask().GetCustomFieldsAsync()).Should().NotBeEmpty();
    }
}
