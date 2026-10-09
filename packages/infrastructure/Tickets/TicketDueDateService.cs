using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Extends a ticket's due date. Provider first for a PSA ticket (the PSA holds it; a refusal is
/// shown and nothing is saved), local for a board ticket. The original due date is kept from the
/// first extension on; the reason and who gave it are kept with the ticket and in the audit.
/// </summary>
public sealed class TicketDueDateService(
    DeskDbContext db, IConnectorResolver connectors, ICurrentUser user, TimeProvider clock, IAuditWriter? audit = null) : ITicketDueDateService
{
    public const int MaxReasonLength = 500;

    public async Task<TicketDueDateDto> ExtendAsync(Ticket ticket, ExtendDueDateInput input, CancellationToken ct = default)
    {
        var reason = (input.Reason ?? "").Trim();
        var now = clock.GetUtcNow();
        var problems = new List<string>();
        if (reason.Length < 3) problems.Add("Say why the due date is moving (at least a few words).");
        if (reason.Length > MaxReasonLength) problems.Add($"The reason must be {MaxReasonLength} characters or fewer.");
        if (input.DueAt <= now) problems.Add("The new due date must be in the future.");
        if (ticket.SlaDueAt is { } current && input.DueAt <= current) problems.Add("The new due date must be later than the current one. To bring it forward, ask an administrator.");
        if (TicketStatusRules.Finished(ticket.PortalStatus)) problems.Add("A finished ticket has no due date to extend.");
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems));

        var from = ticket.SlaDueAt;
        var isPsa = ticket.Origin == TicketOrigin.Psa && !string.IsNullOrEmpty(ticket.ExternalTicketId);
        if (isPsa)
        {
            // The PSA holds a ticket's due date: it is asked first, and a refusal changes nothing here.
            var connector = await connectors.ResolveAsync(ticket.PsaConnectionId ?? throw new ValidationFailedException("This ticket has no PSA connection."), ct);
            var result = await connector.UpdateTicketAsync(ticket.ExternalTicketId!, new UnifiedTicketUpdate
            {
                DueDate = input.DueAt, IdempotencyKey = $"due:{ticket.Id:N}:{input.DueAt.ToUnixTimeSeconds()}",
            }, ct);
            if (!result.Success) throw new ValidationFailedException(result.Error ?? "The PSA did not accept the new due date.");
        }

        ticket.OriginalSlaDueAt ??= from;
        ticket.SlaDueAt = input.DueAt;
        ticket.DueDateExtensions++;
        ticket.DueDateExtendedAt = now;
        ticket.DueDateExtendedByUserId = user.UserId;
        ticket.DueDateExtendedByName = user.DisplayName;
        ticket.DueDateExtensionReason = reason;
        await db.SaveChangesAsync(ct);

        if (audit is not null)
            await audit.WriteAsync("ticket.due_date.extended", "Ticket", ticket.Id.ToString(),
                new { from, to = input.DueAt, reason, extensions = ticket.DueDateExtensions, sentToPsa = isPsa }, ct);

        return Describe(ticket) ?? throw new InvalidOperationException("An extension was just recorded.");
    }

    /// <summary>What the ticket says about its due date, or null where it was never moved.</summary>
    public static TicketDueDateDto? Describe(Ticket t)
        => t.DueDateExtensions == 0 ? null
            : new TicketDueDateDto(t.SlaDueAt, t.OriginalSlaDueAt, t.DueDateExtensions, t.DueDateExtendedAt, t.DueDateExtendedByName, t.DueDateExtensionReason);
}
