using Desk.Connectors.Mock;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit.Certification;

/// <summary>Runs the shared certification suite against the reference mock connector.</summary>
public sealed class MockConnectorCertificationTests : ConnectorCertificationSuite
{
    private readonly MockConnectorOptions _options = new();

    protected override IServiceManagementConnector CreateConnector() => new MockConnector(_options, Clock);

    protected override IServiceManagementConnector CreateFailingConnector(ConnectorFailureKind kind)
        => new MockConnector(new MockConnectorOptions { FailEveryCallWith = kind }, Clock);

    protected override string SeededOrganizationId => "ORG-1";
    protected override string WebhookSecret => _options.WebhookSecret;

    protected override IServiceManagementConnector CreateConnectorWithoutWebhookSecret()
        => new MockConnector(new MockConnectorOptions { WebhookSecret = "" }, Clock);

    [Fact]
    public async Task Mock_create_is_idempotent_on_key()
    {
        var c = CreateConnector();
        var req = new UnifiedTicketCreateRequest { Title = "Dup", ExternalCompanyId = "ORG-1", IdempotencyKey = "same" };
        (await c.CreateTicketAsync(req)).ExternalId
            .Should().Be((await c.CreateTicketAsync(req)).ExternalId);
    }
}
