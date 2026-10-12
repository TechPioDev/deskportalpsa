using Desk.Application.Attachments;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
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
/// A connection's import limits, re-applied to what the provider returns so a provider that ignores
/// a filter cannot widen the scope.
///
/// The queue limit holds the provider's queue or board IDS - that is what the provider is asked
/// for. The ticket carried only the queue's NAME, and the re-check compared the two: nothing ever
/// matched, so limiting a connection to any queue imported no tickets at all.
/// </summary>
public class ImportFilterTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static UnifiedTicket Ticket(string id, string? queueId, string? queueName, string? technician = null, string company = "co-1") => new()
    {
        ExternalId = id, Title = "Ticket " + id, Status = "New", Priority = "Medium",
        QueueOrBoardId = queueId, QueueOrBoard = queueName, AssignedTechnicianExternalId = technician,
        RequesterExternalId = company, CompanyName = "Acme",
    };

    private static async Task<List<string>> ImportedAsync(Action<PsaConnection> limit, params UnifiedTicket[] offered)
    {
        var clock = new TestClock();
        await using var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        var connection = new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", SyncAttachments = false, ImportNotes = false,
        };
        limit(connection);
        db.PsaConnections.Add(connection);
        await db.SaveChangesAsync();

        var connector = new StubConnector();
        connector.Tickets.AddRange(offered);
        var runner = new ConnectionSyncRunner(db, new FakeResolver(connector),
            new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, clock), clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), clock), new HeuristicMalwareScanner(), clock);
        await runner.RunAsync(Conn);

        return await db.Tickets.AsNoTracking().OrderBy(t => t.ExternalTicketId).Select(t => t.ExternalTicketId!).ToListAsync();
    }

    [Fact]
    public async Task A_connection_limited_to_a_queue_imports_that_queues_tickets()
    {
        var imported = await ImportedAsync(c => c.FilterQueueIds = "8",
            Ticket("1", "8", "Service Desk"),
            Ticket("2", "9", "Projects"),
            Ticket("3", null, null));

        imported.Should().Equal("1");
    }

    [Fact]
    public async Task Several_queues_can_be_named()
    {
        var imported = await ImportedAsync(c => c.FilterQueueIds = "8, 11",
            Ticket("1", "8", "Service Desk"),
            Ticket("2", "9", "Projects"),
            Ticket("3", "11", "Escalations"));

        imported.Should().Equal("1", "3");
    }

    [Fact]
    public async Task A_queue_is_matched_by_its_id_not_by_a_name_that_looks_like_one()
    {
        // A board called "8" is not queue 8.
        var imported = await ImportedAsync(c => c.FilterQueueIds = "8",
            Ticket("1", "12", "8"));

        imported.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_that_reports_only_a_queue_name_can_still_be_limited_by_it()
    {
        var imported = await ImportedAsync(c => c.FilterQueueIds = "Service Desk",
            Ticket("1", null, "Service Desk"),
            Ticket("2", null, "Projects"));

        imported.Should().Equal("1");
    }

    [Fact]
    public async Task With_no_queue_limit_every_queue_is_imported()
    {
        var imported = await ImportedAsync(_ => { },
            Ticket("1", "8", "Service Desk"),
            Ticket("2", null, null));

        imported.Should().Equal("1", "2");
    }

    [Fact]
    public async Task The_technician_and_company_limits_still_hold_beside_a_queue_limit()
    {
        var imported = await ImportedAsync(c => { c.FilterQueueIds = "8"; c.FilterResourceIds = "tech-1"; c.FilterCompanyIds = "co-1"; },
            Ticket("1", "8", "Service Desk", "tech-1"),
            Ticket("2", "8", "Service Desk", "tech-2"),
            Ticket("3", "8", "Service Desk", "tech-1", company: "co-2"),
            Ticket("4", "9", "Projects", "tech-1"));

        imported.Should().Equal("1");
    }
}
