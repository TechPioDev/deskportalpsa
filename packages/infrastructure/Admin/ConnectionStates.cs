using Desk.Domain.Enums;
using Desk.Domain.Tenancy;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// Where a connection stands, worked out from its own fields and whether a sync is running. Not
/// stored, so it cannot drift from them. The order is the order of what a person most needs to be
/// told: that it is put away, not finished, off, busy, locked out - and only then how it is doing.
/// </summary>
public static class ConnectionStates
{
    public const string Authentication = "Authentication";

    public static ConnectionState Of(PsaConnection c, bool running)
        => Of(c.InSetup, c.IsEnabled, c.ArchivedAt, c.SyncPausedAt, c.LastErrorKind, c.Status, c.LastSuccessfulSyncAt, running);

    public static ConnectionState Of(
        bool inSetup, bool isEnabled, DateTimeOffset? archivedAt, DateTimeOffset? syncPausedAt, string? lastErrorKind,
        ConnectionStatus status, DateTimeOffset? lastSuccessfulSyncAt, bool running)
    {
        if (archivedAt is not null) return ConnectionState.Archived;
        if (inSetup) return ConnectionState.Setup;
        if (!isEnabled) return ConnectionState.Disabled;
        if (running) return ConnectionState.Syncing;
        // Before paused and before degraded: nothing else about the connection matters until
        // someone enters credentials the PSA accepts.
        if (lastErrorKind == Authentication) return ConnectionState.AuthRequired;
        if (syncPausedAt is not null) return ConnectionState.Paused;
        return status switch
        {
            ConnectionStatus.Failed => ConnectionState.Error,
            ConnectionStatus.Degraded => ConnectionState.Degraded,
            ConnectionStatus.Disabled => ConnectionState.Disabled,
            _ => lastSuccessfulSyncAt is null ? ConnectionState.Connected : ConnectionState.Healthy,
        };
    }
}
