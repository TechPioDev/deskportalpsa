using Microsoft.Extensions.Logging;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Connectors.ConnectWise;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Connectors;

/// <summary>
/// Builds a <see cref="ConnectWiseConnector"/> for a connection, resolving its API keys from the
/// secret store. Raw keys live only for the built connector's lifetime.
/// </summary>
public sealed class ConnectWiseConnectorFactory(
    DeskDbContext db,
    ISecretStore secrets,
    ProviderHttpClients clients,
    TimeProvider clock,
    Microsoft.Extensions.Logging.ILogger<ConnectWiseConnectorFactory> logger) : IConnectorFactory
{
    public ProviderType Provider => ProviderType.ConnectWisePsa;

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderType.ConnectWisePsa, "ConnectWise PSA",
        EndpointExample: "https://api-na.myconnectwise.net/v4_6_release/apis/3.0/",
        EndpointHint: "Your ConnectWise API base, starting with https:// and ending in /apis/3.0/.",
        [
            new CredentialField("CompanyId", "Company ID", Secret: false, "The company you sign in to ConnectWise with."),
            new CredentialField("PublicKey", "Public key", Secret: true, "An API member's public key."),
            new CredentialField("PrivateKey", "Private key", Secret: true),
            new CredentialField("ClientId", "Client ID", Secret: true, "Your ConnectWise developer clientId."),
        ]);

    public async Task<IServiceManagementConnector> CreateAsync(Guid psaConnectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        return Build(connection, await secrets.ReadAsync(connection.CredentialSecretRef, ct));
    }

    public Task<IServiceManagementConnector> CreateWithAsync(
        Desk.Domain.Tenancy.PsaConnection connection, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
        => Task.FromResult(Build(connection, credentials));

    /// <summary>The instance and the company: two connections with both in common reach the same ConnectWise account.</summary>
    public string? AccountKey(string apiEndpoint, IReadOnlyDictionary<string, string> credentials)
        => Uri.TryCreate(apiEndpoint, UriKind.Absolute, out var uri) && credentials.TryGetValue("CompanyId", out var company) && !string.IsNullOrWhiteSpace(company)
            ? $"{uri.IdnHost}|{company.Trim()}".ToLowerInvariant()
            : null;

    private IServiceManagementConnector Build(Desk.Domain.Tenancy.PsaConnection connection, IReadOnlyDictionary<string, string> secret)
    {
        var config = new ConnectWiseConnectorConfig
        {
            BaseUrl = EnsureTrailingSlash(connection.ApiEndpoint),
            Credentials = new ConnectWiseCredentials(
                CompanyId: Require(secret, "CompanyId"),
                PublicKey: Require(secret, "PublicKey"),
                PrivateKey: Require(secret, "PrivateKey"),
                ClientId: Require(secret, "ClientId")),
            WebhookSecret = secret.GetValueOrDefault("WebhookSecret", ""),
        };

        // This connection's own client: its own pace, its own retries, the shared guarded transport.
        var http = clients.For("connectwise", connection.Id, config.BaseUrl, ConnectWiseConnector.RequestsPerMinute);

        return new ConnectWiseConnector(http, config, clock,
            // Field names only — never values — so the log carries no customer data. Once per
            // process, and the one place a silently-missing provider field becomes visible.
            (fields, infoFields) => logger.LogInformation(
                "ConnectWise ticket fields: {Fields} | _info: {InfoFields}", fields, infoFields),
            // Status names and counts only — configuration, never customer content.
            closure => logger.LogInformation("ConnectWise status closure: {Closure}", closure));
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private static string Require(IReadOnlyDictionary<string, string> secret, string key)
        => secret.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : throw new ValidationFailedException($"ConnectWise credential '{key}' missing from the secret store.");
}
