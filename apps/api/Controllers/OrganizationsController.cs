using Desk.Domain.Authorization;
using Desk.Api.Auth;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// Demonstrates the full guard stack: a permission-gated, tenant-scoped read. The query below
/// carries no explicit tenant predicate — the DbContext global filter constrains it to the
/// caller's organization automatically.
/// </summary>
[ApiController]
[Route("api/organizations")]
public sealed class OrganizationsController(DeskDbContext db) : ControllerBase
{
    [HttpGet("connections")]
    [RequirePermission(Permissions.ConnectionsView)]
    public async Task<IActionResult> GetConnections(CancellationToken ct)
    {
        var connections = await db.PsaConnections
            .AsNoTracking()
            .Where(c => c.ArchivedAt == null)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Provider,
                c.Status,
                c.IsEnabled,
                c.LastSuccessfulSyncAt,
                // NOTE: CredentialSecretRef is deliberately never projected to the client.
            })
            .ToListAsync(ct);

        return Ok(connections);
    }
}
