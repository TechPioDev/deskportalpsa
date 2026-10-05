using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// The mapping rules that may decide a value for one connection: its own organization's rules for
/// its provider, and of those only the ones written for this connection or for no connection in
/// particular.
///
/// Every caller that maps a value loads its rules here. The organization is named in the query
/// rather than left to the global tenant filter, because the scheduled sync runs under platform
/// scope, where that filter is off. Filtered by provider alone - as each caller used to do - the
/// worker read every organization's rules, so a rule saved by one tenant could decide how another
/// tenant's tickets were mapped.
/// </summary>
public static class ConnectionMappingRules
{
    public static Task<List<FieldMapping>> LoadAsync(
        DeskDbContext db, Guid organizationId, ProviderType provider, Guid connectionId, CancellationToken ct = default)
        => db.FieldMappings.AsNoTracking()
            .Where(m => m.MspOrganizationId == organizationId
                        && m.Provider == provider
                        && m.IsActive
                        && (m.PsaConnectionId == null || m.PsaConnectionId == connectionId))
            .ToListAsync(ct);
}
