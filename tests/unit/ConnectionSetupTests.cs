using System.Reflection;
using System.Runtime.ExceptionServices;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Connectors.Mock;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Connectors;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// What an administrator is shown while setting a connection up: the test line by line, how the
/// PSA's values map, how much an import would bring in, and what stands in the way of switching it
/// on. The test used to be one call and one word; a PSA account that could sign in and read
/// nothing passed it, and the connection then failed every sync.
/// </summary>
public class ConnectionSetupTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private const string Base = "https://api-na.myconnectwise.net/v4_6_release/apis/3.0/";

    private static Dictionary<string, string> Keys(string company = "techpio")
        => new() { ["CompanyId"] = company, ["PublicKey"] = "pub", ["PrivateKey"] = "priv", ["ClientId"] = "client" };

    /// <summary>Stands in front of a connector: records what was asked of it and fails the operations it is told to.</summary>
    public class Watched : DispatchProxy
    {
        public IServiceManagementConnector Inner { get; set; } = null!;
        public Dictionary<string, ConnectorException> Failures { get; set; } = null!;
        public List<string> Calls { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            if (Failures.TryGetValue(method.Name, out var failure)) throw failure;
            try
            {
                return method.Invoke(Inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>One PSA, kept for the life of a test, behind the watcher.</summary>
    private sealed class Psa(TestClock clock) : IConnectorResolver
    {
        public MockConnectorOptions Options { get; } = new();
        public Dictionary<string, ConnectorException> Failures { get; } = [];
        public List<string> Calls { get; } = [];
        private MockConnector? _mock;
        public MockConnector Mock => _mock ??= new MockConnector(Options, clock);

        private IServiceManagementConnector Connector()
        {
            var proxy = DispatchProxy.Create<IServiceManagementConnector, Watched>();
            var watched = (Watched)(object)proxy;
            (watched.Inner, watched.Failures, watched.Calls) = (Mock, Failures, Calls);
            return proxy;
        }

        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Connector());

        public Task<IServiceManagementConnector> ResolveForTrialAsync(
            PsaConnection connection, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
            => Task.FromResult(Connector());

        public bool Supports(ProviderType provider) => provider == ProviderType.ConnectWisePsa;

        private static readonly ConnectWiseConnectorFactory ConnectWise = new(null!, null!, null!, TimeProvider.System, null!);
        public IReadOnlyList<ProviderDescriptor> Providers => [ConnectWise.Descriptor];
        public string? AccountKey(ProviderType provider, string apiEndpoint, IReadOnlyDictionary<string, string> credentials)
            => ConnectWise.AccountKey(apiEndpoint, credentials);
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

    private static async Task<Guid> AddAsync(World w, string name = "ConnectWise", string company = "techpio")
        => (await w.Service.CreateAsync(new CreateConnectionInput(name, ProviderType.ConnectWisePsa, Base, null, Keys(company), null))).Id;

    private static Task<CreateTicketResult> TicketAsync(World w, string title = "Printer offline")
        => w.Psa.Mock.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = title, ExternalCompanyId = "ORG-1", IdempotencyKey = Guid.NewGuid().ToString() });

    private static Task<PsaConnection> RowAsync(World w, Guid id) => w.H.Db.PsaConnections.AsNoTracking().SingleAsync(c => c.Id == id);

    private static string Outcome(ConnectionCheckReportDto report, string key) => report.Checks.Single(c => c.Key == key).Outcome;

    private static readonly string[] Writes =
    [
        nameof(IServiceManagementConnector.CreateTicketAsync), nameof(IServiceManagementConnector.UpdateTicketAsync),
        nameof(IServiceManagementConnector.AddPublicNoteAsync), nameof(IServiceManagementConnector.AddAttachmentAsync),
        nameof(IServiceManagementConnector.AddTimeEntryAsync), nameof(IServiceManagementConnector.UpdateTimeEntryAsync),
        nameof(IServiceManagementConnector.DeleteTimeEntryAsync),
    ];

    // ---- the test, line by line ---------------------------------------------------------------

    [Fact]
    public async Task The_test_tries_every_read_the_sync_needs_and_writes_nothing()
    {
        var w = Build();
        await using var _ = w.H.Db;
        await TicketAsync(w);
        var id = await AddAsync(w);

        var report = await w.Service.CheckAsync(id);

        report.Passed.Should().BeTrue();
        report.Checks.Select(c => (c.Key, c.Outcome)).Should().Equal(
            ("authentication", "Pass"), ("tickets.read", "Pass"), ("configuration.read", "Pass"), ("technicians.read", "Pass"),
            ("notes.read", "Pass"), ("time.read", "Pass"), ("time.write", "Pass"),
            ("tickets.write", "NotTested"), ("webhooks", "Available"));
        report.Checks.Single(c => c.Key == "configuration.read").Detail.Should().Be("5 statuses, 4 priorities, 3 queues or boards.");
        report.Checks.Single(c => c.Key == "tickets.write").Detail.Should().Contain("never changes anything in your PSA");
        w.Psa.Calls.Should().NotContain(Writes, "a test that wrote to the PSA to prove it could would be one nobody dared run");
        (await RowAsync(w, id)).Status.Should().Be(ConnectionStatus.Healthy);
        (await w.H.Db.AuditLog.CountAsync(a => a.Action == "connection.checked")).Should().Be(1);
    }

    [Fact]
    public async Task Credentials_the_PSA_rejects_are_tested_no_further()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);
        w.Psa.Options.FailEveryCallWith = ConnectorFailureKind.Authentication;

        var report = await w.Service.CheckAsync(id);

        report.Passed.Should().BeFalse();
        Outcome(report, "authentication").Should().Be("Fail");
        report.Checks.Where(c => c.Key != "authentication").Should().OnlyContain(c => c.Outcome == "NotTested");
        w.Psa.Calls.Should().Equal(nameof(IServiceManagementConnector.TestConnectionAsync));
        var row = await RowAsync(w, id);
        (row.InSetup, row.IsEnabled, row.Status).Should().Be((true, false, ConnectionStatus.Failed));
    }

    [Fact]
    public async Task With_no_ticket_to_ask_about_notes_and_time_are_not_tried_rather_than_passed()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);

        var report = await w.Service.CheckAsync(id);

        report.Passed.Should().BeTrue("an empty PSA is a working one");
        report.Checks.Single(c => c.Key == "tickets.read").Detail.Should().Contain("no tickets yet");
        (Outcome(report, "notes.read"), Outcome(report, "time.read")).Should().Be(("NotTested", "NotTested"));
    }

    [Fact]
    public async Task A_PSA_that_lets_the_portal_in_but_not_at_its_tickets_cannot_be_switched_on()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);
        w.Psa.Failures[nameof(IServiceManagementConnector.GetTicketsAsync)] =
            new ConnectorException(ConnectorFailureKind.PermissionDenied, "The API member may not read service tickets.");

        var report = await w.Service.CheckAsync(id);

        report.Passed.Should().BeFalse("it signed in, and that used to be the whole test");
        (Outcome(report, "authentication"), Outcome(report, "tickets.read")).Should().Be(("Pass", "Fail"));
        var row = await RowAsync(w, id);
        row.Status.Should().Be(ConnectionStatus.Failed);
        row.LastError.Should().Be("Read tickets: PermissionDenied: The API member may not read service tickets.");
        await ((Func<Task>)(() => w.Service.ActivateAsync(id))).Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task A_timeout_while_testing_a_live_connection_does_not_stop_its_sync()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);
        (await w.Service.CheckAsync(id)).Passed.Should().BeTrue();
        await w.Service.ActivateAsync(id);
        w.Psa.Failures[nameof(IServiceManagementConnector.GetTicketsAsync)] = new ConnectorException(ConnectorFailureKind.Timeout, "No answer.");

        var report = await w.Service.CheckAsync(id);

        report.Passed.Should().BeFalse("the report says what happened");
        (await RowAsync(w, id)).Status.Should().Be(ConnectionStatus.Healthy, "one timeout during a test is not a reason to stop a working sync");
        (await SyncSchedule.Due(w.H.Db.PsaConnections).CountAsync()).Should().Be(1);

        // The same PSA saying "you may not" is not something the next run gets past.
        w.Psa.Failures[nameof(IServiceManagementConnector.GetTicketsAsync)] = new ConnectorException(ConnectorFailureKind.PermissionDenied, "Forbidden.");
        await w.Service.CheckAsync(id);
        (await RowAsync(w, id)).Status.Should().Be(ConnectionStatus.Failed);
    }

    // ---- mapping ------------------------------------------------------------------------------

    private static FieldMapping Rule(Guid connection, string field, string external, string portal) => new()
    {
        MspOrganizationId = Org, Provider = ProviderType.ConnectWisePsa, Scope = MappingScope.ConnectionOverride, PsaConnectionId = connection,
        PortalField = field, ExternalField = field, ExternalValue = external, PortalValue = portal, Direction = MappingDirection.Bidirectional,
    };

    [Fact]
    public async Task Each_value_the_PSA_lists_is_shown_with_what_it_becomes_or_that_nothing_maps_it()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);
        var other = await AddAsync(w, "Second account", "elsewhere");
        w.H.Db.FieldMappings.AddRange(
            Rule(id, "status", "New", "NEW"), Rule(id, "status", "Closed", "CLOSED"), Rule(id, "priority", "High", "HIGH"),
            Rule(other, "status", "Resolved", "RESOLVED"));
        await w.H.Db.SaveChangesAsync();

        var coverage = await w.Service.MappingCoverageAsync(id);

        coverage.Statuses.Select(s => (s.Label, s.MapsTo)).Should().Equal(
            ("New", "NEW"), ("In Progress", null), ("Waiting Customer", null), ("Resolved", null), ("Closed", "CLOSED"));
        coverage.Statuses.Single(s => s.Label == "Resolved").MapsTo.Should().BeNull("the rule for it belongs to another connection");
        coverage.Priorities.Count(p => p.MapsTo is null).Should().Be(3);
        coverage.Unmapped.Should().Be(6, "three statuses and three priorities: counted, not guessed");
    }

    // ---- how much an import would bring in ------------------------------------------------------

    [Fact]
    public async Task The_preview_asks_the_PSA_how_much_there_is_and_says_so_when_it_cannot_be_told()
    {
        var w = Build();
        await using var _ = w.H.Db;
        await TicketAsync(w, "one");
        await TicketAsync(w, "two");
        var id = await AddAsync(w);
        (await w.Service.CheckAsync(id)).Passed.Should().BeTrue();
        w.Psa.Calls.Clear();

        var preview = await w.Service.PreviewAsync(id);

        (preview.Clients, preview.Technicians, preview.OpenTickets, preview.AllTickets).Should().Be((1, 1, 2, 2));
        preview.TicketsToImport.Should().Be(2, "open tickets only, which is how a new connection is set");
        preview.Unmapped.Should().Be(9);
        w.Psa.Calls.Should().NotContain(nameof(IServiceManagementConnector.GetTicketsAsync), "it asks for a number, not for the tickets")
            .And.NotContain(Writes);

        w.Psa.Failures[nameof(IServiceManagementConnector.CountTicketsAsync)] = new ConnectorException(ConnectorFailureKind.RateLimited, "Slow down.");
        var blind = await w.Service.PreviewAsync(id);

        (blind.OpenTickets, blind.AllTickets, blind.TicketsToImport).Should().Be((null, null, null), "not known is not zero");
        blind.Notes.Should().Contain(n => n.Contains("could not be counted (RateLimited)"));
    }

    // ---- before it is switched on -------------------------------------------------------------

    [Fact]
    public async Task Preflight_passes_a_tested_connection_and_says_what_is_unmapped_without_blocking()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);

        var before = await w.Service.PreflightAsync(id);
        before.CanEnable.Should().BeFalse();
        before.Items.Single(i => i.Key == "connection").Detail.Should().Be("It has not passed a test yet.");
        before.Items.Single(i => i.Key == "mapping").Outcome.Should().Be("NotTested");

        (await w.Service.CheckAsync(id)).Passed.Should().BeTrue();
        var after = await w.Service.PreflightAsync(id);

        after.CanEnable.Should().BeTrue();
        after.Items.Select(i => (i.Key, i.Outcome)).Should().Equal(
            ("connection", "Pass"), ("credentials", "Pass"), ("mapping", "Warn"), ("scope", "Pass"), ("conflicts", "Pass"));
        after.Items.Single(i => i.Key == "mapping").Detail.Should().StartWith("9 values have no mapping and will arrive as the PSA sends them: New, In Progress");
        after.Items.Single(i => i.Key == "scope").Detail.Should().Be("Open tickets, from every queue or board.");
    }

    [Fact]
    public async Task A_scope_that_would_import_nothing_blocks_the_switch_and_is_said_in_words()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var id = await AddAsync(w);
        (await w.Service.CheckAsync(id)).Passed.Should().BeTrue();
        var row = await w.H.Db.PsaConnections.SingleAsync(c => c.Id == id);

        row.ImportOpenTickets = false;
        await w.H.Db.SaveChangesAsync();
        var nothing = await w.Service.PreflightAsync(id);
        (nothing.CanEnable, nothing.Items.Single(i => i.Key == "scope").Outcome).Should().Be((false, "Fail"));
        (await ((Func<Task>)(() => w.Service.ActivateAsync(id))).Should().ThrowAsync<ValidationFailedException>())
            .WithMessage("Not switched on. Sync scope:*nothing would be imported*");

        (row.ImportOpenTickets, row.FilterQueueIds) = (true, "Service Desk, 404");
        await w.H.Db.SaveChangesAsync();
        var unknown = await w.Service.PreflightAsync(id);
        unknown.Items.Single(i => i.Key == "scope").Detail.Should().StartWith("Not in this PSA's list of queues or boards: 404.");

        row.FilterQueueIds = "Service Desk,Network";
        row.FilterActiveWithinDays = 90;
        await w.H.Db.SaveChangesAsync();
        (await w.Service.PreflightAsync(id)).Items.Single(i => i.Key == "scope")
            .Should().Be(new PreflightItemDto("scope", "Sync scope", "Pass", "Open tickets, active in the last 90 days, from 2 of 3 queues or boards."));
        (await w.Service.ActivateAsync(id)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_credential_blocks_and_a_shared_name_is_only_said()
    {
        var w = Build();
        await using var _ = w.H.Db;
        var first = await AddAsync(w, "Main");
        var id = await AddAsync(w, "Main two", "second-company");
        (await w.Service.CheckAsync(id)).Passed.Should().BeTrue();
        // Two connections that share a name can no longer be made. Ones from before that rule can
        // exist, so the line that says so is still there to say it.
        (await w.H.Db.PsaConnections.SingleAsync(c => c.Id == id)).Name = "Main";
        await w.H.Db.SaveChangesAsync();

        var shared = await w.Service.PreflightAsync(id);
        (shared.CanEnable, shared.Items.Single(i => i.Key == "conflicts").Outcome).Should().Be((true, "Warn"));

        var row = await RowAsync(w, id);
        var kept = new Dictionary<string, string>(await w.H.Secrets.ReadAsync(row.CredentialSecretRef));
        kept.Remove("ClientId");
        await w.H.Secrets.RotateAsync(row.CredentialSecretRef, kept);

        var missing = await w.Service.PreflightAsync(id);
        missing.CanEnable.Should().BeFalse();
        missing.Items.Single(i => i.Key == "credentials").Detail.Should().Be("Not stored: Client ID.");
        first.Should().NotBe(id);
    }
}
