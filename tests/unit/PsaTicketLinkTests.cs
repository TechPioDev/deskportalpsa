using Desk.Application.Tickets;
using Desk.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A link back to the PSA exists so someone can verify a note or a time entry at source. That makes
/// a confidently wrong link worse than none: it sends a technician to check a record that is not
/// the one in front of them. These tests care as much about what is refused as what is built.
/// </summary>
public class PsaTicketLinkTests
{
    [Fact]
    public void ConnectWise_points_at_the_web_router_on_the_matching_site()
    {
        var url = PsaTicketLink.For(
            ProviderType.ConnectWisePsa, "https://api-na.myconnectwise.net/v4_6_release/apis/3.0/", "548", "acme-msp");

        // The API host is the "api-" prefixed twin of the UI host, and the router lives under the
        // same release segment the endpoint names. The company name is what the router routes by:
        // without it ConnectWise answers "Cannot route blank company name".
        url.Should().Be(
            "https://na.myconnectwise.net/v4_6_release/services/system_io/Service/fv_sr100_request.rails?service_recid=548&companyName=acme-msp");
    }

    [Fact]
    public void A_self_hosted_ConnectWise_serves_both_from_one_host()
    {
        var url = PsaTicketLink.For(
            ProviderType.ConnectWisePsa, "https://psa.example.com/v4_6_release/apis/3.0/", "17", "acme");

        url.Should().StartWith("https://psa.example.com/v4_6_release/services/system_io/Service/fv_sr100_request.rails");
    }

    [Fact]
    public void Autotask_maps_the_api_zone_to_the_matching_ui_zone()
    {
        var url = PsaTicketLink.For(
            ProviderType.AutotaskPsa, "https://webservices31.autotask.net/ATServicesRest/v1.0/", "7807");

        // Verbatim the form Autotask itself produces for a ticket, confirmed against a live zone-31
        // instance. Zones are not interchangeable — a ticket in 31 is not reachable through 1.
        url.Should().Be(
            "https://ww31.autotask.net/Mvc/ServiceDesk/TicketDetail.mvc?workspace=False&ids%5B0%5D=7807&ticketId=7807");
    }

    [Theory]
    [InlineData(ProviderType.AutotaskPsa, "https://webservices.autotask.net/atservicesrest/")]   // no zone number
    [InlineData(ProviderType.AutotaskPsa, "https://something-else.example.com/api/")]            // unfamiliar host
    [InlineData(ProviderType.ConnectWisePsa, "https://api-na.myconnectwise.net/apis/3.0/")]      // no release segment
    [InlineData(ProviderType.ConnectWisePsa, "not-a-url")]
    public void An_endpoint_we_cannot_map_yields_no_link_rather_than_a_guess(ProviderType provider, string endpoint)
    {
        PsaTicketLink.For(provider, endpoint, "548").Should().BeNull();
    }

    [Theory]
    [InlineData(null, "548")]
    [InlineData("https://webservices2.autotask.net/atservicesrest/", null)]
    [InlineData("https://webservices2.autotask.net/atservicesrest/", "   ")]
    public void Nothing_is_built_without_both_an_endpoint_and_a_ticket_id(string? endpoint, string? id)
    {
        PsaTicketLink.For(ProviderType.AutotaskPsa, endpoint, id).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ConnectWise_without_a_company_name_yields_no_link_rather_than_one_that_cannot_route(string? company)
    {
        // Reported from production: the link opened "Cannot route blank company name".
        PsaTicketLink.For(ProviderType.ConnectWisePsa, "https://api-na.myconnectwise.net/v4_6_release/apis/3.0/", "553", company)
            .Should().BeNull();
    }

    [Fact]
    public void A_provider_with_no_known_web_ui_yields_no_link()
    {
        // Reserved ProviderType slots have no connector and no UI convention to rely on.
        PsaTicketLink.For(ProviderType.HaloPsa, "https://halo.example.com/api/", "548").Should().BeNull();
    }

    [Fact]
    public void A_ticket_reference_is_escaped_rather_than_concatenated()
    {
        var url = PsaTicketLink.For(
            ProviderType.AutotaskPsa, "https://webservices2.autotask.net/atservicesrest/", "T2026 0805.0001");

        url.Should().NotContain(" ");
        url.Should().EndWith("ticketId=T2026%200805.0001");
    }
}
