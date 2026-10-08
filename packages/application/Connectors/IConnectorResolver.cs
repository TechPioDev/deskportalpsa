using Desk.PsaCore.Contracts;

namespace Desk.Application.Connectors;

/// <summary>
/// Resolves a ready-to-use connector for a PSA connection: looks up the connection (tenant-scoped),
/// selects the factory registered for its provider, and builds a connector bound to it. Adding a
/// provider means registering one more <see cref="IConnectorFactory"/> — no change here or in callers.
/// </summary>
public interface IConnectorResolver
{
    Task<IServiceManagementConnector> ResolveAsync(Guid psaConnectionId, CancellationToken ct = default);

    /// <summary>
    /// Whether a connector exists for this provider. More PSAs are named than can be connected
    /// today, and a connection to one of the others could never sync.
    /// </summary>
    bool Supports(Desk.Domain.Enums.ProviderType provider) => true;

    /// <summary>
    /// A connector for trying a connection out, as it is GIVEN: the address on
    /// <paramref name="connection"/>, saved or not, with <paramref name="credentials"/> rather than
    /// the stored ones, whether or not the connection is switched on. For testing a connection
    /// that is still being set up, and for trying a new address or new credentials before either
    /// replaces one that works.
    /// </summary>
    Task<IServiceManagementConnector> ResolveForTrialAsync(
        Desk.Domain.Tenancy.PsaConnection connection, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
        => ResolveAsync(connection.Id, ct);

    /// <summary>The PSAs that can be connected, as their connectors describe themselves.</summary>
    IReadOnlyList<ProviderDescriptor> Providers => [];

    /// <summary>A name for the PSA account a connection would reach, or null when the provider cannot say.</summary>
    string? AccountKey(Desk.Domain.Enums.ProviderType provider, string apiEndpoint, IReadOnlyDictionary<string, string> credentials) => null;
}
