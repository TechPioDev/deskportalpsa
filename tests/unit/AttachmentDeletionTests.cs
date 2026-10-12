using Desk.Application.Attachments;
using Desk.Application.Mapping;
using Desk.Application.Sync;
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
/// Removing files the provider no longer has. A document withdrawn in the PSA must stop being
/// downloadable here — but absence only proves deletion when the read covered everything, so the
/// dangerous half of this feature is knowing when NOT to delete.
/// </summary>
public class AttachmentDeletionTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed record Fixture(DeskDbContext Db, StubConnector Connector, InMemoryObjectStorage Storage, TestClock Clock);

    private static async Task<Fixture> SetupAsync(bool sweeps = true)
    {
        var clock = new TestClock();
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", SyncAttachments = true,
        });
        await db.SaveChangesAsync();

        var connector = new StubConnector { SupportsAttachmentSweep = sweeps };
        connector.Tickets.Add(new UnifiedTicket
        {
            ExternalId = "7810", Title = "t", Status = "1", Priority = "1",
            RequesterExternalId = "176", RequesterName = "Acme", RequesterEmail = "a@acme.test",
        });
        return new Fixture(db, connector, new InMemoryObjectStorage(new AttachmentStorageOptions(), clock), clock);
    }

    private static ConnectionSyncRunner Runner(Fixture f) =>
        new(f.Db, new FakeResolver(f.Connector),
            new TicketSyncService(f.Db, new MappingEngine(), new SyncEventStore(f.Db, f.Clock), f.Clock, new RecordingActivity()),
            f.Storage, new HeuristicMalwareScanner(), f.Clock);

    private static (UnifiedAttachment, byte[]) File(string id, string name, DateTimeOffset at) =>
        (new UnifiedAttachment(id, name, "image/png", Png.Length) { CreatedAt = at, AuthorName = "Jane Tech" }, Png);

    [Fact]
    public async Task A_file_deleted_in_the_provider_is_removed_here_along_with_its_bytes()
    {
        var f = await SetupAsync();
        await using var _ = f.Db;
        f.Connector.Attachments["7810"] = [File("38", "kept.png", f.Clock.GetUtcNow()), File("39", "withdrawn.png", f.Clock.GetUtcNow())];
        (await Runner(f).RunAsync(Conn, full: true)).Attachments.Should().Be(2);
        var key = await f.Db.TicketAttachments.Where(a => a.OriginalFileName == "withdrawn.png")
            .Select(a => a.StorageObjectKey).SingleAsync();

        f.Connector.Attachments["7810"] = [File("38", "kept.png", f.Clock.GetUtcNow())];
        var run = await Runner(f).RunAsync(Conn, full: true);

        run.AttachmentsRemoved.Should().Be(1);
        (await f.Db.TicketAttachments.Select(a => a.OriginalFileName).ToListAsync()).Should().Equal("kept.png");
        // The bytes go too: leaving them keeps a withdrawn document reachable by an old signed URL.
        (await f.Storage.GetAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task A_full_sweep_that_stopped_early_deletes_nothing()
    {
        // The provider read part of its list and stopped - a page limit. Every file past that point
        // is missing from the answer and still exists. Autotask's sweep was exactly this, one page
        // of 500 reported as the whole tenant, and "Re-sync all" would have deleted the rest.
        var f = await SetupAsync();
        await using var _ = f.Db;
        var now = f.Clock.GetUtcNow();
        f.Connector.Attachments["7810"] = [File("38", "first.png", now), File("39", "second.png", now), File("40", "third.png", now)];
        (await Runner(f).RunAsync(Conn, full: true)).Attachments.Should().Be(3);

        f.Connector.SweepStopsAfter = 2;
        var run = await Runner(f).RunAsync(Conn, full: true);

        run.AttachmentsRemoved.Should().Be(0);
        (await f.Db.TicketAttachments.Select(a => a.OriginalFileName).ToListAsync())
            .Should().BeEquivalentTo(["first.png", "second.png", "third.png"]);
        foreach (var key in await f.Db.TicketAttachments.Select(a => a.StorageObjectKey).ToListAsync())
            (await f.Storage.GetAsync(key)).Should().NotBeNull("the stored bytes are untouched too");
    }

    [Fact]
    public async Task A_full_sweep_that_stopped_early_still_imports_what_it_read()
    {
        var f = await SetupAsync();
        await using var _ = f.Db;
        var now = f.Clock.GetUtcNow();
        f.Connector.Attachments["7810"] = [File("38", "first.png", now), File("39", "second.png", now), File("40", "third.png", now)];
        f.Connector.SweepStopsAfter = 2;

        var run = await Runner(f).RunAsync(Conn, full: true);

        run.Attachments.Should().Be(2);
        (await f.Db.TicketAttachments.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task An_incremental_sweep_never_deletes_anything()
    {
        var f = await SetupAsync();
        await using var _ = f.Db;
        f.Connector.Attachments["7810"] = [File("38", "old.png", f.Clock.GetUtcNow())];
        await Runner(f).RunAsync(Conn, full: true);

        // A dated sweep returns only RECENT files. The older file is absent simply because it is old,
        // and reconciling against that would wipe the entire back catalogue on every routine sync.
        f.Clock.Advance(TimeSpan.FromHours(1));
        f.Connector.Attachments["7810"] = [];

        var run = await Runner(f).RunAsync(Conn, full: false);

        run.AttachmentsRemoved.Should().Be(0);
        (await f.Db.TicketAttachments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_customers_own_upload_is_never_removed_by_reconciliation()
    {
        var f = await SetupAsync();
        await using var _ = f.Db;
        await Runner(f).RunAsync(Conn, full: true);
        var ticket = await f.Db.Tickets.SingleAsync();

        // Uploaded here and pushed out, so it carries a provider id — but the portal is its origin.
        f.Db.TicketAttachments.Add(new TicketAttachment
        {
            MspOrganizationId = Org, TicketId = ticket.Id, ExternalAttachmentId = "99",
            OriginalFileName = "customer-evidence.png", ContentType = "image/png", SizeBytes = Png.Length,
            StorageObjectKey = "att/x.png", ImportedFromProvider = false,
            PushedToProviderAt = f.Clock.GetUtcNow(), UploadedAt = f.Clock.GetUtcNow(),
        });
        await f.Db.SaveChangesAsync();

        // The provider reports nothing: a technician deleted their copy.
        var run = await Runner(f).RunAsync(Conn, full: true);

        // Deleting it would destroy the only copy of something the customer supplied.
        run.AttachmentsRemoved.Should().Be(0);
        (await f.Db.TicketAttachments.SingleAsync()).OriginalFileName.Should().Be("customer-evidence.png");
    }

    [Fact]
    public async Task A_ticket_whose_files_could_not_be_read_is_left_untouched()
    {
        var f = await SetupAsync(sweeps: false); // per-ticket path
        await using var _ = f.Db;
        f.Connector.Attachments["7810"] = [File("38", "kept.png", f.Clock.GetUtcNow())];
        await Runner(f).RunAsync(Conn, full: true);

        // The read now fails. An unknown list is not an empty list, so nothing may be inferred.
        f.Connector.AttachmentReadFailure = new ConnectorException(ConnectorFailureKind.RateLimited, "429");
        var run = await Runner(f).RunAsync(Conn, full: true);

        run.AttachmentsRemoved.Should().Be(0);
        (await f.Db.TicketAttachments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_per_ticket_path_reconciles_a_ticket_that_now_has_no_files_at_all()
    {
        var f = await SetupAsync(sweeps: false);
        await using var _ = f.Db;
        f.Connector.Attachments["7810"] = [File("38", "gone.png", f.Clock.GetUtcNow())];
        await Runner(f).RunAsync(Conn, full: true);

        // Emptied provider-side. The read succeeded and returned nothing, which is conclusive here.
        f.Connector.Attachments["7810"] = [];
        var run = await Runner(f).RunAsync(Conn, full: true);

        run.AttachmentsRemoved.Should().Be(1);
        (await f.Db.TicketAttachments.CountAsync()).Should().Be(0);
    }
}
