using Desk.Domain.Notifications;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Tells a person that a ticket was given to them. Until this, nothing did: a ticket raised for
/// Gurpreet sat in his list and he learned of it when he next looked. The only signal there was,
/// a notification on a device, needs the device to have been set up (one person in forty-one had)
/// and compares a ticket with how it last looked, so a ticket that is assigned as it is raised
/// has no "before" to differ from and said nothing at all.
///
/// One e-mail, to the person a ticket was given to by somebody else, naming who gave it. Nobody is
/// told that they took a ticket themselves. It quotes nothing from the ticket but its title.
///
/// Read from the handover history (<see cref="TicketAssignment"/>), which every assignment made in
/// the portal writes: as a ticket is raised, when it is passed on, and when work is planned into
/// somebody's time. Each row remembers that it has been dealt with
/// (<see cref="TicketAssignment.NotifiedAt"/>), so a turn that is missed is made up by the next and
/// nothing is said twice. An assignment made inside a PSA writes no such row and is not mailed.
/// </summary>
public sealed class TicketAssignmentNotifier(
    DeskDbContext db, TemplateMailer mailer, PortalOptions portal, TimeProvider clock, ILogger<TicketAssignmentNotifier> logger)
{
    /// <summary>
    /// How long an assignment must have stood before it is reported. A ticket given to the wrong
    /// person and corrected a moment later is one message, to the right person.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>A ticket and its first assignment are saved together; this much apart is still "as it was raised".</summary>
    private static readonly TimeSpan RaisedTogether = TimeSpan.FromSeconds(5);

    /// <summary>One turn for the organization in scope. Returns how many e-mails were sent.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var horizon = now - Settle;
        // SQLite, which the tests and the local mode run on, cannot compare a date with an offset:
        // "not yet dealt with" is asked of the database and "has stood long enough" is decided here.
        var rows = (await db.TicketAssignments.Where(a => a.NotifiedAt == null).ToListAsync(ct))
            .Where(a => a.CreatedAt <= horizon).OrderBy(a => a.CreatedAt).ToList();
        if (rows.Count == 0) return 0;

        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.ExternalTicketId, t.Title, t.PortalStatus, t.AssignedAppUserId, t.CreatedAt })
            .ToDictionaryAsync(t => t.Id, ct);
        var userIds = rows.SelectMany(r => new[] { r.ToAppUserId, r.AssignedByUserId }).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var users = await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive })
            .ToDictionaryAsync(u => u.Id, ct);
        var subscribed = (await db.PushSubscriptions.AsNoTracking().Select(s => s.AppUserId).Distinct().ToListAsync(ct)).ToHashSet();
        var preferences = await db.PushPreferences.AsNoTracking().ToDictionaryAsync(p => p.AppUserId, ct);

        // A ticket that changed hands more than once since the last turn has one message owed: for
        // where it rests now. The steps before it are history, which the ticket itself shows.
        var latest = rows.GroupBy(r => r.TicketId).ToDictionary(g => g.Key, g => g.Last().Id);

        var sent = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            // Dealt with whatever is decided below: an assignment is reported once or not at all.
            row.NotifiedAt = now;

            if (latest[row.TicketId] != row.Id) continue;
            if (row.ToAppUserId is not { } to) continue;                                  // put back on the board
            if (row.AssignedByUserId == to) continue;                                     // they took it themselves
            if (!tickets.TryGetValue(row.TicketId, out var ticket)) continue;
            if (ticket.AssignedAppUserId != to) continue;                                 // no longer theirs
            if (TicketStatusRules.Finished(ticket.PortalStatus)) continue;                // nothing left to do on it
            if (!users.TryGetValue(to, out var person) || !person.IsActive || person.Email.Length == 0) continue;

            var assigner = row.AssignedByUserId is { } by && users.TryGetValue(by, out var who) ? who.DisplayName : "A colleague";
            var reference = ticket.Number ?? ticket.ExternalTicketId ?? "A ticket";
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["user.name"] = person.DisplayName, ["user.first_name"] = FirstName(person.DisplayName), ["user.email"] = person.Email,
                ["product"] = EmailTemplates.Product, ["product_url"] = portal.PublicUrl,
                ["ticket.number"] = reference, ["ticket.title"] = ticket.Title, ["assigned_by"] = assigner,
                ["link"] = $"{portal.PublicUrl.TrimEnd('/')}/dashboard/tickets/{ticket.Id}",
            };
            if (await SendAsync(row, person.Email, values, ct)) sent++;

            // A device is told here only for a ticket that was assigned as it was raised. One that
            // changes hands later is already reported by the notification scan, which sees the
            // holder change; telling it here as well would buzz the same phone twice.
            var asRaised = row.FromAppUserId is null && (row.CreatedAt - ticket.CreatedAt).Duration() <= RaisedTogether;
            if (asRaised && subscribed.Contains(to) && (!preferences.TryGetValue(to, out var pref) || pref.Wants(PushKind.Assigned)))
                db.PushNotifications.Add(new PushNotification
                {
                    MspOrganizationId = row.MspOrganizationId, AppUserId = to, Kind = PushKind.Assigned, TicketId = ticket.Id,
                    Title = $"{reference} assigned to you", Body = ticket.Title, Url = $"/dashboard/tickets/{ticket.Id}",
                });
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Mails one message. A failure is logged by the mailer and is not tried again: the ticket is in their list either way.</summary>
    private async Task<bool> SendAsync(TicketAssignment row, string to, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        try
        {
            var (ok, _) = await mailer.SendAsync(row.MspOrganizationId, EmailTemplates.TicketAssigned, to, values, "Ticket", row.TicketId, ct);
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The person given ticket {TicketId} could not be mailed", row.TicketId);
            return false;
        }
    }

    private static string FirstName(string displayName)
        => displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? displayName;
}
