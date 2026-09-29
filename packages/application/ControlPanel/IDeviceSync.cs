namespace Desk.Application.ControlPanel;

/// <summary>What one device sync did: rows it added and refreshed, rows the PSA stopped listing, and
/// tickets it could newly point at their device.</summary>
public sealed record DeviceSyncResult(int Created, int Updated, int Retired, int TicketsLinked, int CompaniesFailed)
{
    public static DeviceSyncResult operator +(DeviceSyncResult a, DeviceSyncResult b) => new(
        a.Created + b.Created, a.Updated + b.Updated, a.Retired + b.Retired,
        a.TicketsLinked + b.TicketsLinked, a.CompaniesFailed + b.CompaniesFailed);

    public static readonly DeviceSyncResult None = new(0, 0, 0, 0, 0);
}

/// <summary>Brings each client's devices in from the PSA: Autotask configuration items, ConnectWise
/// configurations.</summary>
public interface IDeviceSyncService
{
    /// <summary>Every active client of one connection. A client whose devices cannot be read is counted
    /// and skipped, not allowed to stop the rest.</summary>
    Task<DeviceSyncResult> SyncConnectionAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>One client, on demand. Errors are the caller's to show.</summary>
    Task<DeviceSyncResult> SyncCompanyAsync(Guid clientCompanyId, CancellationToken ct = default);
}

/// <summary>Runs the device sync for every enabled connection of every organization.</summary>
public interface IDeviceSyncRunner
{
    Task<DeviceSyncResult> RunAllAsync(CancellationToken ct = default);
}
