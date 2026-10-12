using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Saying what a PSA's values become in the portal.
///
/// A PSA has many statuses and the portal has six, so many arrive as one. The mapping page could
/// save one rule per portal status - the one the portal SENDS - and the API matched a rule by its
/// portal value, so a second PSA status for "in progress" overwrote the first. The rules that map
/// the other PSA statuses in production were not made on that page, which could not make them.
/// These hold the way that is now done through the product: any number of PSA values to one portal value, the half that says
/// what the portal sends left alone, and each change recorded with what it replaced.
/// </summary>
public class MappingInboundTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record World(AdminHarness H, MappingAdminService Service, Guid Connection, Guid Other, string DbName);

    private static async Task<World> BuildAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = AdminHarness.Create(Org, dbName);
        PsaConnection Of(string name) => new()
        {
            MspOrganizationId = Org, Name = name, Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://webservices31.autotask.net/ATServicesRest/",
            CredentialSecretRef = "mem://" + name,
        };
        var main = Of("Main");
        var other = Of("Other account");
        h.Db.PsaConnections.AddRange(main, other);
        await h.Db.SaveChangesAsync();
        return new World(h, new MappingAdminService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.User), main.Id, other.Id, dbName);
    }

    private static FieldMapping Rule(Guid connection, string external, string portal, MappingDirection direction = MappingDirection.Bidirectional) => new()
    {
        MspOrganizationId = Org, Provider = ProviderType.AutotaskPsa, Scope = MappingScope.ConnectionOverride, PsaConnectionId = connection,
        PortalField = "status", ExternalField = "status", ExternalValue = external, PortalValue = portal, Direction = direction,
    };

    /// <summary>What the sync and the status writer would get, asked of the engine with the connection's rules.</summary>
    private static async Task<(Func<string, string?> Arrives, Func<string, string?> Sends)> EngineAsync(World w, Guid connection)
    {
        var rules = await ConnectionMappingRules.LoadAsync(w.H.Db, Org, ProviderType.AutotaskPsa, connection);
        var engine = new MappingEngine();
        var context = new MappingContext { Provider = ProviderType.AutotaskPsa, PsaConnectionId = connection };
        return (
            value => engine.MapToPortal(rules, context, "status", value) is { Resolved: true } hit ? hit.Value : null,
            value => engine.MapToProvider(rules, context, "status", value) is { Resolved: true } hit ? hit.Value : null);
    }

    [Fact]
    public async Task Many_values_from_the_PSA_can_arrive_as_one_portal_value()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;

        var result = await w.Service.SetInboundAsync(w.Connection,
        [
            new("status", "In Progress", "IN_PROGRESS"), new("status", "Dispatched", "IN_PROGRESS"),
            new("status", " SOC Updated ", "IN_PROGRESS"), new("priority", "Medium", "NORMAL"),
        ], null);

        result.Changed.Should().Be(4);
        var (arrives, _) = await EngineAsync(w, w.Connection);
        (arrives("In Progress"), arrives("Dispatched"), arrives("SOC Updated")).Should().Be(("IN_PROGRESS", "IN_PROGRESS", "IN_PROGRESS"),
            "the second and third used to overwrite the first: a rule was found by its portal value");
        (await w.H.Db.FieldMappings.CountAsync(m => m.PortalField == "status" && m.PortalValue == "IN_PROGRESS")).Should().Be(3);
        (await w.H.Db.FieldMappings.SingleAsync(m => m.ExternalValue == "SOC Updated")).Direction.Should().Be(MappingDirection.ProviderToPortal);
        (await w.H.Db.FieldMappingVersions.CountAsync()).Should().Be(1, "saved together is one version, not four");
    }

    [Fact]
    public async Task Changing_what_a_value_arrives_as_leaves_what_the_portal_sends()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        // One rule both ways, as the mapping page writes it: CLOSED goes out as Complete, and Complete arrives as CLOSED.
        w.H.Db.FieldMappings.Add(Rule(w.Connection, "Complete", "CLOSED"));
        await w.H.Db.SaveChangesAsync();

        var result = await w.Service.SetInboundAsync(w.Connection, [new("status", "Complete", "RESOLVED")], "Complete is resolved here");

        result.Changes.Should().Equal("status: Complete → RESOLVED (was CLOSED)");
        var (arrives, sends) = await EngineAsync(w, w.Connection);
        arrives("Complete").Should().Be("RESOLVED");
        sends("CLOSED").Should().Be("Complete", "what the portal sends for CLOSED was not what was being changed");
        (await w.H.Db.FieldMappings.Where(m => m.ExternalValue == "Complete").Select(m => new { m.PortalValue, m.Direction }).ToListAsync())
            .Should().BeEquivalentTo(new[]
            {
                new { PortalValue = (string?)"CLOSED", Direction = MappingDirection.PortalToProvider },
                new { PortalValue = (string?)"RESOLVED", Direction = MappingDirection.ProviderToPortal },
            });

        // Saying the same thing again changes nothing, and records nothing.
        (await w.Service.SetInboundAsync(w.Connection, [new("status", "Complete", "RESOLVED")], null)).Changed.Should().Be(0);
        (await w.H.Db.FieldMappingVersions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_mapping_can_be_taken_away_and_the_value_then_arrives_as_the_PSA_wrote_it()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.FieldMappings.AddRange(Rule(w.Connection, "Complete", "CLOSED"), Rule(w.Connection, "Work Complete", "RESOLVED", MappingDirection.ProviderToPortal));
        await w.H.Db.SaveChangesAsync();

        await w.Service.SetInboundAsync(w.Connection, [new("status", "Complete", null), new("status", "Work Complete", null)], null);

        var (arrives, sends) = await EngineAsync(w, w.Connection);
        (arrives("Complete"), arrives("Work Complete")).Should().Be(((string?)null, (string?)null));
        sends("CLOSED").Should().Be("Complete");
        (await w.H.Db.FieldMappings.CountAsync(m => m.ExternalValue == "Work Complete")).Should().Be(0);
    }

    [Fact]
    public async Task Two_rules_for_one_arriving_value_are_made_one()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        // As could be written before: which of these decided what "Complete" arrived as was not anyone's choice.
        w.H.Db.FieldMappings.AddRange(Rule(w.Connection, "Complete", "CLOSED"), Rule(w.Connection, "complete", "RESOLVED", MappingDirection.ProviderToPortal));
        await w.H.Db.SaveChangesAsync();

        await w.Service.SetInboundAsync(w.Connection, [new("status", "Complete", "CLOSED")], null);

        var rules = await w.H.Db.FieldMappings.Where(m => m.ExternalValue!.ToLower() == "complete").ToListAsync();
        rules.Count(r => r.Direction is MappingDirection.Bidirectional or MappingDirection.ProviderToPortal).Should().Be(1);
        (await EngineAsync(w, w.Connection)).Arrives("Complete").Should().Be("CLOSED");
    }

    [Fact]
    public async Task Only_this_connections_rules_are_touched_and_another_tenants_connection_is_not_found()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.FieldMappings.Add(Rule(w.Other, "Complete", "CLOSED"));
        await w.H.Db.SaveChangesAsync();

        await w.Service.SetInboundAsync(w.Connection, [new("status", "Complete", "RESOLVED")], null);

        (await EngineAsync(w, w.Other)).Arrives("Complete").Should().Be("CLOSED", "the other PSA account says what its own Complete means");
        (await EngineAsync(w, w.Connection)).Arrives("Complete").Should().Be("RESOLVED");

        // Someone in another organization, in the same database, naming this connection by its id.
        var elsewhere = AdminHarness.Create(Guid.NewGuid(), w.DbName);
        (await AdminHarness.Platform(w.DbName).PsaConnections.AnyAsync(c => c.Id == w.Connection)).Should().BeTrue("the row is there for them to name");
        var theirs = new MappingAdminService(elsewhere.Db, new AuditWriter(elsewhere.Db, elsewhere.User, elsewhere.Tenant, elsewhere.Clock), elsewhere.User);
        await ((Func<Task>)(() => theirs.SetInboundAsync(w.Connection, [new("status", "Complete", "NEW")], null))).Should().ThrowAsync<NotFoundException>();
        (await EngineAsync(w, w.Connection)).Arrives("Complete").Should().Be("RESOLVED", "and nothing of this organization's changed");
    }

    [Fact]
    public async Task Every_problem_is_said_at_once_and_nothing_is_changed()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;

        var act = () => w.Service.SetInboundAsync(w.Connection,
        [
            new("status", "Complete", "DONE"), new("queue", "Service Desk", "NEW"), new("status", " ", "NEW"),
            new("status", "New", "NEW"), new("status", "new", "CLOSED"), new("status", "Fine", "CLOSED"),
        ], null);

        var problem = (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message;
        problem.Should().Contain("\"DONE\" is not one of the portal's status values")
            .And.Contain("\"queue\" is not a field")
            .And.Contain("missing its value")
            .And.Contain("\"New\" is given two answers");
        (await w.H.Db.FieldMappings.CountAsync()).Should().Be(0, "the one good line among them was not saved on its own");
    }

    [Fact]
    public async Task Each_change_is_recorded_with_what_it_replaced()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        await w.Service.SetInboundAsync(w.Connection, [new("status", "Escalate", "ON_HOLD")], null);

        await w.Service.SetInboundAsync(w.Connection, [new("status", "Escalate", "IN_PROGRESS"), new("status", "Dispatched", "IN_PROGRESS")], "after the service review");

        var entries = await w.H.Db.AuditLog.Where(a => a.Action == "mapping.inbound.changed").OrderBy(a => a.CreatedAt).ToListAsync();
        entries.Should().HaveCount(2).And.OnlyContain(e => e.EntityId == w.Connection.ToString());
        entries[1].DetailJson.Should().Contain("status: Escalate \\u2192 IN_PROGRESS (was ON_HOLD)").And.Contain("status: Dispatched \\u2192 IN_PROGRESS (was not mapped)").And.Contain("Main");
        (await w.H.Db.FieldMappingVersions.OrderBy(v => v.Version).Select(v => v.ChangeNote).ToListAsync())
            .Should().Equal("1 value from the PSA mapped", "after the service review");
    }
}
