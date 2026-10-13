using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Tickets imported before contacts were captured hold "Unknown / unknown@unknown", so a public
/// reply on them has nobody to go to. A sync cannot repair them: it reads only the tickets its
/// import window returns (active in the last N days), and most of the old ones are outside it.
/// This asks the PSA for each such ticket directly and keeps the contact it names.
/// </summary>
public static class ContactBackfill
{
    public sealed record Outcome(int Found, int NoContact, int Failed, int NameOnly);

    /// <summary>
    /// Held tickets with no reachable contact, by connection, as (row id, provider ticket id).
    ///
    /// Only for connections the ticket sync itself would call (<see cref="SyncSchedule.Due"/>).
    /// This runs by itself at every start; a connection that is paused, put away, or whose login
    /// the PSA has refused is left alone, and its tickets wait for the day it works again. Before
    /// this it asked a PSA that had refused the login once for every such ticket, at every start.
    /// </summary>
    public static async Task<Dictionary<Guid, List<(Guid Id, string ExternalId)>>> PendingAsync(DeskDbContext db, CancellationToken ct = default)
    {
        var callable = await SyncSchedule.Due(db.PsaConnections.AsNoTracking()).Select(c => c.Id).ToListAsync(ct);
        if (callable.Count == 0) return [];
        var rows = await db.Tickets.AsNoTracking()
            // PSA tickets only: the team's own tickets have no provider contact to read.
            .Where(t => t.ExternalTicketId != null && t.PsaConnectionId != null && callable.Contains(t.PsaConnectionId.Value) && t.RequesterUserId == null
                        && (t.RequesterEmail == TicketContact.PlaceholderEmail || !t.RequesterEmail.Contains("@")))
            .Select(t => new { t.Id, PsaConnectionId = t.PsaConnectionId!.Value, ExternalId = t.ExternalTicketId! })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.PsaConnectionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ExternalId, StringComparer.Ordinal).Select(r => (r.Id, r.ExternalId)).ToList());
    }

    /// <summary>
    /// Reads each ticket from the PSA and stores the contact it names. A ticket the provider cannot
    /// answer for is counted and left as it was; the next start tries it again. A refused login is
    /// not counted and carried on from: it is thrown, so the caller stops asking this connection.
    /// </summary>
    public static async Task<Outcome> ApplyAsync(
        DeskDbContext db, IServiceManagementConnector connector, IReadOnlyList<(Guid Id, string ExternalId)> tickets, CancellationToken ct = default)
    {
        int found = 0, none = 0, failed = 0, nameOnly = 0;
        foreach (var (id, externalId) in tickets)
        {
            Desk.PsaCore.Models.UnifiedTicket? live;
            try { live = await connector.GetTicketAsync(externalId, ct); }
            catch (ConnectorException ex) when (ex.Kind == ConnectorFailureKind.Authentication) { throw; }
            catch (ConnectorException) { failed++; continue; }

            var email = string.IsNullOrWhiteSpace(live?.RequesterEmail) || !live.RequesterEmail.Contains('@') ? null : live.RequesterEmail.Trim();
            var name = string.IsNullOrWhiteSpace(live?.RequesterName) ? null : live.RequesterName.Trim();
            if (email is null && name is null) { none++; continue; }

            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (ticket is null) continue;
            if (email is not null) { ticket.RequesterEmail = email; found++; }
            else nameOnly++;
            if (name is not null) ticket.RequesterName = name;
            await db.SaveChangesAsync(ct);
        }
        return new Outcome(found, none, failed, nameOnly);
    }
}
