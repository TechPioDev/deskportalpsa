using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// What to call a ticket's source in its reference. "Autotask 12345" says which ticket only while
/// there is one Autotask account: with two, both can have a ticket 12345. So where an organization
/// has more than one connection to the same PSA, a ticket from one of them is named by its
/// connection ("Customer A 12345") - and where it has one, nothing changes.
///
/// Archived connections count: their tickets are still here, and still need telling apart.
/// </summary>
public sealed class ReferenceLabels
{
    public static readonly ReferenceLabels None = new(new Dictionary<Guid, string>());

    private readonly IReadOnlyDictionary<Guid, string> _names;

    private ReferenceLabels(IReadOnlyDictionary<Guid, string> names) => _names = names;

    public static async Task<ReferenceLabels> LoadAsync(DeskDbContext db, CancellationToken ct = default)
    {
        var connections = await db.PsaConnections.AsNoTracking()
            .Select(c => new { c.Id, c.MspOrganizationId, c.Provider, c.Name })
            .ToListAsync(ct);
        return From(connections.Select(c => (c.Id, c.MspOrganizationId, c.Provider, c.Name)));
    }

    /// <summary>From connections a caller has already read, so that naming a reference costs it no query of its own.</summary>
    public static ReferenceLabels From(IEnumerable<(Guid Id, Guid OrganizationId, Desk.Domain.Enums.ProviderType Provider, string Name)> connections)
        // By organization too: a background caller reads every tenant's connections at once, and
        // one tenant's second Autotask account says nothing about another's first.
        => new(connections
            .GroupBy(c => (c.OrganizationId, c.Provider))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToDictionary(c => c.Id, c => c.Name));

    /// <summary>
    /// Whether a ticket's reference depends on its connection at all. The team's own tickets carry a
    /// number of their own, so a page of them need not read the connections to name them.
    /// </summary>
    public static bool Needed(string? number, Desk.Domain.Enums.ProviderType? provider) => number is null && provider is not null;

    /// <summary>The connection's name where the PSA's own would not say which account; null where it would.</summary>
    public string? For(Guid? connectionId) => connectionId is { } id && _names.TryGetValue(id, out var name) ? name : null;
}
