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
    IHttpClientFactory httpFactory,
    TimeProvider clock,
    Microsoft.Extensions.Logging.ILogger<ConnectWiseConnectorFactory> logger) : IConnectorFactory
{
    public ProviderType Provider => ProviderType.ConnectWisePsa;

    public async Task<IServiceManagementConnector> CreateAsync(Guid psaConnectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        var secret = await secrets.ReadAsync(connection.CredentialSecretRef, ct);

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

        var http = httpFactory.CreateClient("connectwise");
        http.BaseAddress = new Uri(config.BaseUrl);

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
