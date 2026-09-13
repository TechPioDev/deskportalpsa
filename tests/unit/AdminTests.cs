using Desk.Infrastructure.Attachments;
using Desk.Application.Attachments;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Connectors.Mock;
using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Infrastructure.Admin;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

public class AdminTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed class FakeResolver(IServiceManagementConnector c) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    /// <summary>Simulates what the real ConnectorResolver throws when a connection's credentials
    /// cannot be resolved at all — e.g. a secret-store outage lost them.</summary>
    private sealed class ThrowingResolver(Exception ex) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => throw ex;
    }

    private static (ConnectionAdminService svc, AdminHarness h) Connections(IServiceManagementConnector? connector = null)
    {
        var h = AdminHarness.Create(Org);
        var resolver = new FakeResolver(connector ?? new MockConnector(new MockConnectorOptions(), h.Clock));
        return (ConnectionsSvc(h, resolver), h);
    }

    private static (ConnectionAdminService svc, AdminHarness h) ConnectionsWithResolver(IConnectorResolver resolver)
    {
        var h = AdminHarness.Create(Org);
        return (ConnectionsSvc(h, resolver), h);
    }

    private static ConnectionAdminService ConnectionsSvc(AdminHarness h, IConnectorResolver resolver)
    {
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        return new ConnectionAdminService(h.Db, h.Secrets, audit, resolver, new ConnectionFieldCache(),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), h.Clock);
    }

    private static async Task<Guid> CreateConnAsync(ConnectionAdminService svc)
        => (await svc.CreateAsync(new CreateConnectionInput(
            "CW", ProviderType.ConnectWisePsa, "https://x", null,
            new Dictionary<string, string> { ["CompanyId"] = "c", ["PrivateKey"] = "p" }, null))).Id;

    private static (MappingAdminService svc, AdminHarness h) Mappings()
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        return (new MappingAdminService(h.Db, audit, h.User), h);
    }

    [Fact]
    public async Task Creating_a_connection_stores_the_secret_in_vault_never_on_the_row()
    {
        var (svc, h) = Connections();
        var creds = new Dictionary<string, string> { ["CompanyId"] = "acme", ["PrivateKey"] = "topsecret" };

        var summary = await svc.CreateAsync(new CreateConnectionInput(
            "Prod CW", ProviderType.ConnectWisePsa, "https://cw.local", "tenant-1", creds, "UTC"));

        var row = await h.Db.PsaConnections.SingleAsync();
        row.CredentialSecretRef.Should().StartWith("mem://");   // opaque reference, not the secret
        row.CredentialSecretRef.Should().NotContain("topsecret");

        // The real secret is retrievable only via the store.
        (await h.Secrets.ReadAsync(row.CredentialSecretRef))["PrivateKey"].Should().Be("topsecret");

        // The summary DTO has no property that could carry a secret (compile-time guarantee).
        summary.Should().BeOfType<ConnectionSummary>();
    }

    [Fact]
    public async Task Connection_creation_is_audited_without_credentials()
    {
        var (svc, h) = Connections();
        await svc.CreateAsync(new CreateConnectionInput(
            "Prod CW", ProviderType.ConnectWisePsa, "https://cw.local", null,
            new Dictionary<string, string> { ["PrivateKey"] = "topsecret" }, null));

        var entry = await h.Db.AuditLog.SingleAsync(a => a.Action == "connection.created");
        entry.DetailJson.Should().NotBeNull();
        entry.DetailJson!.Should().NotContain("topsecret"); // secret never enters the audit trail
        entry.ActorDisplayName.Should().Be("Admin User");
    }

    [Fact]
    public async Task Test_connection_marks_healthy_on_success_and_audits()
    {
        var (svc, h) = Connections(); // default mock connector succeeds
        var id = await CreateConnAsync(svc);

        var result = await svc.TestAsync(id);

        result.Success.Should().BeTrue();
        (await h.Db.PsaConnections.FirstAsync(c => c.Id == id)).Status.Should().Be(ConnectionStatus.Healthy);
        (await h.Db.AuditLog.CountAsync(a => a.Action == "connection.tested")).Should().Be(1);
    }

    [Fact]
    public async Task Test_connection_marks_failed_on_auth_error()
    {
        var h0 = AdminHarness.Create(Org);
        var failing = new MockConnector(new MockConnectorOptions { FailEveryCallWith = ConnectorFailureKind.Authentication }, h0.Clock);
        var (svc, h) = Connections(failing);
        var id = await CreateConnAsync(svc);

        var result = await svc.TestAsync(id);

        result.Success.Should().BeFalse();
        var row = await h.Db.PsaConnections.FirstAsync(c => c.Id == id);
        row.Status.Should().Be(ConnectionStatus.Failed);
        row.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task Test_connection_marks_failed_when_the_connector_cannot_even_be_resolved()
    {
        // Simulates a connection whose credentials the secret store lost (a resolver that throws
        // before a connector is ever built) — this must still record Failed + a reason on the row,
        // not silently leave Status/LastError untouched, and must not surface as an unhandled 500.
        var thrown = new ValidationFailedException("'CW' has no valid stored credentials — edit the connection and re-enter them.");
        var (svc, h) = ConnectionsWithResolver(new ThrowingResolver(thrown));
        var id = await CreateConnAsync(svc);

        var result = await svc.TestAsync(id);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("re-enter them");
        var row = await h.Db.PsaConnections.FirstAsync(c => c.Id == id);
        row.Status.Should().Be(ConnectionStatus.Failed);
        row.LastError.Should().Contain("re-enter them");
    }

    [Fact]
    public async Task Update_changes_settings_and_audits()
    {
        var (svc, h) = Connections();
        var id = await CreateConnAsync(svc);

        await svc.UpdateAsync(id, new UpdateConnectionInput("Renamed", "https://new", "t2", "UTC", true, null));

        var row = await h.Db.PsaConnections.FirstAsync(c => c.Id == id);
        row.Name.Should().Be("Renamed");
        row.ApiEndpoint.Should().Be("https://new");
        (await h.Db.AuditLog.CountAsync(a => a.Action == "connection.updated")).Should().Be(1);
    }

    [Fact]
    public async Task Get_fields_discovers_boards_statuses_and_priorities()
    {
        var (svc, h) = Connections();
        var id = await CreateConnAsync(svc);

        var fields = await svc.GetFieldsAsync(id);

        fields.QueuesOrBoards.Should().NotBeEmpty();
        fields.Statuses.Should().NotBeEmpty();
        fields.Priorities.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Mapping_upsert_creates_a_version_snapshot_and_audit_entry()
    {
        var (svc, h) = Mappings();
        await svc.UpsertAsync(new UpsertMappingInput(
            null, ProviderType.AutotaskPsa, MappingScope.ProviderDefault, null,
            "status", "IN_PROGRESS", "status", "In Progress", MappingDirection.Bidirectional, false, null), "initial");

        (await h.Db.FieldMappings.CountAsync()).Should().Be(1);
        (await h.Db.FieldMappingVersions.CountAsync()).Should().Be(1);
        (await h.Db.AuditLog.CountAsync(a => a.Action == "mapping.upserted")).Should().Be(1);
    }

    [Fact]
    public async Task Mapping_upsert_without_id_updates_the_matching_rule_instead_of_duplicating()
    {
        var (svc, h) = Mappings();
        UpsertMappingInput Input(string external) => new(
            null, ProviderType.AutotaskPsa, MappingScope.ProviderDefault, null,
            "status", "IN_PROGRESS", "status", external, MappingDirection.Bidirectional, false, null);

        await svc.UpsertAsync(Input("In Progress"), "first");
        await svc.UpsertAsync(Input("Working"), "repeat");

        // Same provider/scope/field/portal-value/direction → one rule, updated in place.
        (await h.Db.FieldMappings.CountAsync()).Should().Be(1);
        (await h.Db.FieldMappings.Select(m => m.ExternalValue).SingleAsync()).Should().Be("Working");
    }

    [Fact]
    public async Task Mapping_rollback_restores_a_previous_version()
    {
        var (svc, h) = Mappings();
        var rule = await svc.UpsertAsync(new UpsertMappingInput(
            null, ProviderType.AutotaskPsa, MappingScope.ProviderDefault, null,
            "status", "IN_PROGRESS", "status", "In Progress", MappingDirection.Bidirectional, false, null), "v1");

        // v1 snapshot now holds External = "In Progress". Change it.
        await svc.UpsertAsync(new UpsertMappingInput(
            rule.Id, ProviderType.AutotaskPsa, MappingScope.ProviderDefault, null,
            "status", "IN_PROGRESS", "status", "Working", MappingDirection.Bidirectional, false, null), "v2");
        (await h.Db.FieldMappings.SingleAsync()).ExternalValue.Should().Be("Working");

        var v1 = (await svc.VersionsAsync(ProviderType.AutotaskPsa, null)).Single(v => v.Version == 1);
        await svc.RollbackAsync(v1.Id);

        (await h.Db.FieldMappings.SingleAsync()).ExternalValue.Should().Be("In Progress"); // restored
        (await h.Db.AuditLog.CountAsync(a => a.Action == "mapping.rolledback")).Should().Be(1);
    }

    private static UpsertMappingInput StatusRule(string portal, string external) => new(
        null, ProviderType.AutotaskPsa, MappingScope.ProviderDefault, null,
        "status", portal, "status", external, MappingDirection.Bidirectional, false, null);

    [Fact]
    public async Task A_rule_changed_outside_the_admin_api_shows_the_snapshot_as_out_of_date()
    {
        var (svc, h) = Mappings();
        await svc.UpsertAsync(StatusRule("IN_PROGRESS", "In Progress"), "v1");
        (await svc.SnapshotStatusAsync(ProviderType.AutotaskPsa, null)).MatchesLiveRules.Should().BeTrue();

        // What happened on 3 Sep: rules written straight to the database, never versioned.
        h.Db.FieldMappings.Add(new Desk.Domain.Mapping.FieldMapping
        {
            Provider = ProviderType.AutotaskPsa, PortalField = "status", PortalValue = "ON_HOLD", ExternalField = "status", ExternalValue = "Waiting",
        });
        await h.Db.SaveChangesAsync();

        var status = await svc.SnapshotStatusAsync(ProviderType.AutotaskPsa, null);
        status.MatchesLiveRules.Should().BeFalse();
        (status.LiveRules, status.SnapshotRules).Should().Be((2, 1));
    }

    [Fact]
    public async Task Saving_a_snapshot_catches_up_and_is_audited()
    {
        var (svc, h) = Mappings();
        await svc.UpsertAsync(StatusRule("IN_PROGRESS", "In Progress"), "v1");
        var rule = await h.Db.FieldMappings.SingleAsync();
        rule.ExternalValue = "Working"; // edited in place, outside the API
        await h.Db.SaveChangesAsync();

        var after = await svc.SaveSnapshotAsync(ProviderType.AutotaskPsa, null, null);

        after.Should().BeEquivalentTo(new { LatestVersion = 2, MatchesLiveRules = true, LiveRules = 1, SnapshotRules = 1 });
        (await h.Db.AuditLog.CountAsync(a => a.Action == "mapping.snapshot")).Should().Be(1);

        // And the point of it: rolling back to the new version keeps the edit.
        var v2 = (await svc.VersionsAsync(ProviderType.AutotaskPsa, null)).Single(v => v.Version == 2);
        await svc.RollbackAsync(v2.Id);
        (await h.Db.FieldMappings.SingleAsync()).ExternalValue.Should().Be("Working");
    }

    [Fact]
    public async Task Saving_when_the_snapshot_already_matches_adds_no_version()
    {
        var (svc, h) = Mappings();
        await svc.UpsertAsync(StatusRule("IN_PROGRESS", "In Progress"), "v1");

        var result = await svc.SaveSnapshotAsync(ProviderType.AutotaskPsa, null, null);

        result.LatestVersion.Should().Be(1);
        (await h.Db.FieldMappingVersions.CountAsync()).Should().Be(1);
        (await h.Db.AuditLog.CountAsync(a => a.Action == "mapping.snapshot")).Should().Be(0);
    }

    [Fact]
    public async Task A_snapshot_matches_whatever_order_its_rules_were_written_in()
    {
        // Production's 13 Sep catch-up snapshot was written by SQL: other key spacing, other order.
        var (svc, h) = Mappings();
        await svc.UpsertAsync(StatusRule("IN_PROGRESS", "In Progress"), "v1");
        await svc.UpsertAsync(StatusRule("ON_HOLD", "Waiting"), "v2");
        var version = await h.Db.FieldMappingVersions.OrderByDescending(v => v.Version).FirstAsync();
        version.SnapshotJson =
            "[{\"Scope\" : 1, \"PsaConnectionId\" : null, \"ClientCompanyId\" : null, \"QueueOrBoardKey\" : null, \"TicketTypeKey\" : null, " +
            "\"PortalField\" : \"status\", \"PortalValue\" : \"ON_HOLD\", \"ExternalField\" : \"status\", \"ExternalValue\" : \"Waiting\", " +
            "\"Direction\" : 3, \"IsRequired\" : false, \"FallbackValue\" : null, \"IsActive\" : true}, " +
            "{\"Scope\" : 1, \"PsaConnectionId\" : null, \"ClientCompanyId\" : null, \"QueueOrBoardKey\" : null, \"TicketTypeKey\" : null, " +
            "\"PortalField\" : \"status\", \"PortalValue\" : \"IN_PROGRESS\", \"ExternalField\" : \"status\", \"ExternalValue\" : \"In Progress\", " +
            "\"Direction\" : 3, \"IsRequired\" : false, \"FallbackValue\" : null, \"IsActive\" : true}]";
        await h.Db.SaveChangesAsync();

        (await svc.SnapshotStatusAsync(ProviderType.AutotaskPsa, null)).MatchesLiveRules.Should().BeTrue();
    }

    [Fact]
    public async Task Dead_lettered_job_can_be_reprocessed()
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        var svc = new JobMonitorService(h.Db, audit, h.Clock);

        var job = new BackgroundJob
        {
            MspOrganizationId = Org, JobType = "sync.inbound-event", PayloadJson = "{}",
            Status = BackgroundJobStatus.DeadLettered, Attempts = 5, LastError = "boom",
        };
        h.Db.BackgroundJobs.Add(job);
        await h.Db.SaveChangesAsync();

        await svc.ReprocessAsync(job.Id);

        job.Status.Should().Be(BackgroundJobStatus.Queued);
        job.Attempts.Should().Be(0);
        job.LastError.Should().BeNull();
        (await h.Db.AuditLog.CountAsync(a => a.Action == "job.reprocessed")).Should().Be(1);
    }

    [Fact]
    public async Task Reprocessing_a_non_dead_lettered_job_is_rejected()
    {
        var h = AdminHarness.Create(Org);
        var svc = new JobMonitorService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Clock);
        var job = new BackgroundJob { MspOrganizationId = Org, JobType = "x", PayloadJson = "{}", Status = BackgroundJobStatus.Queued };
        h.Db.BackgroundJobs.Add(job);
        await h.Db.SaveChangesAsync();

        var act = async () => await svc.ReprocessAsync(job.Id);
        await act.Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task Audit_query_returns_entries_for_the_current_tenant()
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        await audit.WriteAsync("test.action", "Thing", "1");

        var svc = new AuditQueryService(h.Db, h.Tenant);
        var entries = await svc.ListAsync();
        entries.Should().ContainSingle().Which.Action.Should().Be("test.action");
    }

    [Fact]
    public async Task Audit_query_filtered_by_entity_id_returns_only_that_entitys_entries()
    {
        // The Users page's Activity tab needs a per-user slice of a log that otherwise mixes every
        // entity type together — proven non-vacuously by seeding two different entities' entries and
        // checking the filter excludes the one that doesn't match, not just includes the one that does.
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        await audit.WriteAsync("user.updated", "AppUser", "user-1");
        await audit.WriteAsync("user.updated", "AppUser", "user-2");

        var svc = new AuditQueryService(h.Db, h.Tenant);
        var entries = await svc.ListAsync(entityId: "user-1");

        entries.Should().ContainSingle().Which.EntityId.Should().Be("user-1");
    }

    [Fact]
    public async Task Re_entering_credentials_heals_a_connection_whose_secret_reference_is_orphaned()
    {
        // End-to-end reproduction of the live failure on piomanage.com: both PSA connections point
        // at Vault-era references whose secrets were discarded when Vault's dev-mode backend
        // restarted, so the connection is stuck Failed and the remedy its own error message
        // prescribes — edit and re-enter the credentials — has to work.
        //
        // Uses the real EncryptedDbSecretStore rather than the harness's in-memory one: the
        // reference-minting only happens in the database-backed store, so the in-memory store would
        // pass this test without exercising anything it covers.
        var h = AdminHarness.Create(Org);
        await using var _ = h.Db;
        var cipher = new Desk.Infrastructure.Secrets.SecretCipher(
            new Desk.Infrastructure.Secrets.SecretEncryptionOptions { Key = "w+WEoJiLQLVmZzgEm//uVd0YpeTwnhwm2rUyftBqdO8=" });
        var store = new Desk.Infrastructure.Secrets.EncryptedDbSecretStore(h.Db, cipher, h.Clock);
        var svc = new ConnectionAdminService(h.Db, store, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock),
            new FakeResolver(new MockConnector(new MockConnectorOptions(), h.Clock)), new ConnectionFieldCache(),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), h.Clock);

        var created = await svc.CreateAsync(new CreateConnectionInput(
            "Autotask", ProviderType.AutotaskPsa, "https://webservices31.autotask.net/ATServicesRest/v1.0/", null,
            new Dictionary<string, string> { ["Secret"] = "original" }, null));

        // Reproduce the production row exactly: a 74-character Vault path with nothing behind it,
        // and the failure the sync job recorded against it.
        var row = await h.Db.PsaConnections.SingleAsync(c => c.Id == created.Id);
        await store.DeleteAsync(row.CredentialSecretRef);
        row.CredentialSecretRef = "desk/psa-credentials/AutotaskPsa/Autotask/2a47afb201f94a759f7645f6b74a845b";
        row.Status = ConnectionStatus.Failed;
        row.LastError = "'Autotask' has no valid stored credentials — edit the connection and re-enter them.";
        await h.Db.SaveChangesAsync();

        await svc.UpdateAsync(created.Id, new UpdateConnectionInput(
            "Autotask", "https://webservices31.autotask.net/ATServicesRest/v1.0/", null, null, true,
            new Dictionary<string, string> { ["Secret"] = "re-entered" }, null));

        var healed = await h.Db.PsaConnections.SingleAsync(c => c.Id == created.Id);
        (await store.ReadAsync(healed.CredentialSecretRef))["Secret"].Should().Be("re-entered");
        healed.LastError.Should().BeNull("the recorded failure was about the credentials just replaced");
        healed.Status.Should().Be(ConnectionStatus.Pending, "only a real successful call may claim Healthy");
    }

    [Fact]
    public async Task Connection_list_reports_which_credential_fields_are_stored_by_name_only()
    {
        var (svc, h) = Connections();
        await using var _ = h.Db;
        await CreateConnAsync(svc); // stores CompanyId + PrivateKey

        var summary = (await svc.ListAsync()).Single();

        // Names only — the edit form needs "is something stored behind this field", never the value.
        summary.StoredCredentialKeys.Should().BeEquivalentTo(["CompanyId", "PrivateKey"]);
    }

    [Fact]
    public async Task A_connection_whose_secret_is_lost_reports_nothing_stored()
    {
        // The Vault-era loss as the list sees it: the ref exists on the row, the secret behind it
        // does not. [] is what lets the edit form stop bluffing 'unchanged' at the admin.
        var (svc, h) = Connections();
        await using var _ = h.Db;
        var id = await CreateConnAsync(svc);
        var row = await h.Db.PsaConnections.SingleAsync(c => c.Id == id);
        await h.Secrets.DeleteAsync(row.CredentialSecretRef);

        (await svc.ListAsync()).Single().StoredCredentialKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Re_entering_one_credential_keeps_the_other_stored_fields()
    {
        // The edit form promises "leave blank to keep the existing keys". Rotation used to replace
        // the whole secret with only the typed fields, making that promise false: re-entering just
        // the PrivateKey silently dropped CompanyId, and the next sync failed on a field the admin
        // never touched.
        var (svc, h) = Connections();
        await using var _ = h.Db;
        var id = await CreateConnAsync(svc); // CompanyId=c, PrivateKey=p

        await svc.UpdateAsync(id, new UpdateConnectionInput("CW", "https://x", null, null, true,
            new Dictionary<string, string> { ["PrivateKey"] = "rotated" }, null));

        var row = await h.Db.PsaConnections.SingleAsync(c => c.Id == id);
        var secret = await h.Secrets.ReadAsync(row.CredentialSecretRef);
        secret["PrivateKey"].Should().Be("rotated");
        secret["CompanyId"].Should().Be("c", "a blank field means keep, and keep must be true per-field");
    }
}
