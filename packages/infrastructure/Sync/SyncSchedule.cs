using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;

namespace Desk.Infrastructure.Sync;

/// <summary>Which connections the scheduled sync reads from. One rule, in one place, so it can be tested.</summary>
public static class SyncSchedule
{
    /// <summary>
    /// Switched on, not failed its last test, not paused, not put away - and not locked out. A
    /// connection whose credentials the PSA has rejected is left alone until someone enters ones
    /// that pass a test: asking again every five minutes with the same rejected key is how an API
    /// account gets locked.
    /// </summary>
    public static IQueryable<PsaConnection> Due(IQueryable<PsaConnection> connections)
        => connections.Where(c => c.IsEnabled && c.Status != ConnectionStatus.Failed
                                  && c.SyncPausedAt == null && c.ArchivedAt == null
                                  && c.LastErrorKind != ConnectionStates.Authentication);
}
