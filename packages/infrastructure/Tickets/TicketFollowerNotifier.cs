using Desk.Application.Abstractions;
using Desk.Domain.Notifications;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Tells the people who follow a ticket what happened on it. Following used to put a ticket in
/// somebody's list and nothing more: nobody was told they had been put on it, and nothing told
/// them when it moved. That is not what "copy me in" means to anyone.
///
/// Two messages, both by e-mail (everyone has an address; one person in forty-one has switched
/// notifications on for a device) and to a device as well where there is one:
///
///   - "You were put on this ticket", once, to someone another person added. Nobody is told that
///     they followed a ticket themselves.
///   - "There is news", for replies and notes written by somebody else since the last message, and
///     for the ticket being finished. Several things inside one turn make one message, not several.
///
/// What is sent names the ticket and who did what. It quotes nothing from the thread: an internal
/// note stays on the ticket, where the reader's own access decides whether they see it.
///
/// Found by looking, not by hooking the places that write a note: a reply arrives from the portal,
/// from a client, from a PSA on its next sync and from a queue that sends later, and a follower is
/// owed the same message whichever it was. Each follower row remembers how far it has been told
/// (<see cref="TicketFollower.NotifiedThrough"/>), so a turn that is missed is made up by the next
/// and nothing is said twice.
/// </summary>
public sealed class TicketFollowerNotifier(
    DeskDbContext db, TemplateMailer mailer, PortalOptions portal, TimeProvider clock, ILogger<TicketFollowerNotifier> logger)
{
    /// <summary>
    /// How long something must have been on a ticket before it is reported. A note is stamped when
    /// it is written and visible a moment later, when its save completes; reporting right up to
    /// "now" would step over one stamped just before this turn and saved just after it, and it
    /// would never be told. Waiting this long costs nothing a reader would notice.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>One turn for the organization in scope. Returns how many messages were sent.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var horizon = now - Settle;
        var rows = await db.TicketFollowers.ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.ExternalTicketId, t.Title, t.PortalStatus, t.ResolvedAt, t.ClosedAt })
            .ToDictionaryAsync(t => t.Id, ct);
        var userIds = rows.Select(r => r.AppUserId).Concat(rows.Where(r => r.AddedByUserId != null).Select(r => r.AddedByUserId!.Value)).Distinct().ToList();
        var users = await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive })
            .ToDictionaryAsync(u => u.Id, ct);

        // SQLite, which the tests and the local mode run on, cannot compare or order by a date with
        // an offset: the notes are narrowed by ticket in the database and by time here. A ticket
        // with followers is a small set and its notes a short list.
        var notes = (await db.TicketNotes.AsNoTracking().Where(n => ticketIds.Contains(n.TicketId))
                .Select(n => new { n.TicketId, n.AuthorName, n.IsPublic, n.AuthoredByClient, n.CreatedAt })
                .ToListAsync(ct))
            .ToLookup(n => n.TicketId);
        var subscribed = (await db.PushSubscriptions.AsNoTracking().Select(s => s.AppUserId).Distinct().ToListAsync(ct)).ToHashSet();

        var sent = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!tickets.TryGetValue(row.TicketId, out var ticket)) continue;
            users.TryGetValue(row.AppUserId, out var person);
            var reachable = person is { IsActive: true, Email.Length: > 0 };
            var reference = ticket.Number ?? ticket.ExternalTicketId ?? "A ticket";
            var link = $"{portal.PublicUrl.TrimEnd('/')}/dashboard/tickets/{ticket.Id}";
            var through = row.NotifiedThrough ?? row.CreatedAt;

            IReadOnlyDictionary<string, string> Values(string addedBy, string updates) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["user.name"] = person!.DisplayName, ["user.first_name"] = FirstName(person.DisplayName), ["user.email"] = person.Email,
                ["product"] = EmailTemplates.Product, ["product_url"] = portal.PublicUrl,
                ["ticket.number"] = reference, ["ticket.title"] = ticket.Title, ["added_by"] = addedBy, ["updates"] = updates, ["link"] = link,
            };

            // ---- told once that somebody put them on it
            if (row.AddedNotifiedAt is null)
            {
                if (reachable && row.AddedByUserId is { } by && by != row.AppUserId)
                {
                    var adder = users.TryGetValue(by, out var who) ? who.DisplayName : "A colleague";
                    if (await SendAsync(row, EmailTemplates.FollowerAdded, person!.Email, Values(adder, ""), ct)) sent++;
                    Push(subscribed, row, $"{adder} added you to {reference}", ticket.Title);
                }
                row.AddedNotifiedAt = now;
            }

            // ---- what happened since they were last told
            var news = new List<string>();
            foreach (var note in notes[row.TicketId].Where(n => n.CreatedAt > through && n.CreatedAt <= horizon).OrderBy(n => n.CreatedAt))
            {
                // Their own reply is not news to them. A note carries its author's name and no id
                // (it may have come from a PSA), so the name is what there is to compare.
                if (person is not null && string.Equals(note.AuthorName?.Trim(), person.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var author = string.IsNullOrWhiteSpace(note.AuthorName) ? "Somebody" : note.AuthorName.Trim();
                news.Add(note.AuthoredByClient ? $"The client replied ({author})"
                    : note.IsPublic ? $"{author} replied"
                    : $"{author} added an internal note");
            }
            var finishedAt = ticket.ClosedAt ?? ticket.ResolvedAt;
            if (TicketStatusRules.Finished(ticket.PortalStatus) && finishedAt is { } done && done > through && done <= horizon)
                news.Add(ticket.ClosedAt is not null ? "The ticket was closed" : "The ticket was resolved");

            if (news.Count > 0 && reachable)
            {
                // A long day on one ticket is still one message: the first few lines and a count.
                var lines = news.Count <= 8 ? news : [.. news.Take(7), $"and {news.Count - 7} more"];
                if (await SendAsync(row, EmailTemplates.FollowerNews, person!.Email, Values("", string.Join("\n", lines.Select(l => "- " + l))), ct)) sent++;
                Push(subscribed, row, news.Count == 1 ? $"{news[0]} on {reference}" : $"{news.Count} updates on {reference}", ticket.Title);
            }
            // Never backwards: someone who started following a moment ago is not owed what came before.
            row.NotifiedThrough = horizon > through ? horizon : through;
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Mails one message. A failure is logged by the mailer and is not tried again: a follower is not owed a retry storm.</summary>
    private async Task<bool> SendAsync(TicketFollower row, string template, string to, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        try
        {
            var (ok, _) = await mailer.SendAsync(row.MspOrganizationId, template, to, values, "Ticket", row.TicketId, ct);
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A follower of ticket {TicketId} could not be mailed", row.TicketId);
            return false;
        }
    }

    /// <summary>The same news to the person's own devices, where they switched that on.</summary>
    private void Push(HashSet<Guid> subscribed, TicketFollower row, string title, string body)
    {
        if (!subscribed.Contains(row.AppUserId)) return;
        db.PushNotifications.Add(new PushNotification
        {
            MspOrganizationId = row.MspOrganizationId, AppUserId = row.AppUserId, Kind = PushKind.Following,
            TicketId = row.TicketId, Title = title, Body = body, Url = $"/dashboard/tickets/{row.TicketId}",
        });
    }

    private static string FirstName(string displayName)
        => displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? displayName;
}

/// <summary>One turn for every organization, each inside its own scope, as the other workers do.</summary>
public sealed class TicketFollowerNotifyRunner(IServiceScopeFactory scopes, ILogger<TicketFollowerNotifyRunner> logger)
{
    public async Task<int> RunAllAsync(CancellationToken ct = default)
    {
        List<Guid> organizations;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            organizations = await db.Set<MspOrganization>().AsNoTracking().Where(o => o.IsActive).Select(o => o.Id).ToListAsync(ct);
        }

        var sent = 0;
        foreach (var org in organizations)
        {
            try
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org);
                sent += await scope.ServiceProvider.GetRequiredService<TicketFollowerNotifier>().RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Follower notices failed for organization {Organization}", org);
            }
        }
        return sent;
    }
}
