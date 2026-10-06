using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Connectors;

/// <summary>
/// Selects the registered <see cref="IConnectorFactory"/> for a connection's provider and builds a
/// connector. The connection lookup runs under the caller's tenant scope, so a connection from
/// another tenant is simply not found.
/// </summary>
public sealed class ConnectorResolver(DeskDbContext db, IEnumerable<IConnectorFactory> factories) : IConnectorResolver
{
    private readonly IReadOnlyDictionary<ProviderType, IConnectorFactory> _factories =
        factories.ToDictionary(f => f.Provider);

    public bool Supports(ProviderType provider) => _factories.ContainsKey(provider);

    public IReadOnlyList<ProviderDescriptor> Providers
        => _factories.Values.Select(f => f.Descriptor).OfType<ProviderDescriptor>().OrderBy(d => d.Name).ToList();

    public string? AccountKey(ProviderType provider, string apiEndpoint, IReadOnlyDictionary<string, string> credentials)
        => _factories.TryGetValue(provider, out var factory) ? factory.AccountKey(apiEndpoint, credentials) : null;

    public Task<IServiceManagementConnector> ResolveForTrialAsync(
        Desk.Domain.Tenancy.PsaConnection connection, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
        => _factories.TryGetValue(connection.Provider, out var factory)
            ? factory.CreateWithAsync(connection, credentials, ct)
            : throw new ValidationFailedException($"No connector registered for provider {connection.Provider}.");

    public async Task<IServiceManagementConnector> ResolveAsync(Guid psaConnectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        if (!connection.IsEnabled)
            throw new ValidationFailedException($"PSA connection '{connection.Name}' is disabled.");

        if (!_factories.TryGetValue(connection.Provider, out var factory))
            throw new ValidationFailedException($"No connector registered for provider {connection.Provider}.");

        try
        {
            return await factory.CreateAsync(psaConnectionId, ct);
        }
        catch (KeyNotFoundException)
        {
            // The secret store's reference genuinely doesn't resolve — most commonly a credential
            // rotation the store lost (e.g. a prior secret-backend outage). Every factory reaches
            // ISecretStore.ReadAsync through here, so this is the one place to turn that into a
            // message an admin can act on, rather than letting it fall through to the API's generic
            // "unexpected error, contact support" response — which is what a plain KeyNotFoundException
            // gets, and which sent an admin looking for a bug instead of the Edit button.
            throw new ValidationFailedException(
                $"'{connection.Name}' has no valid stored credentials — edit the connection and re-enter them.");
        }
    }
}
