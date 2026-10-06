using Desk.Application.Abstractions;
using Desk.Application.Tickets;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Secrets;
using Desk.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Desk.Tests.Unit;

/// <summary>
/// Passes every query through unchanged. Used by tests exercising the CLIENT-scoped ticket paths
/// (which have their own, separate ClientAccess predicate) so they don't have to care about the
/// staff scope-resolution machinery their scenario never touches.
/// </summary>
public sealed class NoopTicketScopeQuery : ITicketScopeQuery
{
    public Task<IQueryable<Ticket>> VisibleAsync(IQueryable<Ticket> source, Guid appUserId, string permissionKey, CancellationToken ct = default)
        => Task.FromResult(source);

    public async Task<Ticket?> FindAsync(IQueryable<Ticket> source, Guid ticketId, Guid appUserId, string permissionKey, CancellationToken ct = default)
        => await source.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
}

/// <summary>
/// Stub authenticated user for admin service tests.
///
/// By default this grants every permission, because most tests exercise a service's own logic and
/// not its gate. Pass <paramref name="permissions"/> to model a real, limited caller — which is the
/// only way to test that something is DENIED, as opposed to merely present.
/// </summary>
public sealed class TestCurrentUser(
    Guid? org,
    string subject = "admin-sub",
    string name = "Admin User",
    IReadOnlySet<string>? permissions = null,
    string? technicianExternalId = null,
    Guid? userId = null) : ICurrentUser
{
    public bool IsAuthenticated => true;
    public string? Subject => subject;
    public string? Email => "admin@test";
    public string? DisplayName => name;
    public Guid? OrganizationId => org;
    public Guid? UserId => userId;
    public string? TechnicianExternalId => technicianExternalId;
    public IReadOnlySet<string> Permissions => permissions ?? new HashSet<string>();

    /// <summary>Grants everything unless an explicit permission set was supplied.</summary>
    public bool HasPermission(string permissionKey)
        => permissions is null || permissions.Contains(permissionKey);
}

/// <summary>
/// Wires a DeskDbContext, tenant context, current user, secret store, and audit writer that all
/// share the same tenant scope — mirroring how a real admin request is composed.
/// </summary>
public sealed class AdminHarness
{
    public required DeskDbContext Db { get; init; }
    public required TenantContext Tenant { get; init; }
    public required TestCurrentUser User { get; init; }
    public required InMemorySecretStore Secrets { get; init; }
    public required TestClock Clock { get; init; }

    public static AdminHarness Create(Guid org, string? dbName = null)
    {
        var tenant = new TenantContext();
        tenant.SetTenant(org);
        var clock = new TestClock();
        var options = new DbContextOptionsBuilder<DeskDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        var db = new DeskDbContext(options, tenant, clock);
        return new AdminHarness
        {
            Db = db, Tenant = tenant, User = new TestCurrentUser(org),
            Secrets = new InMemorySecretStore(), Clock = clock,
        };
    }

    /// <summary>
    /// The database <see cref="Create"/> opens under <paramref name="dbName"/>, read and written
    /// across tenants - for seeding another organization's rows beside the harness's own.
    ///
    /// Built with the same options as Create, on purpose. The in-memory provider keeps its stores
    /// per options shape: a context configured any differently (TestDbContextFactory turns on
    /// sensitive-data logging) opens a different, empty database of the same name, and a test that
    /// "cannot see the other tenant's row" then proves nothing - the row was never there.
    /// </summary>
    public static DeskDbContext Platform(string dbName)
    {
        var tenant = new TenantContext();
        tenant.SetPlatformScope();
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseInMemoryDatabase(dbName).Options;
        return new DeskDbContext(options, tenant, new TestClock());
    }
}
