using Desk.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// One turn of sending what is waiting for a PSA, for every organization.
///
/// Which changes are due is asked once, with every organization in view. Each is then taken in a
/// unit of work of its own that is scoped to the organization it belongs to, as a request from
/// that organization would be: what is read while sending it is that organization's, and what is
/// written (the audit entry above all) carries that organization, where a unit of work with every
/// organization in view would write it belonging to none.
///
/// A unit of work each also means a change that goes wrong in a way nobody foresaw leaves nothing
/// half-done for the next one to save. That try is counted, on a further unit of work, so such a
/// change runs out of tries like any other.
/// </summary>
public sealed class OutboundRunner(IServiceScopeFactory scopes, ILogger<OutboundRunner> logger)
{
    /// <summary>Tries what is due, oldest first. Returns how many were tried.</summary>
    public async Task<int> RunDueAsync(int take = 25, CancellationToken ct = default)
    {
        List<OutboundDue> due;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            due = await scope.ServiceProvider.GetRequiredService<OutboundQueue>().DueWithOrganizationAsync(take, ct);
        }

        var tried = 0;
        foreach (var change in due)
        {
            try
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(change.MspOrganizationId);
                if (await scope.ServiceProvider.GetRequiredService<OutboundProcessor>().ProcessAsync(change.Id, ct) is not null) tried++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbound change {OperationId} failed for a reason of the portal's own", change.Id);
                try
                {
                    using var scope = scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(change.MspOrganizationId);
                    await scope.ServiceProvider.GetRequiredService<OutboundProcessor>().CrashedAsync(change.Id, ct);
                    tried++;
                }
                catch (Exception again) when (again is not OperationCanceledException)
                {
                    // Not even the count could be kept (the database is away, say). The change is
                    // still there and still pending; it is taken again when its hold runs out.
                    logger.LogError(again, "Outbound change {OperationId}: the failed try could not be recorded", change.Id);
                }
            }
        }
        return tried;
    }
}
