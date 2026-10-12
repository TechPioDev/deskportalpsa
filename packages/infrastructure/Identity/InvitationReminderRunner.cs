using Desk.Application.Abstractions;
using Desk.Application.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Identity;

/// <summary>
/// One turn of reminders and expiry notices, for every organization, each inside its own scope
/// so that what is mailed and logged belongs to that organization.
/// </summary>
public sealed class InvitationReminderRunner(IServiceScopeFactory scopes, ILogger<InvitationReminderRunner> logger)
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
                sent += await scope.ServiceProvider.GetRequiredService<IInvitationService>().SendRemindersAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Invitation reminders failed for organization {Organization}", org);
            }
        }
        return sent;
    }
}
