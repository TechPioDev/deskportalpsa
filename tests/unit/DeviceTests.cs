using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Sync;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.ControlPanel;
using Desk.Application.Mapping;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Devices: each client's list comes in from the PSA daily, adopting what was already listed rather
/// than duplicating it and retiring - never deleting - what the PSA stops listing; tickets point at
/// their device whichever arrived first; and the client's administrator sees each device with the
/// tickets raised about it, never the team's internal work.
/// </summary>
public class DeviceTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Globex = Guid.NewGuid();
    private static readonly Guid Stub0 = Guid.NewGuid();

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid connectionId, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed record Kit(AdminHarness H, StubConnector Psa, DeviceSyncService Sync);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://at.test", CredentialSecretRef = "ref",
        });
        h.Db.ClientCompanies.AddRange(
            new ClientCompany { Id = Acme, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "176" },
            new ClientCompany { Id = Globex, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Globex", ExternalCompanyId = "177" },
            new ClientCompany { Id = Stub0, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Company 0", ExternalCompanyId = "0" });
        await h.Db.SaveChangesAsync();
        var psa = new StubConnector();
        return new Kit(h, psa, new DeviceSyncService(h.Db, new Resolver(psa), h.Clock, NullLogger<DeviceSyncService>.Instance));
    }

    private static ExternalDevice Dev(string id, string name, string? serial = null, bool active = true, string? type = "Workstation")
        => new(id, name, type, serial, active, DateTimeOffset.Parse("2027-03-31T00:00:00Z"));

    [Fact]
    public async Task The_sync_brings_devices_in_and_adopts_one_already_listed_by_hand()
    {
        var k = await BuildAsync();
        // Listed by hand before the sync existed, with the client's own note.
        k.H.Db.Devices.Add(new Device { MspOrganizationId = Org, ClientCompanyId = Acme, Name = "Reception PC", Identifier = "5CG1234", Notes = "Front desk" });
        await k.H.Db.SaveChangesAsync();
        k.Psa.Devices["176"] = [Dev("70", "ACME-SRV01", "7XK29", type: "Server"), Dev("71", "ACME-RECEPTION", "5CG1234")];

        var result = await k.Sync.SyncConnectionAsync(Conn);

        result.Should().BeEquivalentTo(new { Created = 1, Updated = 1, Retired = 0, CompaniesFailed = 0 });
        var devices = await k.H.Db.Devices.Where(d => d.ClientCompanyId == Acme).OrderBy(d => d.Name).ToListAsync();
        devices.Should().HaveCount(2);
        // Matched by serial: renamed to the PSA's name, and the note the client wrote is kept.
        devices[0].Should().BeEquivalentTo(new { Name = "ACME-RECEPTION", ExternalId = "71", Notes = "Front desk", FromPsa = true });
        devices[1].Should().BeEquivalentTo(new { Name = "ACME-SRV01", Type = "Server", Identifier = "7XK29", ExternalId = "70",
            WarrantyExpiresAt = (DateTimeOffset?)DateTimeOffset.Parse("2027-03-31T00:00:00Z") });
    }

    [Fact]
    public async Task A_device_the_psa_stops_listing_is_retired_not_deleted()
    {
        var k = await BuildAsync();
        k.Psa.Devices["176"] = [Dev("70", "ACME-SRV01"), Dev("71", "OLD-LAPTOP")];
        await k.Sync.SyncConnectionAsync(Conn);

        k.Psa.Devices["176"] = [Dev("70", "ACME-SRV01")];
        var second = await k.Sync.SyncConnectionAsync(Conn);

        second.Retired.Should().Be(1);
        var old = await k.H.Db.Devices.SingleAsync(d => d.ExternalId == "71");
        old.IsActive.Should().BeFalse();
        // Running again changes nothing further.
        (await k.Sync.SyncConnectionAsync(Conn)).Should().BeEquivalentTo(new { Created = 0, Updated = 1, Retired = 0 });
    }

    [Fact]
    public async Task One_client_failing_does_not_stop_the_others_and_company_zero_is_not_asked()
    {
        var k = await BuildAsync();
        k.Psa.DeviceReadFailsFor.Add("176");
        k.Psa.Devices["177"] = [Dev("80", "GLOBEX-FW")];
        k.Psa.DeviceReadFailsFor.Add("0"); // would be counted as a failure if it were asked

        var result = await k.Sync.SyncConnectionAsync(Conn);

        result.Should().BeEquivalentTo(new { Created = 1, CompaniesFailed = 1 });
        (await k.H.Db.Devices.SingleAsync()).ClientCompanyId.Should().Be(Globex);
    }

    [Fact]
    public async Task A_ticket_that_arrived_before_its_device_is_linked_when_the_device_does()
    {
        var k = await BuildAsync();
        var sync = new TicketSyncService(k.H.Db, new MappingEngine(), new SyncEventStore(k.H.Db, k.H.Clock), k.H.Clock, new RecordingActivity());
        await sync.UpsertFromProviderAsync(Conn, Incoming("9001", device: "70"), []);
        var ticket = await k.H.Db.Tickets.SingleAsync();
        ticket.Should().BeEquivalentTo(new { DeviceExternalId = "70", DeviceId = (Guid?)null });

        k.Psa.Devices["176"] = [Dev("70", "ACME-SRV01")];
        var result = await k.Sync.SyncConnectionAsync(Conn);

        result.TicketsLinked.Should().Be(1);
        var device = await k.H.Db.Devices.SingleAsync();
        (await k.H.Db.Tickets.SingleAsync()).DeviceId.Should().Be(device.Id);

        // And the other way round: a ticket arriving after its device is linked straight away.
        await sync.UpsertFromProviderAsync(Conn, Incoming("9002", device: "70"), []);
        (await k.H.Db.Tickets.SingleAsync(t => t.ExternalTicketId == "9002")).DeviceId.Should().Be(device.Id);
    }

    [Fact]
    public async Task The_device_is_cleared_only_by_a_provider_that_actually_says_so()
    {
        var k = await BuildAsync();
        var sync = new TicketSyncService(k.H.Db, new MappingEngine(), new SyncEventStore(k.H.Db, k.H.Clock), k.H.Clock, new RecordingActivity());
        await sync.UpsertFromProviderAsync(Conn, Incoming("9001", device: "70"), []);

        // A provider that does not carry the device says nothing about it: the device stays.
        await sync.UpsertFromProviderAsync(Conn, Incoming("9001", device: null, known: false, title: "Disk failing (update)"), []);
        (await k.H.Db.Tickets.SingleAsync()).DeviceExternalId.Should().Be("70");

        // Autotask, which does carry it, saying "none" clears it.
        await sync.UpsertFromProviderAsync(Conn, Incoming("9001", device: null, known: true, title: "Disk failing (again)"), []);
        (await k.H.Db.Tickets.SingleAsync()).DeviceExternalId.Should().BeNull();
    }

    [Fact]
    public void A_ticket_with_no_device_keeps_the_hash_it_had_before_devices_existed()
    {
        // Were the device always hashed, every imported ticket would look changed at once and the next
        // sync would rewrite the whole import for nothing.
        var before = UpdateHasher.ForTicketState("NEW", "HIGH", null, "Disk", null, null, null, null, null, "Level I", "Priya", "p@acme.test");
        UpdateHasher.ForTicketState("NEW", "HIGH", null, "Disk", null, null, null, null, null, "Level I", "Priya", "p@acme.test", deviceExternalId: null)
            .Should().Be(before);
        UpdateHasher.ForTicketState("NEW", "HIGH", null, "Disk", null, null, null, null, null, "Level I", "Priya", "p@acme.test", deviceExternalId: "70")
            .Should().NotBe(before);
    }

    [Fact]
    public async Task The_client_sees_each_device_with_its_tickets_and_never_the_teams_internal_work()
    {
        var k = await BuildAsync();
        k.Psa.Devices["176"] = [Dev("70", "ACME-SRV01", "7XK29", type: "Server")];
        await k.Sync.SyncConnectionAsync(Conn);
        var server = await k.H.Db.Devices.SingleAsync();
        var board = new Board { MspOrganizationId = Org, Name = "Internal IT", Key = "INT" };
        k.H.Db.Boards.Add(board);
        k.H.Db.Tickets.AddRange(
            Psa("9001", "Disk failing", server.Id, closed: false),
            Psa("9002", "RAID rebuild", server.Id, closed: true),
            new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "INT-000009", ClientCompanyId = Acme,
                RequesterName = "Anika", RequesterEmail = "a@techpio.test", Title = "Our own note about their server", PortalStatus = "NEW",
                DeviceId = server.Id,
            });
        await k.H.Db.SaveChangesAsync();
        var svc = new AccountSettingsService(k.H.Db, new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock), new Resolver(k.Psa), k.Sync);
        var admin = new ClientAccess(Org, Acme, Guid.NewGuid(), IsCompanyAdministrator: true);

        var list = await svc.ListDevicesAsync(admin);
        list.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "ACME-SRV01", FromPsa = true, OpenTickets = 1, TotalTickets = 2 });

        var detail = await svc.GetDeviceAsync(admin, server.Id);
        detail.Tickets.Select(t => t.Title).Should().BeEquivalentTo("Disk failing", "RAID rebuild");

        // Another company's administrator is not told the device exists.
        await Assert.ThrowsAsync<NotFoundException>(() => svc.GetDeviceAsync(admin with { ClientCompanyId = Globex }, server.Id));
    }

    [Fact]
    public async Task A_synced_devices_details_belong_to_the_psa_and_only_its_notes_can_change()
    {
        var k = await BuildAsync();
        k.Psa.Devices["176"] = [Dev("70", "ACME-SRV01", "7XK29")];
        await k.Sync.SyncConnectionAsync(Conn);
        var server = await k.H.Db.Devices.SingleAsync();
        var svc = new AccountSettingsService(k.H.Db, new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock), new Resolver(k.Psa), k.Sync);
        var admin = new ClientAccess(Org, Acme, Guid.NewGuid(), IsCompanyAdministrator: true);

        await svc.SaveDeviceAsync(admin, new DeviceInput(server.Id, "Renamed", "Laptop", "XXX", "In the comms room"));

        (await k.H.Db.Devices.SingleAsync()).Should().BeEquivalentTo(new { Name = "ACME-SRV01", Identifier = "7XK29", Notes = "In the comms room" });
        var refused = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.DeleteDeviceAsync(admin, server.Id));
        refused.Message.Should().Contain("comes from your PSA");
    }

    private static UnifiedTicket Incoming(string id, string? device, bool known = true, string title = "Disk failing") => new()
    {
        ExternalId = id, Title = title, Status = "New", RequesterExternalId = "176",
        DeviceExternalId = device, DeviceKnown = known,
    };

    private static Ticket Psa(string id, string title, Guid device, bool closed) => new()
    {
        MspOrganizationId = Org, Origin = TicketOrigin.Psa, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
        ExternalTicketId = id, ClientCompanyId = Acme, RequesterName = "Priya", RequesterEmail = "p@acme.test",
        Title = title, PortalStatus = closed ? "CLOSED" : "IN_PROGRESS", DeviceId = device,
        ClosedAt = closed ? DateTimeOffset.Parse("2026-09-01T10:00:00Z") : null,
    };
}
