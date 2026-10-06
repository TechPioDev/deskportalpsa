using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Connectors.Autotask;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Connectors;

/// <summary>
/// Builds an <see cref="AutotaskConnector"/> for a connection: loads the connection row (endpoint),
/// resolves its credentials from the secret store by the stored reference, and hands the connector
/// a configured HttpClient. Raw secrets exist only for the lifetime of the built connector.
/// </summary>
public sealed class AutotaskConnectorFactory(
    DeskDbContext db,
    ISecretStore secrets,
    ProviderHttpClients clients,
    TimeProvider clock,
    ILogger<AutotaskConnectorFactory> logger) : IConnectorFactory
{
    public ProviderType Provider => ProviderType.AutotaskPsa;

    public async Task<IServiceManagementConnector> CreateAsync(Guid psaConnectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        var secret = await secrets.ReadAsync(connection.CredentialSecretRef, ct);

        var config = new AutotaskConnectorConfig
        {
            BaseUrl = EnsureTrailingSlash(connection.ApiEndpoint),
            Credentials = new AutotaskCredentials(
                ApiIntegrationCode: Require(secret, "ApiIntegrationCode"),
                UserName: Require(secret, "UserName"),
                Secret: Require(secret, "Secret")),
            WebhookSecret = secret.GetValueOrDefault("WebhookSecret", ""),
            // Time-entry defaults live on the connection, not in the secret store: they are
            // configuration an admin picks from live discovery, not credentials.
            DefaultTimeEntryResourceId = ParseId(connection.DefaultTimeEntryResourceId),
            DefaultTimeEntryRoleId = ParseId(connection.DefaultTimeEntryRoleId),
        };

        // This connection's own client: its own pace, its own retries, the shared guarded transport.
        var http = clients.For("autotask", connection.Id, config.BaseUrl, AutotaskConnector.RequestsPerMinute);

        return new AutotaskConnector(http, config, clock,
            // Field names and their option labels only — configuration, never customer content.
            (field, values) => logger.LogInformation(
                "Autotask {Field} picklist: {Values}", field, values));
    }

    private static long? ParseId(string? raw) => long.TryParse(raw, out var id) && id > 0 ? id : null;

    /// <summary>
    /// Normalizes the configured endpoint to the API root the connector expects
    /// (…/ATServicesRest/). The connector appends the version itself ("V1.0/{entity}/query"), but
    /// Autotask's own docs and the zone-lookup response show the URL WITH the version, so admins
    /// commonly paste ".../ATServicesRest/v1.0/". Accept either form and strip a trailing version
    /// segment — otherwise every call 404s on a doubled ".../v1.0/V1.0/...".
    /// </summary>
    private static string EnsureTrailingSlash(string url)
    {
        var normalized = url.TrimEnd('/');
        if (normalized.EndsWith("/v1.0", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/v1.0".Length];
        return normalized + "/";
    }

    private static string Require(IReadOnlyDictionary<string, string> secret, string key)
        => secret.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : throw new ValidationFailedException($"Autotask credential '{key}' missing from the secret store.");
}
