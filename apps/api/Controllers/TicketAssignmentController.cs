using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// Staff ownership changes: who the ticket sits with, and which queue/board it belongs to. The PSA
/// stays the system of record — the change is pushed first and the portal projection only follows a
/// success, so the portal can never claim an assignment the PSA rejected.
/// </summary>
// Class-level [Authorize] as a floor: every action here also carries [RequirePermission],
// but that is opt-in per action — an action added later without one would otherwise be
// reachable anonymously. This makes authentication the default and the omission harmless.
[Authorize]
[ApiController]
[Route("api/tickets")]
public sealed class TicketAssignmentController(
    DeskDbContext db,
    IConnectorResolver connectors,
    IConnectionAdminService admin,
    IMappingEngine mapping,
    IAuditWriter audit,
    ITicketScopeQuery scopeQuery,
    ICurrentUser user) : ControllerBase
{
    /// <summary>
    /// Who can take this ticket, and in what role. Narrowed to the technicians who actually cover
    /// the ticket's queue when the provider models that; everyone otherwise, since offering nobody
    /// would be worse than offering too many.
    /// </summary>
    [HttpGet("{id:guid}/assignees")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Assignees(Guid id, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        var fields = await admin.GetFieldsAsync(ticket.PsaConnectionId, ct);
        var queueId = await ResolveQueueIdAsync(ticket, fields, ct);

        // Coverage rows carry a queue when the technician is scoped to one, and null when they cover
        // the department at large — the latter applies to every queue, so it is never filtered out.
        var covering = fields.TechnicianCoverage
            .Where(c => queueId is null || c.QueueOrBoardId is null || c.QueueOrBoardId == queueId)
            .ToList();

        var rolesByTechnician = covering
            .GroupBy(c => c.TechnicianId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.RoleName).Where(n => n is not null).Distinct().ToList());

        // A technician with no role at all cannot take work in the PSA — Autotask's own API-only
        // users are the clearest case — so offering them would only produce a rejection later.
        // Nor the account the portal writes as: assigning a ticket to it in the PSA assigns it to
        // nobody. It is the integration's login, not a technician who will pick the work up.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var candidates = fields.Technicians
            .Where(t => rolesByTechnician.Count == 0 || rolesByTechnician.ContainsKey(t.Value))
            .Where(t => !account.IsAccount(ticket.PsaConnectionId, t.Value))
            .Select(t => new
            {
                id = t.Value,
                name = t.Label,
                // Both lists come from the same source, so the summary beside a name cannot advertise
                // a role the picker will not offer and the provider will not accept.
                roles = RolesFor(fields, t.Value, queueId).Select(r => r.RoleName ?? r.RoleId!).ToList(),
                roleOptions = RolesFor(fields, t.Value, queueId)
                    .Select(r => new { id = r.RoleId!, name = r.RoleName ?? r.RoleId! })
                    .ToList(),
            })
            .OrderBy(t => t.name)
            .ToList();

        // Portal technicians, offered alongside the PSA's own. Not merged into one list: they are
        // different things and the difference matters to whoever is choosing. Assigning a PSA
        // technician changes the ticket in the provider and their name appears there; assigning a
        // portal technician does not leave this system. A single blended list would make that
        // invisible at exactly the moment someone decides.
        var portalTechnicians = await db.AppUsers.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { id = u.Id, name = u.DisplayName, email = u.Email })
            .ToListAsync(ct);

        return Ok(new
        {
            queueOrBoardId = queueId,
            portalTechnicians,
            // Reported separately: narrowing by role and narrowing to this queue are different
            // promises, and claiming the stronger one when only the weaker happened misleads.
            filteredByRole = rolesByTechnician.Count > 0,
            filteredByQueue = rolesByTechnician.Count > 0 && queueId is not null
                              && covering.Any(c => c.QueueOrBoardId == queueId),
            queuesOrBoards = fields.QueuesOrBoards.Select(o => new { o.Value, o.Label }),
            technicians = candidates,
        });
    }

    [HttpPut("{id:guid}/assignment")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignRequest req, CancellationToken ct)
    {
        var ticket = await LoadAsync(id, ct);
        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new ValidationFailedException("This ticket is not yet synced to the PSA.");

        var technicianId = Blank(req.TechnicianExternalId);
        var queueId = Blank(req.QueueOrBoardId);
        if (technicianId is null && queueId is null && req.AppUserId is null)
            throw new ValidationFailedException("Choose a technician or a queue to change.");

        // A portal assignee is recorded here and NEVER pushed. The person has no resource in the
        // PSA — that is what "portal technician" means — so there is nothing to send and any
        // attempt would be rejected. It is also the point: work reaches the provider under the
        // integration's identity, and their name stays inside the portal.
        //
        // Done before the provider call so that assigning a portal technician does not depend on
        // the PSA being reachable. The two assignments are independent facts and a failure to
        // change one must not silently discard the other.
        if (req.AppUserId is { } appUserId)
        {
            var assignee = await db.AppUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == appUserId && u.IsActive, ct)
                ?? throw new ValidationFailedException("That portal user does not exist, or is not active.");

            ticket.AssignedAppUserId = assignee.Id;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("ticket.assigned.portal", "Ticket", ticket.Id.ToString(),
                new { ticket.ExternalTicketId, appUserId = assignee.Id, assignee.DisplayName }, ct);

            // Nothing else was asked for, so nothing else should be attempted — in particular not a
            // provider round trip that could fail and report an error for work that succeeded.
            if (technicianId is null && queueId is null)
                return Ok(new
                {
                    assignedAppUserId = ticket.AssignedAppUserId,
                    assignedAppUserName = assignee.DisplayName,
                    assignedTechnicianExternalId = ticket.AssignedTechnicianExternalId,
                    assignedTechnicianName = ticket.AssignedTechnicianName,
                    queueOrBoard = ticket.QueueOrBoard,
                });
        }

        var fields = await admin.GetFieldsAsync(ticket.PsaConnectionId, ct);

        // Reject an assignment the provider would refuse anyway, with a reason a human can act on.
        if (technicianId is not null && fields.Technicians.Count > 0
            && fields.Technicians.All(t => t.Value != technicianId))
            throw new ValidationFailedException("That technician is not available on this connection.");

        // The role is not optional in every PSA — Autotask refuses a resource without one — so it is
        // resolved here rather than pushed onto the caller: their choice, else the role that
        // technician actually holds on this ticket's queue.
        string? roleId = null;
        if (technicianId is not null)
        {
            roleId = Blank(req.RoleId) ?? await ResolveRoleIdAsync(ticket, fields, technicianId, ct);
            if (roleId is null && fields.TechnicianCoverage.Count > 0)
                throw new ValidationFailedException(
                    "That technician holds no role on this queue, so the PSA will not accept the assignment.");
        }

        var update = new UnifiedTicketUpdate
        {
            AssignedTechnicianExternalId = technicianId,
            AssignedTechnicianRoleId = roleId,
            QueueOrBoard = queueId,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
        };

        var connector = await connectors.ResolveAsync(ticket.PsaConnectionId, ct);
        var result = await connector.UpdateTicketAsync(ticket.ExternalTicketId, update, ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the assignment.");

        if (technicianId is not null)
        {
            ticket.AssignedTechnicianExternalId = technicianId;
            // Cache the name so a detail read never has to call the provider for a byline.
            ticket.AssignedTechnicianName = fields.Technicians.FirstOrDefault(t => t.Value == technicianId)?.Label;
        }
        if (queueId is not null)
        {
            // Store the PORTAL-side value, exactly as an inbound sync would, so the projection does
            // not flip between "Support" and "Level I Support" depending on which wrote it last.
            var rules = await db.FieldMappings.AsNoTracking()
                .Where(m => m.Provider == ticket.Provider && m.IsActive).ToListAsync(ct);
            var ctx = new MappingContext { Provider = ticket.Provider, PsaConnectionId = ticket.PsaConnectionId };
            ticket.QueueOrBoard = mapping.MapToPortal(rules, ctx, "queue", queueId).Value
                ?? fields.QueuesOrBoards.FirstOrDefault(o => o.Value == queueId)?.Label
                ?? queueId;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("ticket.assigned", "Ticket", ticket.Id.ToString(),
            new { ticket.ExternalTicketId, technicianId, queueId }, ct);

        return Ok(new
        {
            assignedAppUserId = ticket.AssignedAppUserId,
            assignedTechnicianExternalId = ticket.AssignedTechnicianExternalId,
            assignedTechnicianName = ticket.AssignedTechnicianName,
            queueOrBoard = ticket.QueueOrBoard,
        });
    }

    /// <summary>
    /// The role this technician works the ticket's queue in. Queue-scoped coverage wins over
    /// department-wide, so a technician who is an Engineer here and an Administrator elsewhere is
    /// assigned as an Engineer.
    /// </summary>
    private async Task<string?> ResolveRoleIdAsync(Desk.Domain.Tickets.Ticket ticket, ConnectionFieldsDto fields, string technicianId, CancellationToken ct)
    {
        var queueId = await ResolveQueueIdAsync(ticket, fields, ct);
        return RolesFor(fields, technicianId, queueId).FirstOrDefault()?.RoleId;
    }

    /// <summary>
    /// The roles a technician may actually take a ticket on this queue in. Queue-scoped coverage is
    /// authoritative when it exists: Autotask rejects a resource/role pair that is not defined for
    /// the queue ("combination is not currently defined"), even when the technician holds that role
    /// department-wide — so offering the wider set would only produce failures at save time.
    /// </summary>
    private static List<TechnicianCoverageDto> RolesFor(ConnectionFieldsDto fields, string technicianId, string? queueId)
    {
        var mine = fields.TechnicianCoverage
            .Where(c => c.TechnicianId == technicianId && c.RoleId is not null)
            .ToList();

        var scoped = queueId is null ? [] : mine.Where(c => c.QueueOrBoardId == queueId).ToList();
        var usable = scoped.Count > 0 ? scoped : mine.Where(c => c.QueueOrBoardId is null).ToList();
        if (usable.Count == 0) usable = mine;

        return usable.GroupBy(c => c.RoleId!).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Maps the ticket's stored queue to the provider id that coverage is keyed by. The projection
    /// holds the PORTAL-side value ("Support"), which need not resemble the provider's own label
    /// ("Level I Support") — so the field mapping rules are consulted first, exactly as a status
    /// change does. Direct id/label matches are still honoured for connections with no queue rule.
    /// </summary>
    private async Task<string?> ResolveQueueIdAsync(Desk.Domain.Tickets.Ticket ticket, ConnectionFieldsDto fields, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket.QueueOrBoard)) return null;

        var rules = await db.FieldMappings.AsNoTracking()
            .Where(m => m.Provider == ticket.Provider && m.IsActive).ToListAsync(ct);
        var ctx = new MappingContext { Provider = ticket.Provider, PsaConnectionId = ticket.PsaConnectionId };
        var mapped = mapping.MapToProvider(rules, ctx, "queue", ticket.QueueOrBoard).Value;
        if (!string.IsNullOrWhiteSpace(mapped)
            && fields.QueuesOrBoards.Any(o => o.Value == mapped)) return mapped;

        return fields.QueuesOrBoards.FirstOrDefault(o =>
            o.Value == ticket.QueueOrBoard
            || string.Equals(o.Label, ticket.QueueOrBoard, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private async Task<Desk.Domain.Tickets.Ticket> LoadAsync(Guid id, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        return await scopeQuery.FindAsync(db.Tickets, id, uid, Permissions.TicketsUpdate, ct)
            ?? throw new NotFoundException("Ticket");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <param name="RoleId">Optional: left unset, the technician's role on this queue is used.</param>
    /// <param name="AppUserId">
    /// A PORTAL technician to give the ticket to. Independent of <paramref name="TechnicianExternalId"/>
    /// rather than an alternative to it — a desk running both kinds of technician needs to say "the
    /// PSA has it on the integration account, and Basit is working it" in one sentence.
    /// </param>
    public sealed record AssignRequest(
        string? TechnicianExternalId, string? QueueOrBoardId, string? RoleId = null, Guid? AppUserId = null);
}
