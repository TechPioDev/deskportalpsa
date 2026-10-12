using Desk.Application.Admin;
using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Connectors.Mock;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Connectors;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A connection's life: set up, tried, switched on, changed, paused, put away.
///
/// It used to be enabled the instant it was saved, before its credentials had been tried once;
/// new credentials replaced working ones before anything checked them; the same PSA account could
/// be connected twice; and there was no way to stop reading from a PSA, or to put a connection
/// away, short of leaving it enabled.
/// </summary>
public class ConnectionLifecycleTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private const string Zone = "https://webservices31.autotask.net/ATServicesRest/";

    private static Dictionary<string, string> Keys(string user = "api@techpio.test", string secret = "s3cret")
        => new() { ["ApiIntegrationCode"] = "code", ["UserName"] = user, ["Secret"] = secret };

    /// <summary>A resolver whose PSA accepts some credentials and rejects others, and remembers what it was asked to try.</summary>
    private sealed class Psa(TestClock clock) : IConnectorResolver
    {
        public Func<string, IReadOnlyDictionary<string, string>, bool> Accepts { get; set; } = (_, _) => true;
        public List<(string Endpoint, IReadOnlyDictionary<string, string> Credentials)> Trials { get; } = [];

        private IServiceManagementConnector Connector(bool accepted) => new MockConnector(
            new MockConnectorOptions { FailEveryCallWith = accepted ? null : ConnectorFailureKind.Authentication }, clock);

        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Connector(true));

        public Task<IServiceManagementConnector> ResolveForTrialAsync(
            PsaConnection connection, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
        {
            Trials.Add((connection.ApiEndpoint, new Dictionary<string, string>(credentials)));
            return Task.FromResult(Connector(Accepts(connection.ApiEndpoint, credentials)));
        }

        public bool Supports(ProviderType provider) => provider is ProviderType.AutotaskPsa or ProviderType.ConnectWisePsa;

        // The real factories' own answers: nothing here needs a database or a network.
        private static readonly AutotaskConnectorFactory Autotask = new(null!, null!, null!, TimeProvider.System, null!);
        private static readonly ConnectWiseConnectorFactory ConnectWise = new(null!, null!, null!, TimeProvider.System, null!);

        public IReadOnlyList<ProviderDescriptor> Providers => [Autotask.Descriptor, ConnectWise.Descriptor];

        public string? AccountKey(ProviderType provider, string apiEndpoint, IReadOnlyDictionary<string, string> credentials)
            => provider == ProviderType.AutotaskPsa ? Autotask.AccountKey(apiEndpoint, credentials) : ConnectWise.AccountKey(apiEndpoint, credentials);
    }

    private sealed record World(AdminHarness H, ConnectionAdminService Service, Psa Psa);

    private static World Build()
    {
        var h = AdminHarness.Create(Org);
        var psa = new Psa(h.Clock);
        var service = new ConnectionAdminService(h.Db, h.Secrets, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), psa,
            new ConnectionFieldCache(), new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), h.Clock);
        return new World(h, service, psa);
    }

    private static Task<ConnectionSummary> AddAsync(World w, string name = "Autotask", string user = "api@techpio.test", string endpoint = Zone)
        => w.Service.CreateAsync(new CreateConnectionInput(name, ProviderType.AutotaskPsa, endpoint, null, Keys(user), null));

    /// <summary>Created, tested and switched on: a connection as it is once someone has finished setting it up.</summary>
    private static async Task<Guid> LiveAsync(World w, string name = "Autotask", string user = "api@techpio.test", string endpoint = Zone)
    {
        var created = await AddAsync(w, name, user, endpoint);
        (await w.Service.TestAsync(created.Id)).Success.Should().BeTrue();
        await w.Service.ActivateAsync(created.Id);
        w.Psa.Trials.Clear();
        return created.Id;
    }

    private static Task<PsaConnection> RowAsync(World w, Guid id) => w.H.Db.PsaConnections.AsNoTracking().SingleAsync(c => c.Id == id);

    // ---- set up before live ----------------------------------------------------------------

    [Fact]
    public async Task A_new_connection_is_in_setup_and_is_not_live()
    {
        var w = Build();
        await using var _ = w.H.Db;

        var created = await AddAsync(w);

        (created.State, created.IsEnabled).Should().Be((ConnectionState.Setup, false));
        var row = await RowAsync(w, created.Id);
        (row.InSetup, row.IsEnabled).Should().Be((true, false));
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(0, "the scheduled sync does not call a PSA with credentials nobody has tried");
    }

    [Fact]
    public async Task It_cannot_be_switched_on_until_a_test_has_passed()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var created = await AddAsync(w);

        (await ((Func<Task>)(() => w.Service.ActivateAsync(created.Id))).Should().ThrowAsync<ValidationFailedException>()).WithMessage("*Test the connection first*");
        // And not by the side door either.
        await ((Func<Task>)(() => w.Service.SetEnabledAsync(created.Id, true))).Should().ThrowAsync<ValidationFailedException>();
        (await RowAsync(w, created.Id)).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Once_the_PSA_has_accepted_its_credentials_it_can_be_switched_on()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var created = await AddAsync(w);

        (await w.Service.TestAsync(created.Id)).Success.Should().BeTrue("a connection that is not on yet can still be tested: that is what lets it be switched on");
        var live = await w.Service.ActivateAsync(created.Id);

        (live.State, live.IsEnabled).Should().Be((ConnectionState.Connected, true), "accepted by the PSA, nothing synced yet");
        (await RowAsync(w, created.Id)).InSetup.Should().BeFalse();
        (await w.H.Db.AuditLog.CountAsync(a => a.Action == "connection.activated")).Should().Be(1);
        (await SyncSchedule.Due(w.H.Db.PsaConnections).Select(c => c.Id).ToListAsync()).Should().Equal(created.Id);
    }

    [Fact]
    public async Task Credentials_the_PSA_rejects_leave_it_in_setup()
    {
        var w = Build();
        await using var _ = w.H.Db;
        w.Psa.Accepts = (_, _) => false;
        var created = await AddAsync(w);

        var test = await w.Service.TestAsync(created.Id);

        test.Success.Should().BeFalse();
        await ((Func<Task>)(() => w.Service.ActivateAsync(created.Id))).Should().ThrowAsync<ValidationFailedException>();
        (await w.Service.ListAsync()).Single().State.Should().Be(ConnectionState.Setup);
    }

    // ---- an address or credentials are tried before they are kept -----------------------------

    [Fact]
    public async Task New_credentials_the_PSA_rejects_are_not_saved_and_the_working_ones_stay()
    {
        // They were saved first. A mistyped key replaced the working one, and the connection was
        // down until someone noticed.
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);
        var before = await RowAsync(w, id);
        w.Psa.Accepts = (_, c) => c["Secret"] == "s3cret";

        var act = () => w.Service.UpdateAsync(id, new UpdateConnectionInput("Renamed", Zone, null, null, true,
            new Dictionary<string, string> { ["Secret"] = "mistyped" }));

        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*Not saved*new credentials*unchanged*");
        var after = await RowAsync(w, id);
        (after.Name, after.Status, after.CredentialSecretRef).Should().Be((before.Name, before.Status, before.CredentialSecretRef));
        (await w.H.Secrets.ReadAsync(after.CredentialSecretRef))["Secret"].Should().Be("s3cret");
    }

    [Fact]
    public async Task New_credentials_are_tried_as_they_will_be_stored_and_kept_when_accepted()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);

        var saved = await w.Service.UpdateAsync(id, new UpdateConnectionInput("Autotask", Zone, null, null, true,
            new Dictionary<string, string> { ["Secret"] = "rotated" }));

        var (endpoint, tried) = w.Psa.Trials.Single();
        endpoint.Should().Be(Zone);
        tried.Should().BeEquivalentTo(new Dictionary<string, string> { ["ApiIntegrationCode"] = "code", ["UserName"] = "api@techpio.test", ["Secret"] = "rotated" },
            "a blank field means keep what is stored, so what is tried is the stored set with the typed field over it");
        saved.Status.Should().Be(ConnectionStatus.Healthy, "the PSA has just accepted exactly what was saved");
        (await w.H.Secrets.ReadAsync((await RowAsync(w, id)).CredentialSecretRef))["Secret"].Should().Be("rotated");
    }

    [Fact]
    public async Task A_new_address_is_tried_with_the_stored_credentials_before_they_are_sent_there_for_good()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);
        const string elsewhere = "https://webservices5.autotask.net/ATServicesRest/";
        w.Psa.Accepts = (endpoint, _) => endpoint == Zone;

        var act = () => w.Service.UpdateAsync(id, new UpdateConnectionInput("Autotask", elsewhere, null, null, true, null));

        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*new address*");
        w.Psa.Trials.Single().Endpoint.Should().Be(elsewhere);
        (await RowAsync(w, id)).ApiEndpoint.Should().Be(Zone);
    }

    [Fact]
    public async Task Changing_only_the_name_asks_nothing_of_the_PSA()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);

        await w.Service.UpdateAsync(id, new UpdateConnectionInput("Autotask (EU)", Zone, null, null, true, null));

        w.Psa.Trials.Should().BeEmpty();
        (await RowAsync(w, id)).Name.Should().Be("Autotask (EU)");
    }

    // ---- one connection per PSA account -------------------------------------------------------

    [Fact]
    public async Task The_same_PSA_account_cannot_be_connected_twice()
    {
        // Two connections to one account import every ticket twice, under two sets of ids.
        var w = Build();
        await using var _ = w.H.Db;
        await LiveAsync(w, "Autotask");

        var again = () => AddAsync(w, "Autotask again", endpoint: "https://WEBSERVICES31.autotask.net/ATServicesRest/v1.0/");

        (await again.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*\"Autotask\" is already connected*");
        (await w.H.Db.PsaConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Another_account_of_the_same_PSA_is_another_connection()
    {
        var w = Build();
        await using var _ = w.H.Db;
        await LiveAsync(w, "Customer A");

        await AddAsync(w, "Customer B", user: "api@customer-b.test");                                    // another API user, same zone
        await AddAsync(w, "Customer C", endpoint: "https://webservices5.autotask.net/ATServicesRest/");  // same user name, another zone

        (await w.H.Db.PsaConnections.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_connection_made_before_accounts_were_recorded_is_still_recognised()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);
        var legacy = await w.H.Db.PsaConnections.SingleAsync(c => c.Id == id);
        legacy.AccountKeyHash = null;
        await w.H.Db.SaveChangesAsync();

        await ((Func<Task>)(() => AddAsync(w, "Second"))).Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task An_edit_cannot_turn_one_connection_into_a_second_one_to_another_account()
    {
        var w = Build();
        await using var _ = w.H.Db;
        await LiveAsync(w, "Customer A", user: "a@techpio.test");
        var b = await LiveAsync(w, "Customer B", user: "b@techpio.test");

        var act = () => w.Service.UpdateAsync(b, new UpdateConnectionInput("Customer B", Zone, null, null, true,
            new Dictionary<string, string> { ["UserName"] = "a@techpio.test" }));

        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*already connected*");
        (await w.H.Secrets.ReadAsync((await RowAsync(w, b)).CredentialSecretRef))["UserName"].Should().Be("b@techpio.test");
    }

    // ---- one name per connection ---------------------------------------------------------------

    [Fact]
    public async Task Two_connections_cannot_share_a_name_because_views_and_filters_go_by_it()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var first = await LiveAsync(w, "Main desk");

        // Another PSA account, so only the name stands in the way.
        var same = () => AddAsync(w, " main DESK ", user: "api@customer-b.test");
        (await same.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*already called \"main DESK\"*");

        var second = await AddAsync(w, "Second desk", user: "api@customer-b.test");
        var rename = () => w.Service.UpdateAsync(second.Id, new UpdateConnectionInput("Main desk", Zone, null, null, false, null));
        await rename.Should().ThrowAsync<ValidationFailedException>();
        (await w.Service.UpdateAsync(second.Id, new UpdateConnectionInput("Second desk", Zone, null, null, false, null))).Name
            .Should().Be("Second desk", "keeping its own name is not taking one");

        // Put away, a connection no longer holds its name; coming back, it needs it to be free.
        await w.Service.ArchiveAsync(first);
        (await w.Service.UpdateAsync(second.Id, new UpdateConnectionInput("Main desk", Zone, null, null, false, null))).Name.Should().Be("Main desk");
        (await ((Func<Task>)(() => w.Service.RestoreAsync(first))).Should().ThrowAsync<ValidationFailedException>()).WithMessage("*already called*");
    }

    // ---- pause -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_paused_connection_is_not_read_from_and_says_so()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);

        var paused = await w.Service.PauseSyncAsync(id);

        paused.State.Should().Be(ConnectionState.Paused);
        paused.IsEnabled.Should().BeTrue("replies, status changes and logged time still reach the PSA");
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(0);
        var connector = new StubConnector();
        var run = await new ConnectionSyncRunner(w.H.Db, new Fixed(connector),
            new TicketSyncService(w.H.Db, new Desk.Application.Mapping.MappingEngine(), new SyncEventStore(w.H.Db, w.H.Clock), w.H.Clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), w.H.Clock), new HeuristicMalwareScanner(), w.H.Clock).RunAsync(id);
        (run.Fetched, connector.TicketRequests.Count).Should().Be((0, 0));

        (await w.Service.ResumeSyncAsync(id)).State.Should().Be(ConnectionState.Connected);
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(1);
        (await w.H.Db.AuditLog.Select(a => a.Action).Where(a => a.StartsWith("connection.sync.")).ToListAsync())
            .Should().BeEquivalentTo(["connection.sync.paused", "connection.sync.resumed"]);
    }

    private sealed class Fixed(IServiceManagementConnector c) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    [Fact]
    public async Task Resuming_reads_from_where_it_stopped_so_what_changed_while_paused_is_not_missed()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);
        var connector = new StubConnector();
        ConnectionSyncRunner Runner() => new(w.H.Db, new Fixed(connector),
            new TicketSyncService(w.H.Db, new Desk.Application.Mapping.MappingEngine(), new SyncEventStore(w.H.Db, w.H.Clock), w.H.Clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), w.H.Clock), new HeuristicMalwareScanner(), w.H.Clock);

        var lastRead = w.H.Clock.GetUtcNow();
        await Runner().RunAsync(id);
        connector.TicketRequests.Should().HaveCount(1);

        await w.Service.PauseSyncAsync(id);
        w.H.Clock.Advance(TimeSpan.FromHours(6));
        await Runner().RunAsync(id);
        connector.TicketRequests.Should().HaveCount(1, "paused: nothing is asked of the PSA");

        await w.Service.ResumeSyncAsync(id);
        await Runner().RunAsync(id);

        connector.TicketRequests.Should().HaveCount(2);
        connector.TicketRequests[^1].ModifiedSince.Should().NotBeNull().And.BeOnOrBefore(lastRead,
            "the six hours it was paused for are inside the question it asks on the way back");
    }

    // ---- switched off and on again ----------------------------------------------------------------

    [Fact]
    public async Task A_connection_switched_back_on_no_longer_reads_as_disabled()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);

        await w.Service.SetEnabledAsync(id, false);
        (await w.Service.ListAsync()).Single().State.Should().Be(ConnectionState.Disabled);
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(0);

        await w.Service.SetEnabledAsync(id, true);

        var row = await RowAsync(w, id);
        (row.IsEnabled, row.Status).Should().Be((true, ConnectionStatus.Pending), "on again, and not yet known to be well");
        (await w.Service.ListAsync()).Single().State.Should().NotBe(ConnectionState.Disabled);
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(1);
    }

    // ---- archive ---------------------------------------------------------------------------------

    [Fact]
    public async Task Archiving_puts_a_connection_away_and_removes_nothing()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);
        w.H.Db.Tickets.Add(new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = id, Provider = ProviderType.AutotaskPsa, ExternalTicketId = "7814",
            Title = "Imported", RequesterName = "r", RequesterEmail = "r@a.test",
        });
        await w.H.Db.SaveChangesAsync();
        var secretRef = (await RowAsync(w, id)).CredentialSecretRef;

        await w.Service.ArchiveAsync(id);

        (await w.Service.ListAsync()).Should().BeEmpty("it is no longer listed");
        var putAway = (await w.Service.ArchivedAsync()).Should().ContainSingle("or it could never be found again to restore").Subject;
        (putAway.Id, putAway.State).Should().Be((id, ConnectionState.Archived));
        var row = await RowAsync(w, id);
        (row.IsEnabled, row.Status, row.ArchivedAt).Should().Be((false, ConnectionStatus.Disabled, w.H.Clock.GetUtcNow()));
        (await w.H.Db.Tickets.CountAsync(t => t.PsaConnectionId == id)).Should().Be(1, "what it imported stays");
        (await w.H.Secrets.ReadAsync(secretRef)).Should().ContainKey("Secret", "restoring it must not mean setting it up again");
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(0);
        await ((Func<Task>)(() => w.Service.SetEnabledAsync(id, true))).Should().ThrowAsync<ValidationFailedException>();
        await ((Func<Task>)(() => w.Service.UpdateAsync(id, new UpdateConnectionInput("x", Zone, null, null, true, null)))).Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task An_archived_connection_comes_back_switched_off_and_only_if_its_account_is_still_free()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w, "Old");
        await w.Service.ArchiveAsync(id);

        // Archived, it no longer holds the account: the same account may be connected afresh.
        var replacement = await AddAsync(w, "New");
        (await ((Func<Task>)(() => w.Service.RestoreAsync(id))).Should().ThrowAsync<ValidationFailedException>()).WithMessage("*already connected*");

        await w.Service.ArchiveAsync(replacement.Id);
        var restored = await w.Service.RestoreAsync(id);

        (restored.State, restored.IsEnabled).Should().Be((ConnectionState.Disabled, false), "back, and still off: switching it on is a second decision");
        (await w.Service.ListAsync()).Select(c => c.Name).Should().Equal("Old");
        (await w.Service.ArchivedAsync()).Select(c => c.Name).Should().Equal("New");
    }

    // ---- locked out ------------------------------------------------------------------------------

    [Fact]
    public async Task A_live_connection_whose_credentials_are_rejected_is_left_alone_until_they_are_replaced()
    {
        // Asked again every five minutes with the same rejected key, an API account gets locked.
        var w = Build();
        await using var _ = w.H.Db;
        var id = await LiveAsync(w);
        var connector = new StubConnector();
        connector.Tickets.Add(new Desk.PsaCore.Models.UnifiedTicket { ExternalId = "1", Title = "t", Status = "New", Priority = "Medium", RequesterExternalId = "co", CompanyName = "Acme" });
        connector.NoteReadFailure = new ConnectorException(ConnectorFailureKind.Authentication, "Autotask rejected the credentials.");
        var runner = new ConnectionSyncRunner(w.H.Db, new Fixed(connector),
            new TicketSyncService(w.H.Db, new Desk.Application.Mapping.MappingEngine(), new SyncEventStore(w.H.Db, w.H.Clock), w.H.Clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), w.H.Clock), new HeuristicMalwareScanner(), w.H.Clock);

        await ((Func<Task>)(() => runner.RunAsync(id))).Should().ThrowAsync<ConnectorException>();

        w.H.Db.ChangeTracker.Clear();
        (await w.Service.ListAsync()).Single().State.Should().Be(ConnectionState.AuthRequired);
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(0);

        // Credentials the PSA accepts put it back.
        await w.Service.UpdateAsync(id, new UpdateConnectionInput("Autotask", Zone, null, null, true,
            new Dictionary<string, string> { ["Secret"] = "new" }));
        (await w.Service.ListAsync()).Single().State.Should().NotBe(ConnectionState.AuthRequired);
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(1);
    }

    // ---- one word for where it stands ------------------------------------------------------------

    [Theory]
    //          setup  on     archived paused kind              status                       synced running  expected
    [InlineData(true,  false, false,   false, null,             ConnectionStatus.Pending,    false, false,   ConnectionState.Setup)]
    [InlineData(true,  false, false,   false, "Authentication", ConnectionStatus.Failed,     false, false,   ConnectionState.Setup)]
    [InlineData(false, true,  false,   false, null,             ConnectionStatus.Healthy,    false, false,   ConnectionState.Connected)]
    [InlineData(false, true,  false,   false, null,             ConnectionStatus.Healthy,    true,  false,   ConnectionState.Healthy)]
    [InlineData(false, true,  false,   false, null,             ConnectionStatus.Healthy,    true,  true,    ConnectionState.Syncing)]
    [InlineData(false, true,  false,   false, "Timeout",        ConnectionStatus.Degraded,   true,  false,   ConnectionState.Degraded)]
    [InlineData(false, true,  false,   false, "Authentication", ConnectionStatus.Degraded,   true,  false,   ConnectionState.AuthRequired)]
    [InlineData(false, true,  false,   true,  "Authentication", ConnectionStatus.Degraded,   true,  false,   ConnectionState.AuthRequired)]
    [InlineData(false, true,  false,   true,  null,             ConnectionStatus.Healthy,    true,  false,   ConnectionState.Paused)]
    [InlineData(false, true,  false,   false, null,             ConnectionStatus.Failed,     true,  false,   ConnectionState.Error)]
    [InlineData(false, false, false,   false, null,             ConnectionStatus.Disabled,   true,  false,   ConnectionState.Disabled)]
    [InlineData(false, false, true,    true,  "Authentication", ConnectionStatus.Degraded,   true,  true,    ConnectionState.Archived)]
    public void A_connection_is_in_exactly_one_state(bool setup, bool on, bool archived, bool paused, string? kind,
        ConnectionStatus status, bool synced, bool running, ConnectionState expected)
    {
        var at = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        ConnectionStates.Of(setup, on, archived ? at : null, paused ? at : null, kind, status, synced ? at : null, running)
            .Should().Be(expected);
    }

    // ---- what can be connected -------------------------------------------------------------------

    [Fact]
    public void The_catalog_says_what_each_available_PSA_needs_and_offers_nothing_for_a_planned_one()
    {
        var w = Build();
        using var _ = w.H.Db;

        var catalog = w.Service.Providers();

        var autotask = catalog.Single(p => p.Provider == ProviderType.AutotaskPsa);
        autotask.Available.Should().BeTrue();
        autotask.Credentials.Select(c => (c.Key, c.Secret)).Should().Equal(("ApiIntegrationCode", true), ("UserName", false), ("Secret", true));
        catalog.Single(p => p.Provider == ProviderType.ConnectWisePsa).Credentials.Select(c => c.Key)
            .Should().Equal("CompanyId", "PublicKey", "PrivateKey", "ClientId");

        var planned = catalog.Where(p => !p.Available).ToList();
        planned.Select(p => p.Name).Should().Equal(
            "HaloPSA", "Syncro", "SuperOps", "Atera", "Kaseya BMS", "N-able MSP Manager", "ServiceNow", "Freshservice",
            "Jira Service Management", "ManageEngine ServiceDesk Plus", "Zendesk", "Zoho Desk", "DeskDay");
        // Every PSA the portal has a name for is either connectable or listed as planned: none is left out.
        catalog.Select(p => p.Provider).Should().BeEquivalentTo(Enum.GetValues<ProviderType>());
        planned.Should().OnlyContain(p => p.Credentials.Count == 0 && p.EndpointExample == null, "there is no connector behind a planned PSA, and no form for one");
    }

    [Fact]
    public async Task What_a_PSA_can_do_can_be_asked_of_a_connection_that_is_not_live_yet()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var created = await AddAsync(w);

        (await w.Service.CapabilitiesAsync(created.Id)).Should().NotBeNull();
    }
}
