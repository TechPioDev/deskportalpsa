using System.Security.Cryptography;
using System.Text;
using Desk.Application.Analytics;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Analytics;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Application.Abstractions;
using Desk.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Boards;

/// <summary>
/// Turns a monitoring tool's alert into a ticket on a board.
///
/// The request arrives from a vendor's cloud with no session behind it, so the source's key is the
/// whole of the authentication and the tenant is whichever organization owns that key. Everything
/// here therefore runs OUTSIDE the tenant filter deliberately, and every write states the
/// organization it belongs to rather than inheriting one.
/// </summary>
public sealed class AlertIntakeService(
    DeskDbContext db,
    ISettableTenantContext tenant,
    TimeProvider clock,
    IActivityRecorder activity,
    ILogger<AlertIntakeService> logger) : IAlertIntakeService
{
    /// <summary>A repeat of the same alert within this window is the tool reminding us, not a new fault.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(24);

    public static string HashKey(string key)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<AlertResult> ReceiveAsync(string key, AlertMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new NotFoundException("Alert source");

        // No tenant is established yet: the key decides which organization this delivery belongs to.
        var hash = HashKey(key.Trim());
        var source = await db.AlertSources.IgnoreQueryFilters()
            .Include(s => s.Board)
            .FirstOrDefaultAsync(s => s.KeyHash == hash, ct);

        // An unknown key and a switched-off source answer identically, so probing keys teaches nothing.
        if (source is null || !source.IsActive || source.Board is null || !source.Board.IsActive)
            throw new NotFoundException("Alert source");

        // The key decides the organization. A scope established earlier is never pivoted — it is
        // checked, and a key belonging to a different organization is refused as if it were unknown.
        if (tenant.HasScope)
        {
            if (tenant.OrganizationId != source.MspOrganizationId) throw new NotFoundException("Alert source");
        }
        else
        {
            tenant.SetTenant(source.MspOrganizationId);
        }

        var alertId = (message.AlertId ?? "").Trim();
        var title = (message.Title ?? "").Trim();
        if (alertId.Length is 0 or > 200)
            return await RefuseAsync(source, "The alert id is missing, or longer than 200 characters.", ct);
        if (title.Length == 0 && !message.Cleared)
            return await RefuseAsync(source, "The alert has no title.", ct);

        source.LastReceivedAt = clock.GetUtcNow();
        source.ReceivedCount++;
        source.LastError = null;

        var existing = await db.Tickets
            .FirstOrDefaultAsync(t => t.AlertSourceId == source.Id && t.SourceAlertId == alertId, ct);

        if (message.Cleared)
            return await ClearAsync(source, existing, alertId, ct);

        if (existing is not null)
            return await RepeatAsync(source, existing, message, ct);

        return await OpenAsync(source, alertId, title, message, ct);
    }

    private async Task<AlertResult> OpenAsync(AlertSource source, string alertId, string title, AlertMessage message, CancellationToken ct)
    {
        var board = source.Board!;
        var now = clock.GetUtcNow();
        var client = await MatchClientAsync(message.Client, ct);
        var number = await NextNumberAsync(board, ct);
        // Timed from when we were told, not from when the tool says the fault began: nobody can be
        // late answering an alert that had not arrived yet.
        var sla = await SlaPlanner.ForAsync(db, board, null, now, ct);

        var ticket = new Ticket
        {
            MspOrganizationId = source.MspOrganizationId,
            Origin = TicketOrigin.Rmm,
            BoardId = board.Id,
            AlertSourceId = source.Id,
            SourceAlertId = alertId,
            Number = number,
            ClientCompanyId = client?.Id,
            // The tool raised it, and saying so is more honest than attributing it to whoever last
            // configured the webhook.
            RequesterName = source.Name,
            RequesterEmail = "alerts@monitoring.local",
            Title = Trim(title, 500),
            Description = Describe(message),
            PortalStatus = "NEW",
            PortalPriority = Priority(message.Severity),
            PortalCategory = Trim(message.Device, 200),
            QueueOrBoard = board.Name,
            PsaCreatedAt = message.OccurredAt ?? now,
            SyncStatus = TicketSyncStatus.Synced,
            SlaPlanId = sla.PlanId,
            SlaDueAt = sla.ResolveBy,
            FirstResponseDueAt = sla.RespondBy,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        await activity.RecordAsync(new ActivityRecord(ActivityKind.TicketCreated, ActivitySource.Psa)
        {
            MspOrganizationId = source.MspOrganizationId,
            OccurredAt = ticket.PsaCreatedAt ?? now,
            TicketId = ticket.Id,
            ClientCompanyId = ticket.ClientCompanyId,
            Detail = source.Name,
        }, ct);

        logger.LogInformation("Alert {AlertId} from {Source} opened {Number}", alertId, source.Name, number);
        return new AlertResult("opened", ticket.Id, number,
            client is null && !string.IsNullOrWhiteSpace(message.Client)
                ? $"No client here is called \"{message.Client}\", so the ticket names none."
                : null);
    }

    /// <summary>
    /// The same condition reported again. One ticket, one note saying it is still happening — and no
    /// note at all if the ticket is young, because a tool that repeats every five minutes would
    /// otherwise bury its own ticket in its own repetitions.
    /// </summary>
    private async Task<AlertResult> RepeatAsync(AlertSource source, Ticket ticket, AlertMessage message, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var reopened = false;
        if (ticket.ClosedAt is not null && now - ticket.ClosedAt.Value <= RepeatWindow)
        {
            ticket.ClosedAt = null;
            ticket.ResolvedAt = null;
            ticket.PortalStatus = "NEW";
            reopened = true;
        }

        var lastNote = await db.TicketNotes.AsNoTracking()
            .Where(n => n.TicketId == ticket.Id)
            .OrderByDescending(n => n.NoteCreatedAt)
            .Select(n => (DateTimeOffset?)n.NoteCreatedAt)
            .FirstOrDefaultAsync(ct);

        if (reopened || lastNote is null || now - lastNote.Value > TimeSpan.FromHours(1))
        {
            db.TicketNotes.Add(new TicketNote
            {
                MspOrganizationId = ticket.MspOrganizationId,
                TicketId = ticket.Id,
                AuthorName = source.Name,
                AuthoredByClient = false,
                Body = reopened
                    ? $"The alert is happening again: {Trim(message.Title, 400)}"
                    : $"Still happening: {Trim(message.Title, 400)}",
                IsPublic = false,
                NoteCreatedAt = now,
                OriginCorrelationId = ticket.CorrelationId,
            });
        }

        await db.SaveChangesAsync(ct);
        return new AlertResult(reopened ? "opened" : "updated", ticket.Id, ticket.Number);
    }

    private async Task<AlertResult> ClearAsync(AlertSource source, Ticket? ticket, string alertId, CancellationToken ct)
    {
        if (ticket is null)
        {
            // A clear for something we never opened is not an error: the alert may pre-date the source.
            await db.SaveChangesAsync(ct);
            return new AlertResult("ignored", null, null, "No open ticket for that alert.");
        }

        var now = clock.GetUtcNow();
        db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticket.Id,
            AuthorName = source.Name,
            AuthoredByClient = false,
            Body = "The monitoring tool reports this condition has cleared.",
            IsPublic = false,
            NoteCreatedAt = now,
            OriginCorrelationId = ticket.CorrelationId,
        });

        if (source.CloseOnClear && ticket.ClosedAt is null)
        {
            ticket.PortalStatus = "CLOSED";
            ticket.ResolvedAt ??= now;
            ticket.ClosedAt = now;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Alert {AlertId} from {Source} cleared {Number}", alertId, source.Name, ticket.Number);
        return new AlertResult(source.CloseOnClear ? "closed" : "updated", ticket.Id, ticket.Number);
    }

    private async Task<AlertResult> RefuseAsync(AlertSource source, string why, CancellationToken ct)
    {
        source.LastError = why;
        source.LastReceivedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        throw new ValidationFailedException(why);
    }

    /// <summary>
    /// The client the tool named, matched against the companies the portal already holds. Matched on
    /// the name as given, then without punctuation, and never on a guess: an unmatched name is kept
    /// in the ticket text so a person can see what the tool called it.
    /// </summary>
    private async Task<Desk.Domain.Tenancy.ClientCompany?> MatchClientAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var wanted = name.Trim();
        var exact = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Name == wanted, ct);
        if (exact is not null) return exact;

        var loose = Simplify(wanted);
        var all = await db.ClientCompanies.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var hit = all.FirstOrDefault(c => Simplify(c.Name) == loose);
        return hit is null ? null : await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == hit.Id, ct);
    }

    private static string Simplify(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private async Task<string> NextNumberAsync(Board board, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var next = board.NextNumber;
            board.NextNumber = next + 1;
            try
            {
                await db.SaveChangesAsync(ct);
                return $"{board.Key}-{next:D6}";
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                await db.Entry(board).ReloadAsync(ct);
            }
        }
    }

    private static string? Describe(AlertMessage m)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Description)) lines.Add(m.Description.Trim());
        if (!string.IsNullOrWhiteSpace(m.Device)) lines.Add($"Device: {m.Device.Trim()}");
        if (!string.IsNullOrWhiteSpace(m.Client)) lines.Add($"Client, as the tool names it: {m.Client.Trim()}");
        if (!string.IsNullOrWhiteSpace(m.Severity)) lines.Add($"Severity: {m.Severity.Trim()}");
        return lines.Count == 0 ? null : Trim(string.Join("\n", lines), 4000);
    }

    /// <summary>Severity as the tools word it, mapped to the portal's own priorities.</summary>
    public static string Priority(string? severity) => (severity ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" or "fatal" or "severe" or "high" or "1" => "HIGH",
        "urgent" or "emergency" => "URGENT",
        "warning" or "moderate" or "medium" or "2" => "NORMAL",
        "info" or "information" or "informational" or "low" or "3" => "LOW",
        _ => "NORMAL",
    };

    private static string Trim(string? value, int max)
        => value is null ? "" : value.Length <= max ? value : value[..max];
}
