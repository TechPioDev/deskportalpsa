using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Choosing the device a ticket is about: a technician on the ticket page, a client raising one, a
/// monitoring alert naming one. A PSA ticket's device is one the PSA knows, and the PSA is told first.
/// </summary>
public class DevicePickerTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Globex = Guid.NewGuid();

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid connectionId, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed record Kit(AdminHarness H, StubConnector Psa, TicketDeviceService Svc,
        Device Server, Device Laptop, Device HandAdded, Device Retired, Device GlobexFirewall);

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
            new ClientCompany { Id = Globex, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Globex", ExternalCompanyId = "177" });
        Device Psa(string name, string ext, Guid company, bool active = true) => new()
        { MspOrganizationId = Org, ClientCompanyId = company, Name = name, PsaConnectionId = Conn, ExternalId = ext, IsActive = active };
        var server = Psa("ACME-SRV01", "70", Acme);
        var laptop = Psa("ACME-LT-07", "71", Acme);
        var retired = Psa("OLD-LAPTOP", "72", Acme, active: false);
        var firewall = Psa("GLOBEX-FW", "80", Globex);
        var handAdded = new Device { MspOrganizationId = Org, ClientCompanyId = Acme, Name = "Reception printer" };
        h.Db.Devices.AddRange(server, laptop, retired, firewall, handAdded);
        await h.Db.SaveChangesAsync();
        var psa = new StubConnector();
        return new Kit(h, psa, new TicketDeviceService(h.Db, new NoopTicketScopeQuery(), new Resolver(psa)),
            server, laptop, handAdded, retired, firewall);
    }

    private static async Task<Ticket> PsaTicketAsync(Kit k, Guid? device = null, string? deviceExternal = null)
    {
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
            ExternalTicketId = "9001", ClientCompanyId = Acme, RequesterName = "Priya", RequesterEmail = "p@acme.test",
            Title = "Disk failing", PortalStatus = "IN_PROGRESS", DeviceId = device, DeviceExternalId = deviceExternal,
        };
        k.H.Db.Tickets.Add(t);
        await k.H.Db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task A_technician_moves_a_psa_ticket_to_another_device_and_the_psa_is_told_first()
    {
        var k = await BuildAsync();
        var t = await PsaTicketAsync(k, k.Server.Id, "70");

        var set = await k.Svc.SetAsync(Guid.NewGuid(), t.Id, k.Laptop.Id);

        set!.Name.Should().Be("ACME-LT-07");
        // The replaced device travels with the change, so a PSA that allows several removes only it.
        k.Psa.DeviceChanges.Should().Equal(("9001", "71", "70"));
        (await k.H.Db.Tickets.SingleAsync()).Should().BeEquivalentTo(new { DeviceId = (Guid?)k.Laptop.Id, DeviceExternalId = "71" });

        await k.Svc.SetAsync(Guid.NewGuid(), t.Id, null);
        k.Psa.DeviceChanges.Last().Should().Be(("9001", null, "71"));
        (await k.H.Db.Tickets.SingleAsync()).DeviceId.Should().BeNull();
    }

    [Fact]
    public async Task A_psa_refusing_the_change_leaves_the_ticket_as_it_was()
    {
        var k = await BuildAsync();
        var t = await PsaTicketAsync(k, k.Server.Id, "70");
        k.Psa.DeviceChangeFailure = new ConnectorException(ConnectorFailureKind.InvalidRequest, "configuration item is inactive");

        var refused = await Assert.ThrowsAsync<ValidationFailedException>(() => k.Svc.SetAsync(Guid.NewGuid(), t.Id, k.Laptop.Id));

        refused.Message.Should().Contain("configuration item is inactive");
        (await k.H.Db.Tickets.SingleAsync()).DeviceId.Should().Be(k.Server.Id);
    }

    [Fact]
    public async Task A_psa_ticket_is_only_offered_devices_the_psa_knows_and_says_why_not_the_rest()
    {
        var k = await BuildAsync();
        var t = await PsaTicketAsync(k);

        var choices = await k.Svc.ChoicesAsync(Guid.NewGuid(), t.Id);

        choices.Select(c => c.Name).Should().NotContain("GLOBEX-FW");
        choices.Single(c => c.Name == "ACME-SRV01").Unavailable.Should().BeNull();
        choices.Single(c => c.Name == "Reception printer").Unavailable.Should().Contain("added by hand");
        choices.Single(c => c.Name == "OLD-LAPTOP").Unavailable.Should().Contain("retired");

        await Assert.ThrowsAsync<ValidationFailedException>(() => k.Svc.SetAsync(Guid.NewGuid(), t.Id, k.HandAdded.Id));
        await Assert.ThrowsAsync<ValidationFailedException>(() => k.Svc.SetAsync(Guid.NewGuid(), t.Id, k.Retired.Id));
        // Another company's device: not found, as if it did not exist.
        await Assert.ThrowsAsync<NotFoundException>(() => k.Svc.SetAsync(Guid.NewGuid(), t.Id, k.GlobexFirewall.Id));
        k.Psa.DeviceChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_board_ticket_can_use_a_hand_added_device_and_nothing_is_sent_anywhere()
    {
        var k = await BuildAsync();
        var board = new Board { MspOrganizationId = Org, Name = "Internal IT", Key = "INT" };
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "INT-000001", ClientCompanyId = Acme,
            RequesterName = "Anika", RequesterEmail = "a@techpio.test", Title = "Replace the reception printer toner", PortalStatus = "NEW",
        };
        k.H.Db.Boards.Add(board);
        k.H.Db.Tickets.Add(t);
        await k.H.Db.SaveChangesAsync();

        await k.Svc.SetAsync(Guid.NewGuid(), t.Id, k.HandAdded.Id);

        (await k.H.Db.Tickets.SingleAsync()).Should().BeEquivalentTo(new { DeviceId = (Guid?)k.HandAdded.Id, DeviceExternalId = (string?)null });
        k.Psa.DeviceChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_names_a_device_when_raising_a_ticket_from_their_own_list()
    {
        var k = await BuildAsync();
        var user = new ClientUser { MspOrganizationId = Org, ClientCompanyId = Acme, Email = "priya@acme.test", DisplayName = "Priya" };
        k.H.Db.ClientUsers.Add(user);
        await k.H.Db.SaveChangesAsync();
        var access = new ClientAccess(Org, Acme, user.Id, IsCompanyAdministrator: false);

        // An ordinary user gets the picker - only in-use devices the PSA knows, and only names and types.
        (await k.Svc.ClientChoicesAsync(access)).Select(c => c.Name).Should().Equal("ACME-LT-07", "ACME-SRV01");

        var create = new TicketCommandService(k.H.Db, new Resolver(k.Psa), new MappingEngine(), new SyncEventStore(k.H.Db, k.H.Clock),
            new NoopTicketScopeQuery(), k.H.Clock, new RecordingActivity());
        await create.CreateAsync(access, new CreateTicketInput("Laptop will not boot", null, null, null, null, k.Laptop.Id));

        k.Psa.CreateRequests.Single().DeviceExternalId.Should().Be("71");
        (await k.H.Db.Tickets.SingleAsync()).Should().BeEquivalentTo(new { DeviceId = (Guid?)k.Laptop.Id, DeviceExternalId = "71" });

        // Another company's device, or one the PSA does not know, is refused before anything is sent.
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            create.CreateAsync(access, new CreateTicketInput("Firewall down", null, null, null, null, k.GlobexFirewall.Id)));
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            create.CreateAsync(access, new CreateTicketInput("Printer jam", null, null, null, null, k.HandAdded.Id)));
        k.Psa.CreateRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_sync_keeps_the_portals_device_while_the_psa_still_lists_it_among_several()
    {
        var k = await BuildAsync();
        await PsaTicketAsync(k, k.Laptop.Id, "71");
        var sync = new TicketSyncService(k.H.Db, new MappingEngine(), new SyncEventStore(k.H.Db, k.H.Clock), k.H.Clock, new RecordingActivity());

        // ConnectWise lists the server first, the portal's laptop second: the laptop stays.
        await sync.UpsertFromProviderAsync(Conn, new UnifiedTicket
        {
            ExternalId = "9001", Title = "Disk failing", Status = "In Progress", RequesterExternalId = "176",
            DeviceKnown = true, DeviceExternalId = "70", DeviceExternalIds = ["70", "71"],
        }, []);
        (await k.H.Db.Tickets.SingleAsync()).DeviceId.Should().Be(k.Laptop.Id);

        // Once the laptop is taken off in the PSA, the ticket follows the PSA.
        await sync.UpsertFromProviderAsync(Conn, new UnifiedTicket
        {
            ExternalId = "9001", Title = "Disk failing", Status = "In Progress", RequesterExternalId = "176",
            DeviceKnown = true, DeviceExternalId = "70", DeviceExternalIds = ["70"],
        }, []);
        (await k.H.Db.Tickets.SingleAsync()).DeviceId.Should().Be(k.Server.Id);
    }

    [Fact]
    public async Task An_alert_lands_in_the_history_of_the_device_it_names_and_only_that_clients()
    {
        var k = await BuildAsync();
        var audit = new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock);
        var boards = new BoardService(k.H.Db, k.H.Tenant, audit);
        var sources = new AlertSourceService(k.H.Db, k.H.Tenant, k.H.User, audit);
        var intake = new AlertIntakeService(k.H.Db, k.H.Tenant, k.H.Clock, new RecordingActivity(), NullLogger<AlertIntakeService>.Instance);
        var board = await boards.CreateAsync(new BoardInput("Monitoring", "RMM", null, BoardKind.Rmm));
        var source = await sources.CreateAsync(new AlertSourceInput("NinjaOne", board.Id, AlertVendor.NinjaOne, true));

        await intake.ReceiveAsync(source.Key, new AlertMessage("a1", "Disk C: 95% full", Device: "acme-srv01", Client: "Acme"));
        // Globex has no ACME-SRV01; the name alone must not reach into Acme's list.
        await intake.ReceiveAsync(source.Key, new AlertMessage("a2", "Disk C: 95% full", Device: "ACME-SRV01", Client: "Globex"));
        await intake.ReceiveAsync(source.Key, new AlertMessage("a3", "Unknown host down", Device: "ACME-SRV01"));

        var tickets = await k.H.Db.Tickets.OrderBy(t => t.SourceAlertId).ToListAsync();
        tickets.Select(t => t.DeviceId).Should().Equal(k.Server.Id, null, null);
    }
}
