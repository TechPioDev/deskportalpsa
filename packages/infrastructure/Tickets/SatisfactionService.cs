using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Customer satisfaction. A client rates a ticket they can see once it is finished; staff read the
/// ratings back per technician, per client and over time.
///
/// Visibility is the client list's own rule (<see cref="TicketReadService.ClientVisible"/>), so a
/// client can rate exactly the tickets they can open — never an internal board's, never another
/// company's.
/// </summary>
public sealed class SatisfactionService(DeskDbContext db, TimeProvider clock) : ISatisfactionService
{
    /// <summary>How long after a ticket is finished its rating can be given or changed.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public async Task<SatisfactionStateDto> StateAsync(ClientAccess access, Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await TicketAsync(access, ticketId, ct);
        var existing = await db.TicketSatisfactions.AsNoTracking().FirstOrDefaultAsync(s => s.TicketId == ticketId, ct);
        return State(ticket, existing);
    }

    public async Task<SatisfactionStateDto> RateAsync(
        ClientAccess access, Guid ticketId, int rating, string? comment, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5) throw new ValidationFailedException("Choose a rating from 1 to 5.");
        var clean = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (clean is { Length: > 1000 }) throw new ValidationFailedException("Keep the comment to 1,000 characters.");

        var ticket = await TicketAsync(access, ticketId, ct);
        var existing = await db.TicketSatisfactions.FirstOrDefaultAsync(s => s.TicketId == ticketId, ct);
        var state = State(ticket, existing);
        if (!state.CanRate) throw new ValidationFailedException(state.Reason ?? "This ticket cannot be rated.");

        // Who held it NOW, when the client is judging the work — the integration account the portal
        // writes as is nobody, and is not credited with anything.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var external = !string.IsNullOrEmpty(ticket.AssignedTechnicianExternalId)
                       && !account.IsAccount(ticket.PsaConnectionId, ticket.AssignedTechnicianExternalId)
            ? ticket.AssignedTechnicianExternalId : null;
        var name = ticket.AssignedAppUserId is { } uid
            ? await db.AppUsers.AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : external is not null ? ticket.AssignedTechnicianName ?? external : null;

        var row = existing ?? new TicketSatisfaction { MspOrganizationId = ticket.MspOrganizationId, TicketId = ticket.Id };
        row.ClientCompanyId = ticket.ClientCompanyId;
        row.ClientUserId = access.ClientUserId;
        row.Rating = rating;
        row.Comment = clean;
        row.RatedAt = clock.GetUtcNow();
        row.TechnicianAppUserId = ticket.AssignedAppUserId;
        row.TechnicianExternalId = ticket.AssignedAppUserId is null ? external : null;
        row.TechnicianName = name;
        if (existing is null) db.TicketSatisfactions.Add(row);
        await db.SaveChangesAsync(ct);
        return State(ticket, row);
    }

    public async Task<SatisfactionSummaryDto> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var rows = await db.TicketSatisfactions.AsNoTracking()
            .Where(s => s.RatedAt >= from && s.RatedAt <= to)
            .Select(s => new
            {
                s.TicketId, s.Rating, s.Comment, s.RatedAt, s.TechnicianAppUserId, s.TechnicianExternalId, s.TechnicianName,
                s.ClientCompanyId,
                ClientName = db.ClientCompanies.Where(c => c.Id == s.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                Reference = db.Tickets.Where(t => t.Id == s.TicketId).Select(t => t.Number ?? t.ExternalTicketId).FirstOrDefault(),
                Title = db.Tickets.Where(t => t.Id == s.TicketId).Select(t => t.Title).FirstOrDefault(),
                // The PSA account the rated ticket came from: a PSA login is a person only within it.
                Conn = db.Tickets.Where(t => t.Id == s.TicketId).Select(t => t.PsaConnectionId).FirstOrDefault(),
            })
            .ToListAsync(ct);

        static SatisfactionGroupDto Group(string key, string name, IReadOnlyCollection<int> ratings)
        {
            var satisfied = ratings.Count(r => r >= TicketSatisfaction.Satisfied);
            return new SatisfactionGroupDto(key, name, ratings.Count, satisfied,
                ratings.Count > 0 ? Math.Round(100.0 * satisfied / ratings.Count, 1) : null,
                ratings.Count > 0 ? Math.Round(ratings.Average(), 2) : 0);
        }

        var byTech = rows
            .GroupBy(r => r.TechnicianAppUserId is null && r.TechnicianExternalId is null
                ? "" : PersonKey.For(r.TechnicianAppUserId, r.Conn, r.TechnicianExternalId))
            .Select(g => Group(g.Key, g.Key == "" ? "Nobody held it" : g.First().TechnicianName ?? "Unknown", g.Select(r => r.Rating).ToList()))
            .OrderByDescending(g => g.Ratings).ThenBy(g => g.Name).ToList();
        var byClient = rows
            .GroupBy(r => r.ClientCompanyId?.ToString() ?? "")
            .Select(g => Group(g.Key, g.First().ClientName ?? "No client", g.Select(r => r.Rating).ToList()))
            .OrderByDescending(g => g.Ratings).ThenBy(g => g.Name).ToList();
        var recent = rows
            .Where(r => r.Comment is not null || r.Rating <= 2)
            .OrderByDescending(r => r.RatedAt).Take(25)
            .Select(r => new SatisfactionCommentDto(r.TicketId, r.Reference ?? "—", r.Title ?? "", r.Rating, r.Comment,
                r.ClientName, r.TechnicianName, r.RatedAt))
            .ToList();

        var all = Group("", "", rows.Select(r => r.Rating).ToList());
        return new SatisfactionSummaryDto(
            all.Ratings, all.Satisfied, all.CsatPct, rows.Count > 0 ? all.Average : null,
            Enumerable.Range(1, 5).Select(n => rows.Count(r => r.Rating == n)).ToList(),
            byTech, byClient, recent);
    }

    private async Task<Ticket> TicketAsync(ClientAccess access, Guid ticketId, CancellationToken ct)
        => await TicketReadService.ClientVisible(db, access).AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct)
           ?? throw new NotFoundException("Ticket");

    private SatisfactionStateDto State(Ticket ticket, TicketSatisfaction? existing)
    {
        if (!TicketStatusRules.Finished(ticket.PortalStatus))
            return new SatisfactionStateDto(false, "You can rate this ticket once it is resolved.",
                existing?.Rating, existing?.Comment, existing?.RatedAt, null);

        // Measured from when it was finished; a ticket with no recorded date falls back to its last update.
        var finished = ticket.ResolvedAt ?? ticket.ClosedAt ?? ticket.UpdatedAt;
        var until = finished + Window;
        return clock.GetUtcNow() <= until
            ? new SatisfactionStateDto(true, null, existing?.Rating, existing?.Comment, existing?.RatedAt, until)
            : new SatisfactionStateDto(false, "Ratings close 30 days after a ticket is resolved.",
                existing?.Rating, existing?.Comment, existing?.RatedAt, until);
    }
}
