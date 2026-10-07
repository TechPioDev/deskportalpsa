using System.Data.Common;
using System.Diagnostics;
using Desk.Application.Admin;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Application.Tickets;
using Desk.Connectors.Mock;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Mapping;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Secrets;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Desk.Tests.Unit;

/// <summary>
/// Phase 9 at volume: a connection holding twenty thousand tickets, and a first import of five
/// thousand.
///
/// Every other test of these paths holds a handful of rows, where a read that loads every ticket
/// and one that asks the database to count them cost the same. These run on a real database with
/// every command counted, and prove two things: what an administrator opens (a connection's mapping
/// health, its preview, the checks before it is switched on) costs the same number of queries
/// whatever the connection holds, and what works through the tickets (applying a mapping, a sync)
/// does a bounded amount for each and does not slow down as it goes.
///
/// SQLite by default. Set DESK_TEST_POSTGRES and the same tests run on PostgreSQL, in a database of
/// their own made by the real migrations - which is how the figures in
/// docs/integrations/performance.md were measured. Set DESK_TEST_VOLUME as well and the benchmark
/// at the end runs too: a hundred connections, half a million tickets, a million time entries.
/// </summary>
public sealed class ConnectionVolumeTests(ITestOutputHelper output) : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly string? Postgres = Environment.GetEnvironmentVariable("DESK_TEST_POSTGRES");
    /// <summary>
    /// A directory. When set, the benchmark writes every statement of each read there, with the
    /// values it was sent with, and leaves its database in place: a slow statement can then be put
    /// to EXPLAIN exactly as it ran. The database's name is printed; dropping it is by hand.
    /// </summary>
    private static readonly string? Profile = Environment.GetEnvironmentVariable("DESK_TEST_PROFILE");
    private readonly string _database = $"desk_p9_{Guid.NewGuid():N}";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new();
    private readonly Counter _counter = new();

    private sealed class Counter : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        /// <summary>Time the database took to answer, summed. What is left of a measurement is the work done in memory.</summary>
        public double DatabaseMs { get; private set; }
        /// <summary>The time each statement took in all, by its text: which one to look at when a figure grows.</summary>
        private readonly Dictionary<string, (int Times, double Ms)> _statements = [];
        private readonly Dictionary<string, string> _parameters = [];
        private readonly Dictionary<string, List<DbParameter>> _sentWith = [];

        public void Reset()
        {
            (Commands, DatabaseMs) = (0, 0);
            _statements.Clear();
            _parameters.Clear();
            _sentWith.Clear();
        }

        /// <summary>The statements that took longest in all since the last reset.</summary>
        public IEnumerable<string> Costliest(int take) => _statements
            .OrderByDescending(x => x.Value.Ms).Take(take)
            .Select(x => $"{x.Value.Ms:0} ms in {x.Value.Times} ({x.Value.Ms / x.Value.Times:0.00} ms each): {OneLine(x.Key, 230)}");

        /// <summary>The statements since the last reset whose text contains <paramref name="part"/>, costliest first, each with what it was sent with.</summary>
        public IEnumerable<(string Sql, List<DbParameter> Parameters)> Sent(string part) => _statements
            .Where(x => x.Key.Contains(part)).OrderByDescending(x => x.Value.Ms)
            .Select(x => (x.Key, _sentWith.GetValueOrDefault(x.Key) ?? []));

        /// <summary>Every statement since the last reset, costliest first, whole and with the values it was sent with.</summary>
        public string Dump() => string.Join("\n\n", _statements.OrderByDescending(x => x.Value.Ms)
            .Select(x => $"-- {x.Value.Ms:0} ms in {x.Value.Times}\n-- {_parameters.GetValueOrDefault(x.Key)}\n{x.Key};"));

        private static string OneLine(string sql, int max)
        {
            var line = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= max ? line : line[..max] + "...";
        }

        private void Took(DbCommand command, CommandExecutedEventData eventData)
        {
            DatabaseMs += eventData.Duration.TotalMilliseconds;
            var seen = _statements.GetValueOrDefault(command.CommandText);
            _statements[command.CommandText] = (seen.Times + 1, seen.Ms + eventData.Duration.TotalMilliseconds);
            _parameters[command.CommandText] = string.Join(", ", command.Parameters.Cast<DbParameter>().Select(x => $"{x.ParameterName}={x.Value}"));
            _sentWith[command.CommandText] = command.Parameters.Cast<DbParameter>().Where(x => x is ICloneable).Select(x => (DbParameter)((ICloneable)x).Clone()).ToList();
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Took(command, eventData);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        // A statement sent without awaiting it is still a statement, and its time is still the database's.
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Commands++;
            Took(command, eventData);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            Commands++;
            Took(command, eventData);
            return base.ScalarExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Took(command, eventData);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>A unit of work of its own, as each request and each run has. The first one makes the schema.</summary>
    private DeskDbContext NewContext(bool platform = false)
    {
        var tenant = new TenantContext();
        if (platform) tenant.SetPlatformScope(); else tenant.SetTenant(Org);
        var builder = new DbContextOptionsBuilder<DeskDbContext>();
        if (Postgres is null) builder.UseSqlite(_connection);
        else builder.UseNpgsql($"{Postgres};Database={_database}");
        return new DeskDbContext(builder
            .AddInterceptors(_counter)
            .ConfigureWarnings(w => w.Throw(
                RelationalEventId.MultipleCollectionIncludeWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning,
                CoreEventId.FirstWithoutOrderByAndFilterWarning)).Options, tenant, _clock);
    }

    private async Task<DeskDbContext> CreateAsync()
    {
        var db = NewContext();
        if (Postgres is null)
        {
            _connection.Open();
            await db.Database.EnsureCreatedAsync();
        }
        // On PostgreSQL the schema comes from the migrations themselves, as it does in production.
        else await db.Database.MigrateAsync();
        db.Add(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio" });
        await db.SaveChangesAsync();
        return db;
    }

    private async Task<(int Commands, long Ms, T Result, long InMemoryMs)> MeasureAsync<T>(Func<Task<T>> work)
    {
        _counter.Reset();
        var sw = Stopwatch.StartNew();
        var result = await work();
        sw.Stop();
        return (_counter.Commands, sw.ElapsedMilliseconds, result, Math.Max(0, sw.ElapsedMilliseconds - (long)_counter.DatabaseMs));
    }

    /// <summary>
    /// The plan PostgreSQL makes for a statement, with the values it was sent with. Asked on the
    /// connection itself, not through the unit of work, so that asking is not counted as a query.
    /// </summary>
    private static async Task<string> PlanAsync(DeskDbContext db, (string Sql, List<DbParameter> Parameters) statement)
    {
        // Opened here and given back here. Left open, the unit of work would hold it for the rest of
        // the test, and whoever next needed a connection would have to make a new one.
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXPLAIN " + statement.Sql;
            foreach (var parameter in statement.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            return string.Join('\n', lines);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private sealed class Fixed(IServiceManagementConnector c) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static PsaConnection Connection(string name) => new()
    {
        MspOrganizationId = Org, Name = name, Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://cw.example/",
        CredentialSecretRef = "mem://" + name, Status = ConnectionStatus.Healthy, IsEnabled = true, SyncAttachments = false,
    };

    // What the PSA sends. Five statuses have a rule; "Complete" and "Scheduled" have none, and neither has "Medium".
    private static readonly (string Psa, string? Portal)[] Statuses =
    [
        ("New", "NEW"), ("In Progress", "IN_PROGRESS"), ("Waiting Customer", "WAITING_CUSTOMER"), ("Resolved", "RESOLVED"),
        ("Closed", "CLOSED"), ("Complete", null), ("Scheduled", null),
    ];
    private static readonly (string Psa, string? Portal)[] Priorities = [("Critical", "CRITICAL"), ("High", "HIGH"), ("Low", "LOW"), ("Medium", null)];
    private const int Clients = 40;
    private const int Queues = 4;
    private const int Technicians = 25;
    private const int Linked = 10;

    /// <summary>
    /// A connection of <paramref name="tickets"/> tickets as a sync would have left them: forty
    /// clients, four boards, twenty-five technicians of whom ten are linked to a portal user, and
    /// the values no rule maps passed through as the PSA wrote them.
    /// </summary>
    private async Task<(Guid Connection, Guid Other, int Unmapped, int UnmappedStatus, int UnmappedPriority)> SeedAsync(DeskDbContext db, int tickets)
    {
        var main = Connection("Main");
        var other = Connection("Other account");
        db.AddRange(main, other);
        var clients = Enumerable.Range(0, Clients)
            .Select(i => new ClientCompany { MspOrganizationId = Org, PsaConnectionId = main.Id, Name = $"Client {i:00}", ExternalCompanyId = $"co-{i}" })
            .ToList();
        db.AddRange(clients);
        // Five people at two of its clients, and a client of the other account's with one of its own.
        var theirs = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = other.Id, Name = "Their client", ExternalCompanyId = "co-0" };
        db.Add(theirs);
        ClientUser Person(ClientCompany at, int n) => new() { MspOrganizationId = Org, ClientCompanyId = at.Id, Email = $"p{n}@{at.ExternalCompanyId}.{at.PsaConnectionId:N}.test", DisplayName = $"Person {n}" };
        db.AddRange(Person(clients[0], 1), Person(clients[0], 2), Person(clients[0], 3), Person(clients[1], 1), Person(clients[1], 2), Person(theirs, 1));
        foreach (var (psa, portal) in Statuses.Where(s => s.Portal is not null))
            db.Add(Rule(main.Id, "status", psa, portal!));
        foreach (var (psa, portal) in Priorities.Where(p => p.Portal is not null))
            db.Add(Rule(main.Id, "priority", psa, portal!));
        for (var i = 0; i < Linked; i++)
        {
            var user = new AppUser { MspOrganizationId = Org, DisplayName = $"Tech {i:00}", Email = $"tech{i}@techpio.test", IsActive = true };
            db.Add(user);
            db.Add(new UserPsaIdentity { MspOrganizationId = Org, AppUserId = user.Id, PsaConnectionId = main.Id, ExternalTechnicianId = $"m-{i}", ExternalTechnicianName = $"Tech {i:00}" });
        }
        await db.SaveChangesAsync();

        int unmapped = 0, unmappedStatus = 0, unmappedPriority = 0;
        for (var i = 0; i < tickets; i++)
        {
            // Coprime strides, so every combination of status, priority, board and client turns up.
            var (status, portalStatus) = Statuses[i % Statuses.Length];
            var (priority, portalPriority) = Priorities[i / 7 % Priorities.Length];
            if (portalStatus is null) unmappedStatus++;
            if (portalPriority is null) unmappedPriority++;
            if (portalStatus is null || portalPriority is null) unmapped++;
            db.Add(new Ticket
            {
                MspOrganizationId = Org, PsaConnectionId = main.Id, Provider = ProviderType.ConnectWisePsa, Origin = TicketOrigin.Psa,
                ExternalTicketId = (100_000 + i).ToString(), RequesterName = "r", RequesterEmail = "r@a.test", Title = $"Ticket {i}",
                PsaStatus = status, PortalStatus = portalStatus ?? status, PsaPriority = priority, PortalPriority = portalPriority ?? priority,
                QueueOrBoard = $"Board {i / 3 % Queues}", ClientCompanyId = clients[i / 11 % Clients].Id,
                AssignedTechnicianExternalId = $"m-{i % Technicians}", AssignedTechnicianName = $"Tech {i % Technicians:00}",
                LastSyncedAt = _clock.GetUtcNow().AddMinutes(-i), SyncStatus = TicketSyncStatus.Synced,
            });
            if (i % 2_000 == 1_999)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }
        // Another PSA account of the same kind, holding the very values this one has no rule for. They are not this one's.
        for (var i = 0; i < 200; i++)
            db.Add(new Ticket
            {
                MspOrganizationId = Org, PsaConnectionId = other.Id, Provider = ProviderType.ConnectWisePsa, Origin = TicketOrigin.Psa,
                ExternalTicketId = (900_000 + i).ToString(), RequesterName = "r", RequesterEmail = "r@a.test", Title = $"Theirs {i}",
                PsaStatus = "Complete", PortalStatus = "Complete", PsaPriority = "Medium", PortalPriority = "Medium", SyncStatus = TicketSyncStatus.Synced,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (main.Id, other.Id, unmapped, unmappedStatus, unmappedPriority);
    }

    private static FieldMapping Rule(Guid connection, string field, string external, string portal) => new()
    {
        MspOrganizationId = Org, Provider = ProviderType.ConnectWisePsa, Scope = MappingScope.ConnectionOverride, PsaConnectionId = connection,
        PortalField = field, ExternalField = field, ExternalValue = external, PortalValue = portal, Direction = MappingDirection.Bidirectional,
    };

    private (ConnectionAdminService Connections, MappingAdminService Mappings) Services(DeskDbContext db)
    {
        var user = new TestCurrentUser(Org);
        var tenant = new TenantContext();
        tenant.SetTenant(Org);
        var audit = new AuditWriter(db, user, tenant, _clock);
        return (new ConnectionAdminService(db, new InMemorySecretStore(), audit, new Fixed(new MockConnector(new MockConnectorOptions(), _clock)),
                new ConnectionFieldCache(), new InMemoryObjectStorage(new AttachmentStorageOptions(), _clock), _clock),
            new MappingAdminService(db, audit, user));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(20_000)]
    public async Task What_an_administrator_opens_costs_the_same_number_of_queries_whatever_the_connection_holds(int tickets)
    {
        await using var db = await CreateAsync();
        var seeded = await SeedAsync(db, tickets);
        var (connections, mappings) = Services(db);

        var list = await MeasureAsync(() => connections.ListAsync());
        var health = await MeasureAsync(() => connections.MappingHealthAsync(seeded.Connection));
        var sample = await MeasureAsync(() => connections.MappingPreviewAsync(seeded.Connection, 10));
        var coverage = await MeasureAsync(() => connections.MappingCoverageAsync(seeded.Connection));
        var preview = await MeasureAsync(() => connections.PreviewAsync(seeded.Connection));
        var preflight = await MeasureAsync(() => connections.PreflightAsync(seeded.Connection));

        // What each holds, and nothing of the other's.
        list.Result.Select(c => (c.Name, c.TicketCount, c.CustomerCount, c.ContactCount))
            .Should().BeEquivalentTo(new[] { ("Main", tickets, Clients, 5), ("Other account", 200, 1, 1) });
        (health.Result.Tickets, health.Result.UnmappedTickets).Should().Be((tickets, seeded.Unmapped), "each ticket is counted once, and the other account's are not counted at all");
        health.Result.Level.Should().Be(ConnectionAdminService.Warning);
        var statuses = health.Result.Fields.Single(f => f.Field == "status");
        statuses.UnmappedTickets.Should().Be(seeded.UnmappedStatus);
        statuses.Items.Where(i => i.UnmappedTickets > 0).Select(i => i.Value).Should().BeEquivalentTo(["Complete", "Scheduled"]);
        health.Result.Fields.Single(f => f.Field == "priority").UnmappedTickets.Should().Be(seeded.UnmappedPriority);
        // The PSA lists a technician of its own besides the twenty-five who hold tickets.
        health.Result.Technicians.Unlinked.Where(p => p.ExternalId.StartsWith("m-")).Should().HaveCount(Technicians - Linked).And.OnlyContain(p => p.Tickets > 0);
        sample.Result.Should().HaveCount(10);

        // The same at a thousand tickets and at twenty thousand: the database counts, and nothing is read a ticket at a time.
        // Measured 5 / 10 / 3 / 3 / 5 / 6 on SQLite and on PostgreSQL alike.
        list.Commands.Should().BeLessThanOrEqualTo(5);
        health.Commands.Should().BeLessThanOrEqualTo(10);
        sample.Commands.Should().BeLessThanOrEqualTo(3);
        coverage.Commands.Should().BeLessThanOrEqualTo(3);
        preview.Commands.Should().BeLessThanOrEqualTo(5);
        preflight.Commands.Should().BeLessThanOrEqualTo(6);
        foreach (var ms in new[] { list.Ms, health.Ms, sample.Ms, coverage.Ms, preview.Ms, preflight.Ms }) ms.Should().BeLessThan(30_000);

        output.WriteLine($"{tickets} tickets | connections {list.Commands} q {list.Ms} ms | mapping health {health.Commands} q {health.Ms} ms | sample {sample.Commands} q {sample.Ms} ms"
            + $" | coverage {coverage.Commands} q {coverage.Ms} ms | preview {preview.Commands} q {preview.Ms} ms | preflight {preflight.Commands} q {preflight.Ms} ms");

        // The three values nothing maps are given a meaning, and the tickets that passed through are put right.
        var saved = await MeasureAsync(() => mappings.SetInboundAsync(seeded.Connection,
        [
            new SetInboundMappingInput("status", "Complete", "CLOSED"),
            new SetInboundMappingInput("status", "Scheduled", "IN_PROGRESS"),
            new SetInboundMappingInput("priority", "Medium", "NORMAL"),
        ], "volume"));
        saved.Result.Changed.Should().Be(3);
        db.ChangeTracker.Clear();

        var applied = await MeasureAsync(() => connections.ApplyMappingAsync(seeded.Connection));
        (applied.Result.StatusesChanged, applied.Result.PrioritiesChanged).Should().Be((seeded.UnmappedStatus, seeded.UnmappedPriority));
        // At most a command a ticket (SQLite writes a row at a time; PostgreSQL a batch at a time) and a
        // fixed few for each batch of five hundred. It was one pass for each client on each board: 873
        // commands for these 530 tickets at a thousand, and it grew with the clients, not the tickets.
        var rewritten = seeded.UnmappedStatus + seeded.UnmappedPriority;
        applied.Commands.Should().BeLessThanOrEqualTo(rewritten + 2 * (rewritten / 500 + 3) + 10);
        db.ChangeTracker.Entries<Ticket>().Should().BeEmpty("a batch is let go of once it is saved");
        saved.Commands.Should().BeLessThanOrEqualTo(9);
        applied.Ms.Should().BeLessThan(120_000);
        db.ChangeTracker.Clear();

        var after = await MeasureAsync(() => connections.MappingHealthAsync(seeded.Connection));
        after.Result.UnmappedTickets.Should().Be(0);
        (await db.Tickets.AsNoTracking().CountAsync(t => t.PsaConnectionId == seeded.Other && t.PortalStatus == "Complete" && t.PortalPriority == "Medium"))
            .Should().Be(200, "the other account's tickets are not this connection's to rewrite");
        (await connections.ApplyMappingAsync(seeded.Connection)).Should().BeEquivalentTo(new MappingApplyResultDto(0, 0, []), "done once, there is nothing left to do");

        output.WriteLine($"{tickets} tickets | save 3 rules {saved.Commands} q {saved.Ms} ms | apply to {seeded.UnmappedStatus} + {seeded.UnmappedPriority} tickets {applied.Commands} q {applied.Ms} ms | health after {after.Commands} q {after.Ms} ms");
    }

    private static UnifiedTicket Arriving(int i, DateTimeOffset modifiedAt) => new()
    {
        ExternalId = (100_000 + i).ToString(), Title = $"Ticket {i}", Status = Statuses[i % Statuses.Length].Psa, Priority = Priorities[i / 7 % Priorities.Length].Psa,
        QueueOrBoard = $"Board {i / 3 % Queues}", RequesterExternalId = $"co-{i / 11 % Clients}", CompanyName = $"Client {i / 11 % Clients:00}",
        AssignedTechnicianExternalId = $"m-{i % Technicians}", RequesterName = "Requester", RequesterEmail = "r@a.test", ModifiedAt = modifiedAt, CreatedAt = modifiedAt,
    };

    /// <summary>
    /// A first import, then the same tickets read again with nothing changed. The work for one
    /// ticket is the same for the five-thousandth as for the first.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(5_000)]
    public async Task A_sync_does_the_same_work_for_each_ticket_however_many_it_reads(int tickets)
    {
        Guid connectionId;
        await using (var db = await CreateAsync())
        {
            var connection = Connection("Main");
            connectionId = connection.Id;
            db.Add(connection);
            foreach (var (psa, portal) in Statuses.Where(s => s.Portal is not null)) db.Add(Rule(connection.Id, "status", psa, portal!));
            await db.SaveChangesAsync();
        }
        var psaSide = new StubConnector(ProviderType.ConnectWisePsa) { Paged = true, SupportsAttachmentDownload = false };
        for (var i = 0; i < tickets; i++) psaSide.Tickets.Add(Arriving(i, _clock.GetUtcNow().AddDays(-1)));
        for (var i = 0; i < Technicians; i++) psaSide.Technicians.Add(new ExternalTechnician($"m-{i}", $"tech{i}@psa.test", $"Tech {i:00}", true));

        async Task<(int Commands, long Ms, SyncRunResult Result, int Held, long InMemoryMs)> RunAsync(bool full)
        {
            // A run has a unit of work of its own, as it does in the worker.
            await using var db = NewContext(platform: true);
            var runner = new ConnectionSyncRunner(db, new Fixed(psaSide),
                new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, _clock), _clock, new RecordingActivity()),
                new InMemoryObjectStorage(new AttachmentStorageOptions(), _clock), new HeuristicMalwareScanner(), _clock);
            var run = await MeasureAsync(() => runner.RunAsync(connectionId, new SyncRunRequest(full)));
            return (run.Commands, run.Ms, run.Result, db.ChangeTracker.Entries().Count(), run.InMemoryMs);
        }

        var first = await RunAsync(full: false);
        (first.Result.Fetched, first.Result.Created, first.Result.MoreToRead, first.Result.Failed).Should().Be((tickets, tickets, false, 0));

        _clock.Advance(TimeSpan.FromMinutes(5));
        var quiet = await RunAsync(full: false);
        quiet.Result.Fetched.Should().Be(0, "nothing has changed since the first read began");

        _clock.Advance(TimeSpan.FromMinutes(5));
        var again = await RunAsync(full: true);
        (again.Result.Fetched, again.Result.Created, again.Result.Updated, again.Result.Failed).Should().Be((tickets, 0, 0, 0), "read again, and found as it was left");

        // Printed before anything is held to a figure, so a run that misses one still says what it measured.
        output.WriteLine($"{tickets} tickets | first import {first.Commands} q ({(double)first.Commands / tickets:0.0} a ticket) {first.Ms} ms ({(double)first.Ms / tickets:0.00} ms a ticket; {first.InMemoryMs} ms of it not the database)"
            + $" | nothing changed {quiet.Commands} q {quiet.Ms} ms | read again, unchanged {again.Commands} q ({(double)again.Commands / tickets:0.0} a ticket) {again.Ms} ms ({(double)again.Ms / tickets:0.00} ms a ticket; {again.InMemoryMs} ms of it not the database) | held at the end of a run: {first.Held} and {again.Held} rows");

        // The work for a ticket: measured 9.1 commands on a first import and 7.1 when it is read again
        // unchanged, at five hundred and at five thousand alike.
        first.Commands.Should().BeLessThanOrEqualTo(tickets * 10 + 50);
        again.Commands.Should().BeLessThanOrEqualTo(tickets * 8 + 50);
        quiet.Commands.Should().BeLessThanOrEqualTo(20);
        // And what a run holds on to. It used to keep every ticket it had read, and each save looked
        // through all of them: on PostgreSQL this test took nine minutes at five thousand, and now takes under two.
        // Held to what it holds, not to a time: the time is the database's and the machine's, and what is held is the cause.
        new[] { first.Held, again.Held }.Should().OnlyContain(held => held <= 150, "a page is let go of once it is saved");

        await using var check = NewContext();
        (await check.Tickets.AsNoTracking().CountAsync()).Should().Be(tickets);
        (await check.ClientCompanies.AsNoTracking().CountAsync()).Should().Be(Math.Min(Clients, (tickets + 10) / 11));
        (await check.Tickets.AsNoTracking().CountAsync(t => t.PortalStatus == "CLOSED")).Should().Be(tickets / Statuses.Length + (tickets % Statuses.Length > 4 ? 1 : 0));
    }

    // ---- the benchmark -------------------------------------------------------------------------

    /// <summary>
    /// Copies one row of a table <paramref name="from"/> times over, in the database, with some of
    /// its columns worked out from the copy's number. Half a million tickets take seconds this way
    /// and minutes through a unit of work, and the copy has every column the table has today
    /// without this test naming them.
    /// </summary>
    private static async Task<int> CloneAsync(DeskDbContext db, string table, Guid template, string from, Dictionary<string, string> columns, params object[] parameters)
    {
        var names = await db.Database
            .SqlQueryRaw<string>("SELECT column_name::text AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0} AND is_generated = 'NEVER' ORDER BY ordinal_position", table)
            .ToListAsync();
        columns.Keys.Should().BeSubsetOf(names, "a column this test fills in was renamed");
        var into = string.Join(", ", names.Select(n => $"\"{n}\""));
        var select = string.Join(", ", names.Select(n => columns.TryGetValue(n, out var expression) ? expression : $"t.\"{n}\""));
        return await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO \"{table}\" ({into}) SELECT {select} FROM \"{table}\" t CROSS JOIN {from} WHERE t.\"Id\" = '{template}'", parameters);
    }

    /// <summary>
    /// The benchmark Phase 9 was asked for: ten connections and a hundred thousand tickets, then a
    /// hundred connections, half a million tickets and a million time entries. One PSA account holds
    /// half of everything, as the largest customer of a desk does. Measured: the connection list
    /// and what the worker asks for, a connection's sync health with a year of runs behind it, its
    /// mapping page, the ticket list everyone opens, and a sync - of tickets that changed, and of a
    /// new connection's first five thousand.
    ///
    /// PostgreSQL only, and only when asked for (DESK_TEST_VOLUME=1 beside DESK_TEST_POSTGRES): it
    /// takes minutes. Nothing here asserts a time a slower machine would miss; the figures are
    /// printed for docs/integrations/performance.md. What is asserted is that the number of queries
    /// does not move with the size of the data.
    /// </summary>
    [Theory]
    [InlineData(10, 100_000)]
    [InlineData(100, 500_000)]
    public async Task The_benchmark_at_ten_and_a_hundred_connections(int connections, int tickets)
    {
        if (Postgres is null || Environment.GetEnvironmentVariable("DESK_TEST_VOLUME") is null) return;
        const int perConnection = 20;
        await using var db = await CreateAsync();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));

        var role = new Role { MspOrganizationId = Org, Name = "Administrator", BuiltInType = RoleType.MspAdministrator };
        role.Permissions.Add(new RolePermission { PermissionKey = Permissions.TicketsViewAll, Scope = PermissionScope.All });
        var admin = new AppUser { MspOrganizationId = Org, DisplayName = "Admin", Email = "admin@techpio.test", IsActive = true };
        admin.Roles.Add(new UserRole { RoleId = role.Id });
        db.AddRange(role, admin);
        var all = Enumerable.Range(0, connections).Select(i => Connection($"Connection {i:000}")).ToList();
        db.AddRange(all);
        var clients = all.SelectMany((c, i) => Enumerable.Range(0, perConnection)
            .Select(k => new ClientCompany { MspOrganizationId = Org, PsaConnectionId = c.Id, Name = $"Client {i:000}-{k:00}", ExternalCompanyId = $"co-{k}" })).ToList();
        db.AddRange(clients);
        foreach (var (psa, portal) in Statuses.Where(x => x.Portal is not null)) db.Add(Rule(all[0].Id, "status", psa, portal!));
        foreach (var (psa, portal) in Priorities.Where(x => x.Portal is not null)) db.Add(Rule(all[0].Id, "priority", psa, portal!));
        var ticket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = all[0].Id, Provider = ProviderType.ConnectWisePsa, Origin = TicketOrigin.Psa, ExternalTicketId = "template",
            RequesterName = "Requester", RequesterEmail = "r@a.test", Title = "Template", Description = new string('x', 600),
            PsaStatus = "New", PortalStatus = "NEW", PsaPriority = "High", PortalPriority = "HIGH", ClientCompanyId = clients[0].Id, SyncStatus = TicketSyncStatus.Synced,
            TimeWorkedHours = 1m, BillableHours = 0.5m,
        };
        var entry = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, Hours = 0.5m, Billable = true, ExternalEntryId = "template", TechnicianExternalId = "m-0", TechnicianName = "Tech 00",
            Source = TimeEntrySource.Provider, SyncStatus = TimeEntrySyncStatus.Synced, EntryDate = _clock.GetUtcNow(),
        };
        var ran = new SyncRun
        {
            MspOrganizationId = Org, PsaConnectionId = all[0].Id, Trigger = SyncRunTrigger.Scheduled, Status = SyncRunStatus.Succeeded,
            StartedAt = _clock.GetUtcNow(), FinishedAt = _clock.GetUtcNow(), LeaseExpiresAt = _clock.GetUtcNow(),
        };
        db.AddRange(ticket, entry, ran);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var seeding = Stopwatch.StartNew();
        // Which connection the n-th ticket belongs to: every other one to the first, the rest in turn.
        var connection = $"(CASE WHEN n % 2 = 0 THEN 0 ELSE 1 + (n / 2) % {Math.Max(1, connections - 1)} END)";
        var copied = await CloneAsync(db, "tickets", ticket.Id, $"generate_series(1, {tickets}) AS n", new()
        {
            ["Id"] = "gen_random_uuid()",
            ["PsaConnectionId"] = $"(@connections)[1 + {connection}]",
            ["ClientCompanyId"] = $"(@clients)[1 + {connection} * {perConnection} + (n / 11) % {perConnection}]",
            ["ExternalTicketId"] = "(1000000 + n)::text",
            ["Title"] = "'Ticket ' || n",
            ["PsaStatus"] = "(ARRAY['New','In Progress','Waiting Customer','Resolved','Closed','Complete','Scheduled'])[1 + n % 7]",
            ["PortalStatus"] = "(ARRAY['NEW','IN_PROGRESS','WAITING_CUSTOMER','RESOLVED','CLOSED','Complete','Scheduled'])[1 + n % 7]",
            ["PsaPriority"] = "(ARRAY['Critical','High','Low','Medium'])[1 + (n / 7) % 4]",
            ["PortalPriority"] = "(ARRAY['CRITICAL','HIGH','LOW','Medium'])[1 + (n / 7) % 4]",
            ["QueueOrBoard"] = "'Board ' || (n / 3) % 4",
            ["AssignedTechnicianExternalId"] = "'m-' || n % 25",
            ["AssignedTechnicianName"] = "'Tech ' || n % 25",
            // A ticket a minute, going back: half a million of them is about a year of a busy desk.
            ["CreatedAt"] = "now() - make_interval(mins => n)",
            ["UpdatedAt"] = "now() - make_interval(mins => n / 2)",
            ["LastSyncedAt"] = "now() - make_interval(mins => n / 2)",
        }, new Npgsql.NpgsqlParameter("connections", all.Select(c => c.Id).ToArray()), new Npgsql.NpgsqlParameter("clients", clients.Select(c => c.Id).ToArray()));
        copied.Should().Be(tickets);
        var entries = await CloneAsync(db, "ticket_time_entries", entry.Id, "tickets k CROSS JOIN generate_series(1, 2) AS n", new()
        {
            ["Id"] = "gen_random_uuid()",
            ["TicketId"] = "k.\"Id\"",
            // Left without its account, as every entry was before the column existed: the migration's own statement gives it below.
            ["PsaConnectionId"] = "NULL",
            ["ExternalEntryId"] = "k.\"ExternalTicketId\" || '-' || n",
            ["TechnicianExternalId"] = "k.\"AssignedTechnicianExternalId\"",
            ["TechnicianName"] = "k.\"AssignedTechnicianName\"",
            ["EntryDate"] = "k.\"CreatedAt\" + make_interval(hours => n)",
        });
        // The statement the migration runs on entries already there, on all of these.
        var placing = Stopwatch.StartNew();
        var placed = await db.Database.ExecuteSqlRawAsync(Desk.Infrastructure.Persistence.Configurations.TicketIndexes.GiveTimeEntriesTheirAccount);
        placing.Stop();
        placed.Should().Be(entries, "every one of them is on a PSA ticket");
        (await db.TicketTimeEntries.AsNoTracking().CountAsync(e => e.PsaConnectionId == null)).Should().Be(0, "the one the rest were copied from was given its own as it was saved");
        // A run every five minutes for a year, on the one connection.
        var runs = await CloneAsync(db, "sync_runs", ran.Id, "generate_series(1, 105000) AS n", new()
        {
            ["Id"] = "gen_random_uuid()",
            ["StartedAt"] = "now() - make_interval(mins => n * 5)",
            ["FinishedAt"] = "now() - make_interval(mins => n * 5) + interval '20 seconds'",
        });
        // What autovacuum does to a table that has been there a while. Without it the planner knows nothing of
        // what was just loaded, and a count that an index alone could answer visits every row to see if it is visible.
        await db.Database.ExecuteSqlRawAsync("VACUUM ANALYZE");
        seeding.Stop();
        // What the indexes these reads rest on cost in space, beside the tables they are on.
        var sizes = await db.Database.SqlQueryRaw<string>("""
            SELECT relname || ' ' || pg_size_pretty(pg_relation_size(oid)) AS "Value" FROM pg_class
            WHERE relname IN ('tickets', 'ticket_time_entries', 'IX_tickets_open', 'IX_tickets_search', 'IX_tickets_MspOrganizationId_ResolvedByAppUserId',
                'IX_tickets_MspOrganizationId_PsaConnectionId_AssignedTechnicia~', 'IX_ticket_time_entries_MspOrganizationId_PsaConnectionId_Techn~')
            ORDER BY relkind DESC, relname
            """).ToListAsync();
        var allIndexes = await db.Database.SqlQueryRaw<string>("""
            SELECT relname || ' indexes ' || pg_size_pretty(pg_indexes_size(oid)) AS "Value" FROM pg_class WHERE relname IN ('tickets', 'ticket_time_entries') ORDER BY relname
            """).ToListAsync();

        var big = all[0].Id;
        var held = await db.Tickets.AsNoTracking().CountAsync(t => t.PsaConnectionId == big);
        var (admins, _) = Services(db);
        var user = new TestCurrentUser(Org, userId: admin.Id);
        var permissions = new EffectivePermissionService(db);
        var reads = new TicketReadService(db, new TicketScopeQuery(db, permissions), user);
        var tenant = new TenantContext();
        tenant.SetTenant(Org);
        var health = new SyncHealthService(db, new AuditWriter(db, user, tenant, _clock), _clock);

        // Twice each, and the second is the one reported: the first pays for compiling the query, once per process.
        var slow = new List<string>();
        async Task<(int Commands, long Ms, T Result, long InMemoryMs)> WarmAsync<T>(string what, Func<Task<T>> work)
        {
            await work();
            db.ChangeTracker.Clear();
            var measured = await MeasureAsync(work);
            // Anything a person would notice waiting for says which statement the time went on.
            if (measured.Ms >= 150) slow.AddRange(_counter.Costliest(2).Select(line => $"    {what}, costliest: {line}"));
            if (Profile is not null) File.WriteAllText(Path.Combine(Profile, $"{tickets}-{what.Replace(' ', '-')}.sql"), _counter.Dump());
            return measured;
        }

        var list = await WarmAsync("connection list", () => admins.ListAsync());
        var due = await WarmAsync("due for a sync", () => SyncSchedule.Due(db.PsaConnections.AsNoTracking()).Select(c => c.Id).ToListAsync());
        var state = await WarmAsync("sync health", () => health.StateAsync(big));
        var mapping = await WarmAsync("mapping health", () => admins.MappingHealthAsync(big));
        var sample = await WarmAsync("mapping sample", () => admins.MappingPreviewAsync(big, 10));
        var page = await WarmAsync("first page", () => reads.PageAsync(new TicketQuery(Take: 50)));
        var deep = await WarmAsync("page 201", () => reads.PageAsync(new TicketQuery(Take: 50, Skip: 10_000)));
        var byStatus = await WarmAsync("one status", () => reads.PageAsync(new TicketQuery(Status: "IN_PROGRESS", Take: 50)));
        var byConnection = await WarmAsync("one connection", () => reads.PageAsync(new TicketQuery(ConnectionName: all[^1].Name, Take: 50)));
        var search = await WarmAsync("search", () => reads.SearchAsync(new TicketQuery(Q: "Ticket 4242")));
        var summary = await WarmAsync("summary", () => reads.SummaryAsync(mineOnly: false));
        var summaryPlan = await PlanAsync(db, _counter.Sent("GROUP BY").First());
        var facets = await WarmAsync("filter lists", () => reads.FacetsAsync());
        var walks = _counter.Sent("WITH RECURSIVE").Count();
        var searchPlans = new List<string>();
        await MeasureAsync(() => reads.SearchAsync(new TicketQuery(Q: "Ticket 4242")));
        foreach (var statement in _counter.Sent("LIKE").Where(x => x.Sql.Contains("FROM tickets"))) searchPlans.Add(await PlanAsync(db, statement));

        list.Result.Should().HaveCount(connections);
        // Every ticket is on one connection or another, and the first holds half of them.
        (list.Result.Sum(c => c.TicketCount), list.Result.Single(c => c.Id == big).TicketCount, list.Result.Sum(c => c.CustomerCount)).Should().Be((tickets + 1, held, clients.Count));
        due.Result.Should().HaveCount(connections);
        state.Result.Runs.Should().HaveCount(20);
        mapping.Result.Tickets.Should().Be(held);
        (page.Result.Total, page.Result.Items.Count).Should().Be((tickets + 1, 50));
        // Every ticket carries an hour worked, half of it billable: the page's totals are of all of them, not of the fifty shown.
        (page.Result.HoursWorked, page.Result.HoursBillable).Should().Be((tickets + 1, (tickets + 1) / 2m));
        byConnection.Result.Total.Should().BeGreaterThan(0).And.BeLessThan(tickets);
        search.Result.Total.Should().BeGreaterThan(0);

        // The three reads that were a pass over every ticket for each thing they show. Held first to
        // what they answer - five tickets in seven are open here, and each connection has twenty-five
        // logins that hold tickets and log time - and then to how they found it: the search from
        // its index, the summary from the index of open work, the people by walking theirs.
        var open = Enumerable.Range(1, tickets).Count(n => n % 7 is not (3 or 4)) + 1;
        (summary.Result.Open, summary.Result.Unassigned, summary.Result.Overdue).Should().Be((open, 1, 0), "only the one ticket the rest were copied from is held by nobody");
        summary.Result.OpenBySource.Sum(x => x.Count).Should().Be(open);
        summary.Result.OpenByPriority.Select(x => x.Label).Should().BeEquivalentTo(["CRITICAL", "HIGH", "LOW", "MEDIUM"]);
        facets.Result.People.Should().HaveCount(connections * 25);
        (facets.Result.Statuses.Count, facets.Result.Priorities.Count, facets.Result.Queues.Count, facets.Result.Sources.Count).Should().Be((7, 4, 4, connections));
        summaryPlan.Should().Contain(Desk.Infrastructure.Persistence.Configurations.TicketIndexes.Open, "the dashboard is counted from the index of open work, not from the tickets");
        searchPlans.Should().NotBeEmpty().And.OnlyContain(plan => plan.Contains(Desk.Infrastructure.Persistence.Configurations.TicketIndexes.Search), "a search is answered from its index");
        walks.Should().Be(5, "the people are found by walking five indexes: three for people here, two for PSA logins");
        search.Commands.Should().BeLessThanOrEqualTo(8);
        summary.Commands.Should().BeLessThanOrEqualTo(8);
        facets.Commands.Should().BeLessThanOrEqualTo(17);

        // The same number of queries at a hundred thousand and at half a million, at ten connections and at a hundred.
        list.Commands.Should().BeLessThanOrEqualTo(5);
        due.Commands.Should().Be(1);
        state.Commands.Should().BeLessThanOrEqualTo(4);
        mapping.Commands.Should().BeLessThanOrEqualTo(10);
        sample.Commands.Should().BeLessThanOrEqualTo(3);

        output.WriteLine($"{connections} connections, {tickets} tickets ({held} on one connection), {entries} time entries, {runs} sync runs; loaded in {seeding.ElapsedMilliseconds / 1000} s");
        output.WriteLine("  sizes: " + string.Join(" | ", sizes.Concat(allIndexes)));
        output.WriteLine($"  the migration's statement gave {placed} time entries their account in {placing.ElapsedMilliseconds} ms");
        output.WriteLine($"  connections: list {list.Commands} q {list.Ms} ms | due for a sync {due.Commands} q {due.Ms} ms | sync health {state.Commands} q {state.Ms} ms");
        output.WriteLine($"  mapping page ({held} tickets): health {mapping.Commands} q {mapping.Ms} ms | sample {sample.Commands} q {sample.Ms} ms");
        output.WriteLine($"  ticket list: first page {page.Commands} q {page.Ms} ms | page 201 {deep.Commands} q {deep.Ms} ms | one status {byStatus.Commands} q {byStatus.Ms} ms"
            + $" | one connection {byConnection.Commands} q {byConnection.Ms} ms | search {search.Commands} q {search.Ms} ms | summary {summary.Commands} q {summary.Ms} ms | filter lists {facets.Commands} q {facets.Ms} ms");

        foreach (var line in slow) output.WriteLine(line);

        // A sync of what changed: a hundred tickets the big connection already holds, with a new status, and a hundred it has not seen.
        var changed = new StubConnector(ProviderType.ConnectWisePsa) { Paged = true, SupportsAttachmentDownload = false };
        var changedAt = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 100; i++) changed.Tickets.Add(Arriving(i * 2, changedAt) with { ExternalId = (1_000_000 + i * 2).ToString(), Status = "Closed" });
        for (var i = 1; i <= 100; i++) changed.Tickets.Add(Arriving(i, changedAt) with { ExternalId = (9_000_000 + i).ToString() });
        async Task<(int Commands, long Ms, SyncRunResult Result, long InMemoryMs)> SyncAsync(Guid connectionId, StubConnector connector)
        {
            await using var own = NewContext(platform: true);
            var runner = new ConnectionSyncRunner(own, new Fixed(connector),
                new TicketSyncService(own, new MappingEngine(), new SyncEventStore(own, _clock), _clock, new RecordingActivity()),
                new InMemoryObjectStorage(new AttachmentStorageOptions(), _clock), new HeuristicMalwareScanner(), _clock);
            return await MeasureAsync(() => runner.RunAsync(connectionId, new SyncRunRequest()));
        }
        var incremental = await SyncAsync(big, changed);
        var costliest = _counter.Costliest(4).ToList();
        (incremental.Result.Fetched, incremental.Result.Created, incremental.Result.Updated, incremental.Result.Failed).Should().Be((200, 100, 100, 0));

        // And a connection's first import, into a database that already holds all of the above.
        var fresh = Connection("New account");
        db.Add(fresh);
        await db.SaveChangesAsync();
        var first = new StubConnector(ProviderType.ConnectWisePsa) { Paged = true, SupportsAttachmentDownload = false };
        for (var i = 0; i < 5_000; i++) first.Tickets.Add(Arriving(i, changedAt));
        var initial = await SyncAsync(fresh.Id, first);
        var costliestFirst = _counter.Costliest(4).ToList();
        (initial.Result.Created, initial.Result.Failed, initial.Result.MoreToRead).Should().Be((5_000, 0, false));
        foreach (var line in costliest) output.WriteLine("    changed tickets, costliest: " + line);
        foreach (var line in costliestFirst) output.WriteLine("    first import, costliest: " + line);
        output.WriteLine($"  sync: 200 changed or new on the big connection {incremental.Commands} q {incremental.Ms} ms ({incremental.Ms / 200.0:0.0} ms a ticket; {incremental.InMemoryMs} ms not the database)"
            + $" | a new connection's first 5,000 {initial.Commands} q {initial.Ms} ms ({initial.Ms / 5000.0:0.0} ms a ticket; {initial.InMemoryMs} ms not the database)");
        incremental.Commands.Should().BeLessThanOrEqualTo(200 * 10 + 50);
        initial.Commands.Should().BeLessThanOrEqualTo(5_000 * 10 + 50);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Postgres is null) return;
        if (Profile is not null)
        {
            output.WriteLine($"kept for profiling: database {_database}");
            return;
        }
        using var db = NewContext();
        db.Database.EnsureDeleted();
    }
}
