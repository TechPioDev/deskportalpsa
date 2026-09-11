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
    private readonly Dictionary<Guid, string> _connectionName;

    private IntegrationIdentity(Dictionary<Guid, string> accountByConnection, Dictionary<Guid, string> connectionName)
    {
        _accountByConnection = accountByConnection;
        _connectionName = connectionName;
    }

    public static async Task<IntegrationIdentity> LoadAsync(DeskDbContext db, CancellationToken ct = default)
    {
        var rows = await db.PsaConnections.AsNoTracking()
            .Where(c => c.DefaultTimeEntryResourceId != null && c.DefaultTimeEntryResourceId != "")
            .Select(c => new { c.Id, Account = c.DefaultTimeEntryResourceId!, c.Name })
            .ToListAsync(ct);
        return new(rows.ToDictionary(r => r.Id, r => r.Account.Trim()), rows.ToDictionary(r => r.Id, r => r.Name));
    }

    /// <summary>True when <paramref name="externalId"/> is the account <paramref name="connectionId"/> writes as.</summary>
    public bool IsAccount(Guid connectionId, string? externalId)
        => !string.IsNullOrWhiteSpace(externalId)
           && _accountByConnection.TryGetValue(connectionId, out var account)
           && string.Equals(account, externalId.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A note's byline. The name as stored - unless the note's AUTHOR ID is the account, which
    /// makes it the integration's note whatever the account happens to be called ("Autotask
    /// integration"). Decided on the id alone: a note stored before ids were kept keeps its name
    /// until the sync fills the id in, rather than being relabelled on a guess from the name.
    /// </summary>
    public string NoteAuthor(Guid connectionId, string? authorExternalId, string authorName)
        => IsAccount(connectionId, authorExternalId) ? $"{_connectionName[connectionId]} integration" : authorName;

    /// <summary>
    /// An attachment's byline, on the note's rule. Null stays null: a portal upload by the ticket's own
    /// requester has no byline to replace.
    /// </summary>
    public string? AttachmentAuthor(Guid connectionId, string? authorExternalId, string? authorName)
        => IsAccount(connectionId, authorExternalId) ? $"{_connectionName[connectionId]} integration" : authorName;
}
