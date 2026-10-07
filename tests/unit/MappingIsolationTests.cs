using Desk.Application.Admin;
using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A mapping rule decides values for the connection it was written for, in the organization that
/// wrote it, and nowhere else.
///
/// The scheduled sync runs under platform scope, where the tenant filter is off. It used to load
/// rules by provider alone, so it read every organization's rules; and only a rule at connection
/// scope was held to its connection. One tenant's rule could therefore decide how another tenant's
/// tickets were mapped. Nothing tested a sync with two organizations, or two accounts of one PSA.
/// </summary>
public class MappingIsolationTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid ConnA = Guid.NewGuid();
    private static readonly Guid ConnA2 = Guid.NewGuid();
    private static readonly Guid ConnB = Guid.NewGuid();

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static PsaConnection Connection(Guid id, Guid org, string name) => new()
    {
        Id = id, MspOrganizationId = org, Name = name, Provider = ProviderType.AutotaskPsa,
        ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", SyncAttachments = false, ImportNotes = false,
    };

    private static FieldMapping StatusRule(Guid org, MappingScope scope, Guid? connection, string external, string portal,
        string? queueKey = null) => new()
    {
        MspOrganizationId = org, Provider = ProviderType.AutotaskPsa, Scope = scope, PsaConnectionId = connection,
        QueueOrBoardKey = queueKey, PortalField = "status", ExternalField = "status",
        ExternalValue = external, PortalValue = portal, Direction = MappingDirection.Bidirectional,
    };

    /// <summary>Both organizations in one database, read as the worker reads it: with the tenant filter off.</summary>
    private static async Task<DeskDbContext> TwoOrganizationsAsync(params FieldMapping[] rules)
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.AddRange(
            Connection(ConnA, OrgA, "A - Autotask"),
            Connection(ConnA2, OrgA, "A - second Autotask account"),
            Connection(ConnB, OrgB, "B - Autotask"));
        db.FieldMappings.AddRange(rules);
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<string> SyncedStatusAsync(DeskDbContext db, Guid connectionId, string providerStatus)
    {
        var clock = new TestClock();
        var connector = new StubConnector();
        connector.Tickets.Add(new UnifiedTicket
        {
            ExternalId = "100", Title = "Printer", Status = providerStatus, Priority = "Medium",
            RequesterExternalId = "co-1", CompanyName = "Acme", ModifiedAt = clock.GetUtcNow(),
        });
        var runner = new ConnectionSyncRunner(db, new FakeResolver(connector),
            new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, clock), clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), clock), new HeuristicMalwareScanner(), clock);

        await runner.RunAsync(connectionId);

        return (await db.Tickets.AsNoTracking().SingleAsync(t => t.PsaConnectionId == connectionId)).PortalStatus;
    }

    [Fact]
    public async Task The_scheduled_sync_does_not_apply_another_organizations_provider_wide_rule()
    {
        // Organization A says every Autotask "Open" is closed. That is A's business. B has no rule
        // for "Open" at all, so B's ticket must come through as the provider sent it.
        await using var db = await TwoOrganizationsAsync(
            StatusRule(OrgA, MappingScope.ProviderDefault, null, "Open", "CLOSED"));

        (await SyncedStatusAsync(db, ConnB, "Open")).Should().Be("Open");
    }

    [Fact]
    public async Task A_rule_filed_against_another_organizations_connection_is_never_loaded_for_it()
    {
        // The forged case: A holds a rule that NAMES B's connection. Held to its connection alone it
        // would apply; it is A's row, so B's sync does not read it.
        await using var db = await TwoOrganizationsAsync(
            StatusRule(OrgA, MappingScope.ConnectionOverride, ConnB, "Open", "CLOSED"));

        (await SyncedStatusAsync(db, ConnB, "Open")).Should().Be("Open");
    }

    [Fact]
    public async Task An_organizations_own_rules_still_apply_to_its_own_connection()
    {
        await using var db = await TwoOrganizationsAsync(
            StatusRule(OrgB, MappingScope.ConnectionOverride, ConnB, "Open", "NEW"),
            StatusRule(OrgA, MappingScope.ProviderDefault, null, "Open", "CLOSED"));

        (await SyncedStatusAsync(db, ConnB, "Open")).Should().Be("NEW");
    }

    [Fact]
    public async Task A_rule_for_one_PSA_account_does_not_map_a_second_account_of_the_same_organization()
    {
        // Customer A's Autotask and customer B's Autotask have their own picklists: "Open" on one
        // says nothing about "Open" on the other.
        await using var db = await TwoOrganizationsAsync(
            StatusRule(OrgA, MappingScope.ConnectionOverride, ConnA, "Open", "IN_PROGRESS"));

        (await SyncedStatusAsync(db, ConnA, "Open")).Should().Be("IN_PROGRESS");
        (await SyncedStatusAsync(db, ConnA2, "Open")).Should().Be("Open");
    }

    [Fact]
    public async Task Rules_are_loaded_for_the_organization_and_the_connection_asked_for()
    {
        await using var db = await TwoOrganizationsAsync(
            StatusRule(OrgA, MappingScope.ConnectionOverride, ConnA, "a-own", "NEW"),
            StatusRule(OrgA, MappingScope.ProviderDefault, null, "a-wide", "NEW"),
            StatusRule(OrgA, MappingScope.ConnectionOverride, ConnA2, "a-other-account", "NEW"),
            StatusRule(OrgB, MappingScope.ProviderDefault, null, "b-wide", "NEW"),
            StatusRule(OrgB, MappingScope.ConnectionOverride, ConnA, "b-forged", "NEW"));

        var rules = await ConnectionMappingRules.LoadAsync(db, OrgA, ProviderType.AutotaskPsa, ConnA);

        rules.Select(r => r.ExternalValue).Should().BeEquivalentTo(["a-own", "a-wide"]);
    }

    [Theory]
    [InlineData(MappingScope.QueueOrBoardOverride)]
    [InlineData(MappingScope.TicketTypeOverride)]
    [InlineData(MappingScope.CustomField)]
    [InlineData(MappingScope.Conditional)]
    [InlineData(MappingScope.ProviderDefault)]
    public void A_rule_that_names_a_connection_applies_to_that_connection_at_every_scope(MappingScope scope)
    {
        // Only the connection scope used to check. A queue rule for one account matched the same
        // queue name on another.
        var rule = StatusRule(OrgA, scope, ConnA, "Open", "IN_PROGRESS", queueKey: "Support");
        rule.TicketTypeKey = "Incident";
        var engine = new MappingEngine();
        MappingContext On(Guid connection) => new()
        {
            Provider = ProviderType.AutotaskPsa, PsaConnectionId = connection,
            QueueOrBoardKey = "Support", TicketTypeKey = "Incident",
        };

        engine.MapToPortal([rule], On(ConnA), "status", "Open").Value.Should().Be("IN_PROGRESS");
        engine.MapToPortal([rule], On(ConnA2), "status", "Open").Resolved.Should().BeFalse();
    }

    private static (MappingAdminService Service, AdminHarness Harness) AdminFor(Guid org, string dbName)
    {
        var h = AdminHarness.Create(org, dbName);
        return (new MappingAdminService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.User), h);
    }

    private static UpsertMappingInput Input(MappingScope scope, Guid? connection, ProviderType provider = ProviderType.AutotaskPsa)
        => new(null, provider, scope, connection, "status", "CLOSED", "status", "Open", MappingDirection.Bidirectional, false, null);

    [Fact]
    public async Task A_rule_cannot_be_saved_against_another_organizations_connection()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seed = AdminHarness.Platform(dbName))
        {
            seed.PsaConnections.AddRange(Connection(ConnA, OrgA, "A"), Connection(ConnB, OrgB, "B"));
            await seed.SaveChangesAsync();
        }
        var (service, h) = AdminFor(OrgA, dbName);
        await using var _ = h.Db;
        // Both connections are in the one store; this caller can see only its own.
        (await h.Db.PsaConnections.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await h.Db.PsaConnections.Select(c => c.Id).ToListAsync()).Should().Equal(ConnA);

        var act = () => service.UpsertAsync(Input(MappingScope.ConnectionOverride, ConnB), "forged");

        // The same answer as for an id that does not exist: it does not confirm the connection is real.
        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*does not exist*");
        (await h.Db.FieldMappings.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_rule_cannot_name_a_connection_of_a_different_PSA()
    {
        var dbName = Guid.NewGuid().ToString();
        var (service, h) = AdminFor(OrgA, dbName);
        await using var _ = h.Db;
        h.Db.PsaConnections.Add(Connection(ConnA, OrgA, "A"));
        await h.Db.SaveChangesAsync();

        var act = () => service.UpsertAsync(Input(MappingScope.ConnectionOverride, ConnA, ProviderType.ConnectWisePsa), "wrong PSA");

        await act.Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task A_connection_rule_has_to_name_its_connection()
    {
        var (service, h) = AdminFor(OrgA, Guid.NewGuid().ToString());
        await using var _ = h.Db;

        var act = () => service.UpsertAsync(Input(MappingScope.ConnectionOverride, null), "no connection");

        await act.Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task A_rule_for_the_callers_own_connection_is_saved()
    {
        var (service, h) = AdminFor(OrgA, Guid.NewGuid().ToString());
        await using var _ = h.Db;
        h.Db.PsaConnections.Add(Connection(ConnA, OrgA, "A"));
        await h.Db.SaveChangesAsync();

        var saved = await service.UpsertAsync(Input(MappingScope.ConnectionOverride, ConnA), "ok");

        saved.PsaConnectionId.Should().Be(ConnA);
        (await h.Db.FieldMappings.SingleAsync()).MspOrganizationId.Should().Be(OrgA);
    }
}
