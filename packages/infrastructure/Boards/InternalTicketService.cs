using Desk.Application.Abstractions;
using Desk.Application.Analytics;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Analytics;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Boards;

/// <summary>
/// Raising a ticket on a board that belongs to no PSA: the team's own work, and the task one
/// colleague hands another. Nothing is pushed to a provider, because there is none — the record
/// lives here and is complete.
/// </summary>
public sealed class InternalTicketService(
    DeskDbContext db,
    ITenantContext tenant,
    TimeProvider clock,
    IActivityRecorder activity) : IInternalTicketService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<InternalTicketCreatedDto> CreateAsync(Guid appUserId, InternalTicketInput input, CancellationToken ct = default)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length is 0 or > 500) throw new ValidationFailedException("Give the ticket a title of up to 500 characters.");

        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == input.BoardId, ct)
            ?? throw new NotFoundException("Board");
        if (!board.IsActive) throw new ValidationFailedException("That board is closed, so nothing new can be raised on it.");

        var author = await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == appUserId, ct)
            ?? throw new NotFoundException("User");

        if (input.ClientCompanyId is { } companyId && !await db.ClientCompanies.AnyAsync(c => c.Id == companyId, ct))
            throw new ValidationFailedException("That client does not exist.");

        Guid? assignee = null;
        if (input.AssignedAppUserId is { } wanted)
        {
            if (!await db.AppUsers.AnyAsync(u => u.Id == wanted && u.IsActive, ct))
                throw new ValidationFailedException("That person is not an active member of staff.");
            assignee = wanted;
        }

        var number = await NextNumberAsync(board, ct);
        var now = clock.GetUtcNow();
        var ticket = new Ticket
        {
            MspOrganizationId = Org,
            Origin = board.Kind == BoardKind.Rmm ? TicketOrigin.Rmm : TicketOrigin.Internal,
            BoardId = board.Id,
            Number = number,
            // No provider, no provider identity, nothing to push. The absence is the point.
            PsaConnectionId = null,
            Provider = null,
            ExternalTicketId = null,
            ClientCompanyId = input.ClientCompanyId,
            // The requester is the person who raised it. Every existing reader expects a name here,
            // and on this board the honest answer is the member of staff, not a customer contact.
            RequesterName = author.DisplayName,
            RequesterEmail = author.Email,
            CreatedByUserId = appUserId,
            AssignedAppUserId = assignee,
            AssignedByUserId = assignee is null ? null : appUserId,
            Title = title,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            PortalStatus = "NEW",
            PortalPriority = string.IsNullOrWhiteSpace(input.Priority) ? "NORMAL" : input.Priority.Trim().ToUpperInvariant(),
            PortalCategory = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            QueueOrBoard = board.Name,
            SlaDueAt = input.DueAt,
            PsaCreatedAt = now,
            // Synced is the wrong word for a ticket with nowhere to sync to, and every "not synced"
            // reader treats PendingCreate as a failed push. Conflict and Error are worse. Synced is
            // read everywhere as "nothing outstanding", which is exactly true here.
            SyncStatus = TicketSyncStatus.Synced,
            LastSyncedAt = null,
        };
        db.Tickets.Add(ticket);

        if (assignee is { } to)
            db.TicketAssignments.Add(new TicketAssignment
            {
                MspOrganizationId = Org,
                TicketId = ticket.Id,
                ToAppUserId = to,
                AssignedByUserId = appUserId,
                Note = "Raised and assigned",
            });

        await db.SaveChangesAsync(ct);

        await activity.RecordAsync(new ActivityRecord(ActivityKind.TicketCreated, ActivitySource.Portal)
        {
            MspOrganizationId = Org,
            OccurredAt = now,
            ActorUserId = appUserId,
            TicketId = ticket.Id,
            ClientCompanyId = ticket.ClientCompanyId,
            Detail = board.Name,
        }, ct);

        return new InternalTicketCreatedDto(ticket.Id, number, ticket.Title, board.Id);
    }

    /// <summary>
    /// The next number on the board, as BOARDKEY-000123. People quote these to each other, so they
    /// must never repeat: the counter is taken under the row's own concurrency check and retried if
    /// two people raise a ticket on the same board at the same moment.
    /// </summary>
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
}
