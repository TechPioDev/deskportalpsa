using System.Text;
using Desk.Application.Attachments;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
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
/// Who attached a file, as an id - the attachment half of what notes already do. The name alone could
/// not tell the integration account (named after a real person) from that person.
/// </summary>
public class AttachmentAuthorTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private const string Account = "29682885";

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static async Task<DeskDbContext> SeedAsync(string? account = null)
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
            SyncAttachments = true, DefaultTimeEntryResourceId = account,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static ConnectionSyncRunner Runner(DeskDbContext db, StubConnector connector, TestClock clock)
        => new(db, new FakeResolver(connector),
            new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, clock), clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), clock), new HeuristicMalwareScanner(), clock);

    private static UnifiedTicket Incoming(string extId) => new()
    {
        ExternalId = extId, Title = "Printer offline", Status = "1", Priority = "1",
        RequesterExternalId = "176", RequesterName = "Acme", RequesterEmail = "a@acme.test",
    };

    private static (UnifiedAttachment, byte[]) File(string id, string name, string author, string? authorId, TestClock clock) =>
        (new UnifiedAttachment(id, name, "text/plain", 5)
            { CreatedAt = clock.GetUtcNow(), AuthorName = author, AuthorExternalId = authorId },
         Encoding.UTF8.GetBytes("hello"));

    [Fact]
    public async Task An_imported_files_author_id_is_stored_and_a_sweep_fills_it_in_on_files_already_held()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7814"));
        connector.Attachments["7814"] = [File("a1", "screenshot.txt", "Sudanshu Aggarwal", Account, clock)];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var stored = await db.TicketAttachments.SingleAsync();
        stored.AuthorExternalId.Should().Be(Account);

        // As it stood before the id was kept.
        stored.AuthorExternalId = null;
        await db.SaveChangesAsync();

        var (added, removed) = await Runner(db, connector, clock).RefreshAttachmentsAsync(Conn);

        (added, removed).Should().Be((0, 0), "the file is already held - only its author id is filled in");
        (await db.TicketAttachments.AsNoTracking().SingleAsync()).AuthorExternalId.Should().Be(Account);
    }

    [Fact]
    public async Task A_portal_upload_is_never_stamped_with_the_providers_author()
    {
        // The provider's copy of a portal upload is stamped with whatever login pushed it. The
        // portal's row is the record of who actually uploaded it, and must not take that stamp.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7814"));
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var ticket = await db.Tickets.SingleAsync();
        db.TicketAttachments.Add(new TicketAttachment
        {
            MspOrganizationId = Org, TicketId = ticket.Id, ExternalAttachmentId = "a1",
            OriginalFileName = "invoice.txt", ContentType = "text/plain", SizeBytes = 5, StorageObjectKey = "k",
            ImportedFromProvider = false, UploadedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        connector.Attachments["7814"] = [File("a1", "invoice.txt", "TechPio Support", "29682887", clock)];

        await Runner(db, connector, clock).RefreshAttachmentsAsync(Conn);

        var row = await db.TicketAttachments.AsNoTracking().SingleAsync();
        row.AuthorExternalId.Should().BeNull();
        row.ImportedFromProvider.Should().BeFalse("and it is still the portal's row, not a second copy");
    }

    [Fact]
    public async Task A_provider_without_a_sweep_is_left_alone()
    {
        // ConnectWise reads files per ticket and names a document's owner only by login, so there is
        // no author id to fill and no reason to spend a read on it.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var connector = new StubConnector { SupportsAttachmentSweep = false };
        connector.Tickets.Add(Incoming("7814"));
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var sweepsBefore = connector.AttachmentSweeps;

        var result = await Runner(db, connector, clock).RefreshAttachmentsAsync(Conn);

        result.Should().Be((0, 0));
        connector.AttachmentSweeps.Should().Be(sweepsBefore);
    }

    [Fact]
    public async Task A_file_the_integration_attached_says_so_and_a_person_of_the_same_name_keeps_theirs()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(account: Account);
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7814"));
        connector.Attachments["7814"] =
        [
            File("a1", "by-the-integration.txt", "Sudanshu Aggarwal", Account, clock),
            // Same NAME, another resource: a real person - the reason this is decided on ids.
            File("a2", "by-the-person.txt", "Sudanshu Aggarwal", "29682999", clock),
        ];
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var ticket = await db.Tickets.SingleAsync();
        var company = await db.ClientCompanies.SingleAsync();
        var reads = new Desk.Infrastructure.Tickets.TicketReadService(
            db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));
        var staff = (await reads.GetDetailForStaffAsync(ticket.Id))!.Attachments;
        var client = (await reads.GetDetailAsync(
            new ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true), ticket.Id))!.Attachments;

        foreach (var files in new[] { staff, client })
        {
            files.Single(a => a.FileName == "by-the-integration.txt").AuthorName.Should().Be("AT integration");
            files.Single(a => a.FileName == "by-the-person.txt").AuthorName.Should().Be("Sudanshu Aggarwal");
        }
    }

    [Fact]
    public async Task The_backfill_picks_only_imported_files_that_should_have_an_author_id()
    {
        await using var db = await SeedAsync();
        var cw = Guid.NewGuid();
        db.PsaConnections.Add(new PsaConnection
        {
            Id = cw, MspOrganizationId = Org, Name = "CW", Provider = ProviderType.ConnectWisePsa,
            ApiEndpoint = "https://y", CredentialSecretRef = "mem://y", SyncAttachments = true,
        });
        Ticket T(Guid connection, string ext) => new()
        {
            MspOrganizationId = Org, PsaConnectionId = connection, Provider = ProviderType.AutotaskPsa,
            ExternalTicketId = ext, ClientCompanyId = Guid.NewGuid(), RequesterName = "r", RequesterEmail = "r@a.test",
            Title = ext, PortalStatus = "NEW", PortalPriority = "NORMAL",
        };
        var at = T(Conn, "1");
        var onCw = T(cw, "2");
        db.Tickets.AddRange(at, onCw);
        TicketAttachment F(Ticket t, string? author, bool imported = true, string? authorId = null) => new()
        {
            MspOrganizationId = Org, TicketId = t.Id, ExternalAttachmentId = Guid.NewGuid().ToString("N"),
            OriginalFileName = "f.txt", ContentType = "text/plain", StorageObjectKey = "k",
            AuthorName = author, ImportedFromProvider = imported, AuthorExternalId = authorId,
        };
        db.TicketAttachments.AddRange(
            F(at, "Sudanshu Aggarwal"),                   // Autotask, imported, no id: needs it
            F(at, null, imported: false),                 // a portal upload: our own record
            F(at, "AutotaskPsa automation"),              // provider-generated: no author to find
            F(at, "Kamal Arora", authorId: "29682889"),   // already has one
            F(onCw, "Sarabjit"));                          // ConnectWise names owners by login only
        await db.SaveChangesAsync();

        (await AuthorBackfill.AttachmentsCountAsync(db)).Should().Be(1);
        (await AuthorBackfill.AttachmentConnectionsPendingAsync(db)).Should().Equal(Conn);
    }
}
