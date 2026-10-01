using System.Text.Json;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Domain.Audit;
using Desk.Infrastructure.Persistence;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// Appends immutable audit entries. The actor and tenant come from the current request context; the
/// detail is serialized as-is, so callers must pass only non-secret data (connection creation logs
/// name/provider/endpoint, never credentials).
/// </summary>
public sealed class AuditWriter(DeskDbContext db, ICurrentUser user, ITenantContext tenant, TimeProvider clock)
    : IAuditWriter
{
    public async Task WriteAsync(string action, string entityType, string? entityId, object? detail = null, CancellationToken ct = default)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            MspOrganizationId = tenant.OrganizationId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            // Viewing as someone is read-only, but anything recorded meanwhile was the administrator's.
            ActorUserId = user.ViewedByUserId is { } by ? by.ToString() : user.Subject,
            ActorDisplayName = user.ViewedByUserId is null ? user.DisplayName : $"{user.ViewedByName} (viewing as {user.DisplayName})",
            CreatedAt = clock.GetUtcNow(),
            DetailJson = detail is null ? null : JsonSerializer.Serialize(detail),
        });
        await db.SaveChangesAsync(ct);
    }
}
