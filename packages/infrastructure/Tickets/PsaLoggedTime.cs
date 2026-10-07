using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// The time of some people that nobody logged in the portal: entered in the PSA itself, under a
/// login that is linked to one of them.
///
/// An hour logged in the portal carries its author. One entered in the PSA does not; it is filed
/// under a PSA login, and it is a person's because that login is theirs on that connection. This
/// is the one place that says so for the screens that count a person's recorded time (their day,
/// the team's day, the workforce figures), by the rule the productivity reports already use: the
/// link as it stands now, never the login the portal itself writes as, and only time the PSA
/// actually holds.
///
/// Two queries, and one where none of the people has a PSA login: the screens that call this are
/// held to a number of queries that does not grow with the people or the hours.
/// </summary>
public static class PsaLoggedTime
{
    /// <summary>Each such entry dated in the window, with the person it is counted for.</summary>
    public static async Task<List<(TicketTimeEntry Entry, Guid AppUserId)>> ForAsync(
        DeskDbContext db, IReadOnlyCollection<Guid> people, DateTimeOffset from, DateTimeOffset before, CancellationToken ct = default)
    {
        if (people.Count == 0) return [];
        // Their logins, each with the login its connection writes as: that one is the whole
        // portal's writing and is nobody, whoever it has been linked to.
        var theirs = await (
                from i in db.UserPsaIdentities.AsNoTracking()
                where people.Contains(i.AppUserId)
                join c in db.PsaConnections.AsNoTracking() on i.PsaConnectionId equals c.Id into connections
                from c in connections.DefaultIfEmpty()
                select new { i.AppUserId, i.PsaConnectionId, i.ExternalTechnicianId, Account = c == null ? null : c.DefaultTimeEntryResourceId })
            .ToListAsync(ct);

        // A login is a person only together with its connection: the same id on another account is somebody else.
        var whose = new Dictionary<(Guid Connection, string Login), Guid>();
        foreach (var l in theirs)
            if (!string.IsNullOrWhiteSpace(l.ExternalTechnicianId)
                && !string.Equals(l.Account?.Trim(), l.ExternalTechnicianId.Trim(), StringComparison.OrdinalIgnoreCase))
                whose[(l.PsaConnectionId, Key(l.ExternalTechnicianId))] = l.AppUserId;
        if (whose.Count == 0) return [];

        var connectionIds = theirs.Select(l => l.PsaConnectionId).Distinct().ToList();
        var logins = theirs.Select(l => l.ExternalTechnicianId).Distinct().ToList();
        var rows = await db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.AppUserId == null && e.SyncStatus == TimeEntrySyncStatus.Synced
                && e.PsaConnectionId != null && connectionIds.Contains(e.PsaConnectionId.Value)
                && e.TechnicianExternalId != null && logins.Contains(e.TechnicianExternalId)
                && e.EntryDate >= from && e.EntryDate < before)
            .ToListAsync(ct);

        var found = new List<(TicketTimeEntry, Guid)>();
        foreach (var e in rows)
            if (whose.TryGetValue((e.PsaConnectionId!.Value, Key(e.TechnicianExternalId!)), out var person))
                found.Add((e, person));
        return found;
    }

    /// <summary>A PSA login as it is compared: providers do not agree with themselves on case or spacing.</summary>
    private static string Key(string login) => login.Trim().ToLowerInvariant();
}
