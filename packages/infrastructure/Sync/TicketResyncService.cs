using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Lists tickets the portal holds that never reached the PSA, and pushes them again one at a time.
///
/// The retry rebuilds the provider request from scratch rather than replaying whatever was sent
/// originally: mapping rules and the connection's ticket defaults are re-read at retry time, so a
/// ticket rejected for a missing queue succeeds once the default board is configured, without the
/// customer re-entering anything.
/// </summary>
public sealed class TicketResyncService(
    DeskDbContext db,
    IConnectorResolver connectors,
    IMappingEngine mapping,
    IAuditWriter audit,
    TimeProvider clock) : ITicketResyncService
{
    /// <summary>
    /// Anything not confirmed as living in the PSA — asked only of tickets that BELONG in a PSA.
    /// A ticket the team raised for itself, or an RMM alert, is complete where it is: it has no
    /// provider, was never pushed, and reporting it as "never reached the PSA" would turn every
    /// internal ticket into a permanent critical alert.
    /// </summary>
    private static bool IsUnsynced(Ticket t) =>
        t.Origin == TicketOrigin.Psa && (t.ExternalTicketId == null || t.SyncStatus != TicketSyncStatus.Synced);

    public async Task<UnsyncedTicketsDto> ListAsync(Guid? connectionId = null, CancellationToken ct = default)
    {
        var q = db.Tickets.AsNoTracking()
            .Where(t => t.Origin == TicketOrigin.Psa && t.PsaConnectionId != null
                        && (t.ExternalTicketId == null || t.SyncStatus != TicketSyncStatus.Synced));
        if (connectionId is { } id) q = q.Where(t => t.PsaConnectionId == id);

        var rows = await q
            .OrderByDescending(t => t.CreatedAt)
            .Join(db.PsaConnections.AsNoTracking(), t => t.PsaConnectionId!.Value, c => c.Id, (t, c) => new { t, c.Name })
            .Take(500)
            .Select(x => new
            {
                x.t.Id, PsaConnectionId = x.t.PsaConnectionId!.Value, ConnectionName = x.Name, x.t.Title,
                x.t.ClientCompanyId, x.t.SyncStatus, x.t.SyncError, x.t.CreatedAt, x.t.LastSyncedAt,
            })
            .ToListAsync(ct);

        var companyIds = rows.Where(r => r.ClientCompanyId is not null).Select(r => r.ClientCompanyId!.Value).Distinct().ToList();
        var companies = await db.ClientCompanies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var tickets = rows.Select(r => new UnsyncedTicketDto(
            r.Id, r.PsaConnectionId, r.ConnectionName, r.Title,
            r.ClientCompanyId is { } cid ? companies.GetValueOrDefault(cid) : null,
            r.SyncStatus.ToString(), r.SyncError, r.CreatedAt, r.LastSyncedAt)).ToList();

        return new UnsyncedTicketsDto(tickets.Count, tickets);
    }

    public async Task<ResyncResultDto> ResyncAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new NotFoundException("Ticket");

        // A ticket that belongs to no PSA cannot be pushed to one, and saying "not found" would
        // suggest the ticket is missing rather than that the request makes no sense for it.
        if (ticket.Origin != TicketOrigin.Psa)
            throw new ValidationFailedException("This ticket is on an internal board and does not belong to a PSA.");

        // Already there: report it rather than creating a duplicate in the PSA.
        if (!IsUnsynced(ticket))
            return new ResyncResultDto(true, ticket.Id, ticket.ExternalTicketId, null);

        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == ticket.PsaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == ticket.ClientCompanyId, ct)
            ?? throw new NotFoundException("Client company");

        var rules = await ConnectionMappingRules.LoadAsync(
            db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var ctx = new MappingContext
        {
            Provider = connection.Provider,
            PsaConnectionId = connection.Id,
            ClientCompanyId = company.Id,
            QueueOrBoardKey = ticket.QueueOrBoard,
        };

        var request = new UnifiedTicketCreateRequest
        {
            Title = ticket.Title,
            Description = ticket.Description,
            Status = MapOut(rules, ctx, "status", ticket.PortalStatus),
            Priority = MapOut(rules, ctx, "priority", ticket.PortalPriority),
            Category = MapOut(rules, ctx, "category", ticket.PortalCategory),
            // Re-read at retry time, which is the point: the commonest cause of a rejected create is
            // a queue the connection had not been given yet. Configure the default, press resync,
            // and it goes through with no re-entry.
            QueueOrBoard = ticket.QueueOrBoard is not null
                ? MapOut(rules, ctx, "queue", ticket.QueueOrBoard)
                : connection.DefaultQueueOrBoardId,
            TicketType = connection.DefaultTicketType,
            IssueType = connection.DefaultIssueType,
            SubIssueType = connection.DefaultSubIssueType,
            ExternalCompanyId = company.ExternalCompanyId,
            RequesterEmail = ticket.RequesterEmail,
            // Reuses the ticket's own correlation id, so a provider that honours idempotency keys
            // recognises a retry of the same ticket instead of opening a second one.
            IdempotencyKey = ticket.CorrelationId.ToString("N"),
        };

        CreateTicketResult created;
        try
        {
            var connector = await connectors.ResolveAsync(connection.Id, ct);
            created = await connector.CreateTicketAsync(request, ct);
        }
        catch (ConnectorException ex)
        {
            created = new CreateTicketResult(false, null, ex.Message);
        }

        if (!created.Success)
        {
            ticket.SyncStatus = TicketSyncStatus.Error;
            ticket.SyncError = created.Error;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("ticket.resync_failed", "Ticket", ticket.Id.ToString(),
                new { ticket.Title, created.Error }, ct);
            return new ResyncResultDto(false, ticket.Id, null, created.Error);
        }

        ticket.ExternalTicketId = created.ExternalId;
        ticket.SyncStatus = TicketSyncStatus.Synced;
        ticket.SyncError = null;
        ticket.LastSyncedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("ticket.resynced", "Ticket", ticket.Id.ToString(),
            new { ticket.Title, ExternalTicketId = created.ExternalId }, ct);
        return new ResyncResultDto(true, ticket.Id, created.ExternalId, null);
    }

    private string? MapOut(List<FieldMapping> rules, MappingContext ctx, string field, string? value)
    {
        if (value is null) return null;
        var r = mapping.MapToProvider(rules, ctx, field, value);
        return r.Resolved ? r.Value : value; // passthrough when unmapped, as the create path does
    }
}
