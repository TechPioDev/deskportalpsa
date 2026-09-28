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

        // What the ticket is about, and what that usually implies. A topic is a shortcut: anything
        // the caller stated explicitly wins over it, so the form can pre-fill and still be corrected.
        BoardTopic? topic = null;
        if (input.BoardTopicId is { } topicId)
        {
            topic = await db.BoardTopics.FirstOrDefaultAsync(t => t.Id == topicId && t.BoardId == board.Id, ct)
                ?? throw new ValidationFailedException("That topic is not on this board.");
            if (!topic.IsActive) throw new ValidationFailedException("That topic has been retired.");
        }

        var departmentId = input.DepartmentId ?? topic?.DefaultDepartmentId;
        if (departmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept && d.IsActive, ct))
            throw new ValidationFailedException("That department does not exist, or is closed.");

        Guid? assignee = null;
        if ((input.AssignedAppUserId ?? topic?.DefaultAssigneeUserId) is { } wanted)
        {
            if (!await db.AppUsers.AnyAsync(u => u.Id == wanted && u.IsActive, ct))
                throw new ValidationFailedException("That person is not an active member of staff.");
            assignee = wanted;
        }

        var number = await NextNumberAsync(board, ct);
        var now = clock.GetUtcNow();
        var sla = await SlaPlanner.ForAsync(db, board, topic, now, ct);
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
            PortalPriority = Priority(input.Priority) ?? topic?.DefaultPriority ?? "NORMAL",
            DepartmentId = departmentId,
            BoardTopicId = topic?.Id,
            Source = Source(input.Source),
            PortalCategory = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            QueueOrBoard = board.Name,
            // The date somebody typed, else the topic's fixed time, else the SLA plan's, else none. A
            // due date that nobody chose is worse than none: it becomes an overdue ticket nobody meant.
            SlaDueAt = input.DueAt ?? (topic?.DueInHours is { } hours ? now.AddHours(hours) : sla.ResolveBy),
            SlaPlanId = sla.PlanId,
            FirstResponseDueAt = sla.RespondBy,
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

    private static string? Priority(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>
    /// How the work reached us. A short known list rather than free text, because the only reason to
    /// record it is to count it later, and free text cannot be counted.
    /// </summary>
    public static readonly string[] Sources = ["Phone", "Email", "Chat", "Walk-in", "Meeting", "Monitoring", "Other"];

    private static string? Source(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Sources.FirstOrDefault(s => s.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ValidationFailedException($"Source must be one of: {string.Join(", ", Sources)}.");
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
