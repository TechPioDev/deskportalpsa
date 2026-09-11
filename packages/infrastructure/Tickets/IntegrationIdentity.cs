using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// The PSA resource each connection writes AS - its <c>DefaultTimeEntryResourceId</c> - which is an
/// account, not a person.
///
/// Portal-only technicians' time and notes reach the PSA under it, so the provider credits that one
/// resource with the whole portal team's work, and tickets the integration touched sit "assigned" to
/// it. Named from the provider it reads as a colleague (on Techpio's Autotask, "Sudanshu Aggarwal")
/// holding tickets nobody here holds and hours the team logged. Every surface that turns a resource
/// id into a person asks this first.
///
/// Per connection: the same id on another PSA is a different resource and may well be a real person.
/// Time a PORTAL user logged is attributed to them before this is consulted, so excluding the account
/// credits no one's work to nobody - it only stops the account appearing as one of the team.
/// </summary>
public sealed class IntegrationIdentity
{
    private readonly Dictionary<Guid, string> _accountByConnection;

    private IntegrationIdentity(Dictionary<Guid, string> accountByConnection) => _accountByConnection = accountByConnection;

    public static async Task<IntegrationIdentity> LoadAsync(DeskDbContext db, CancellationToken ct = default)
        => new(await db.PsaConnections.AsNoTracking()
            .Where(c => c.DefaultTimeEntryResourceId != null && c.DefaultTimeEntryResourceId != "")
            .ToDictionaryAsync(c => c.Id, c => c.DefaultTimeEntryResourceId!.Trim(), ct));

    /// <summary>True when <paramref name="externalId"/> is the account <paramref name="connectionId"/> writes as.</summary>
    public bool IsAccount(Guid connectionId, string? externalId)
        => !string.IsNullOrWhiteSpace(externalId)
           && _accountByConnection.TryGetValue(connectionId, out var account)
           && string.Equals(account, externalId.Trim(), StringComparison.OrdinalIgnoreCase);
}
