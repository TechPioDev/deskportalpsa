using System.Reflection;
using System.Text.Json;
using Desk.Api.Controllers;
using Desk.Application.Admin;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The account the portal writes as - the connection's time-entry resource - on a ticket's own page.
///
/// Techpio's is Autotask resource 29682885, named "Sudanshu Aggarwal". The PSA shows tickets as held by
/// it and files the portal team's hours under it, so the detail page named it as the assignee, as the
/// technician on every portal-logged hour, and offered it as someone to assign work to.
/// </summary>
public class IntegrationAccountDetailTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private const string Account = "29682885";

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    /// <summary>Answers field discovery and nothing else; any other call fails loudly.</summary>
    public class FieldsOnlyAdmin : DispatchProxy
    {
        private ConnectionFieldsDto _fields = null!;

        public static IConnectionAdminService With(ConnectionFieldsDto fields)
        {
            var proxy = Create<IConnectionAdminService, FieldsOnlyAdmin>();
            ((FieldsOnlyAdmin)(object)proxy)._fields = fields;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(IConnectionAdminService.GetFieldsAsync)
                ? Task.FromResult(_fields)
                : throw new NotSupportedException(method?.Name);
    }

    private static async Task<(AdminHarness H, Ticket Ticket, AppUser Basit)> SeedAsync(string? psaAssignee, string? psaAssigneeName)
    {
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", DefaultTimeEntryResourceId = Account,
        });
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" };
        h.Db.ClientCompanies.Add(company);
        var basit = new AppUser { MspOrganizationId = Org, Email = "basit@techpio.com", DisplayName = "Basit Lone" };
        h.Db.AppUsers.Add(basit);
        var ticket = new Ticket
        {
            MspOrganizationId = Org, ClientCompanyId = company.Id, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
            Title = "t", RequesterName = "r", RequesterEmail = "r@a.test", ExternalTicketId = "7814",
            PortalStatus = "NEW", PortalPriority = "NORMAL",
            AssignedTechnicianExternalId = psaAssignee, AssignedTechnicianName = psaAssigneeName,
        };
        h.Db.Tickets.Add(ticket);
        await h.Db.SaveChangesAsync();
        return (h, ticket, basit);
    }

    private static TestCurrentUser Staff(params string[] permissions) =>
        new(Org, permissions: new HashSet<string>(permissions), userId: Guid.NewGuid());

    [Fact]
    public async Task A_ticket_the_account_holds_shows_no_PSA_assignee_to_staff_or_to_the_client()
    {
        var (h, ticket, _) = await SeedAsync(Account, "Sudanshu Aggarwal");
        var reads = new TicketReadService(h.Db, new NoopTicketScopeQuery(), Staff());

        var staff = await reads.GetDetailForStaffAsync(ticket.Id);
        var client = await reads.GetDetailAsync(
            new ClientAccess(Org, ticket.ClientCompanyId, Guid.NewGuid(), IsCompanyAdministrator: true), ticket.Id);

        foreach (var detail in new[] { staff!, client! })
        {
            detail.AssignedTechnicianName.Should().BeNull("the account is not a person holding the ticket");
            detail.AssignedTechnicianExternalId.Should().BeNull("or the reassign panel would pre-select it");
        }
    }

    [Fact]
    public async Task A_real_PSA_assignee_is_still_shown()
    {
        // The mirror of the fix: hiding every PSA assignee would pass the test above too.
        var (h, ticket, _) = await SeedAsync("29682889", "Kamal Arora");

        var detail = await new TicketReadService(h.Db, new NoopTicketScopeQuery(), Staff()).GetDetailForStaffAsync(ticket.Id);

        detail!.AssignedTechnicianName.Should().Be("Kamal Arora");
        detail.AssignedTechnicianExternalId.Should().Be("29682889");
    }

    [Fact]
    public async Task The_time_panel_names_who_logged_the_hour_and_never_the_account()
    {
        var (h, ticket, basit) = await SeedAsync(null, null);
        var at = h.Clock.GetUtcNow();
        h.Db.TicketTimeEntries.Add(new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, ExternalEntryId = "te-portal", Hours = 1m, Billable = true,
            AppUserId = basit.Id, TechnicianExternalId = Account, Source = TimeEntrySource.Portal,
            SyncStatus = TimeEntrySyncStatus.Synced, EntryDate = at,
        });
        await h.Db.SaveChangesAsync();
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.TimeEntries["7814"] =
        [
            // Logged here by Basit; the PSA files it under the account.
            new("te-portal", Account, 1m, true, at, "logged in the portal") { TechnicianName = "Sudanshu Aggarwal" },
            // Under the account with no portal row: nobody we can name.
            new("te-integration", Account, 2m, true, at, "written by the integration") { TechnicianName = "Sudanshu Aggarwal" },
            // A real resource keeps the provider's name.
            new("te-kamal", "29682889", 0.5m, true, at, "worked in Autotask") { TechnicianName = "Kamal Arora" },
        ];
        var controller = new TicketTimeController(
            h.Db, new FakeResolver(connector), null!, new NoopTicketScopeQuery(), Staff(Permissions.TicketsLogTime));

        var ok = (OkObjectResult)await controller.List(ticket.Id, default);
        var rows = ((IEnumerable<TicketTimeController.TimeRow>)ok.Value!).ToDictionary(r => r.ExternalId);

        rows["te-portal"].TechnicianName.Should().Be("Basit Lone");
        rows["te-portal"].Technician.Should().BeNull();
        rows["te-integration"].TechnicianName.Should().BeNull();
        rows["te-integration"].Technician.Should().BeNull();
        rows["te-kamal"].TechnicianName.Should().Be("Kamal Arora");
        rows["te-kamal"].Technician.Should().Be("29682889");
        rows.Values.Should().NotContain(r => r.TechnicianName == "Sudanshu Aggarwal");
    }

    [Fact]
    public async Task The_account_is_not_offered_as_a_technician_to_assign()
    {
        var (h, ticket, _) = await SeedAsync(Account, "Sudanshu Aggarwal");
        var fields = new ConnectionFieldsDto([], [], [], [], [], [],
            [new FieldOptionDto(Account, "Sudanshu Aggarwal", Account), new FieldOptionDto("29682889", "Kamal Arora", "29682889")],
            []);
        // Mapping and audit are untouched on this path: the ticket has no queue to map.
        var controller = new TicketAssignmentController(
            h.Db, new FakeResolver(new StubConnector()), FieldsOnlyAdmin.With(fields), null!, null!,
            new NoopTicketScopeQuery(), Staff(Permissions.TicketsUpdate));

        var ok = (OkObjectResult)await controller.Assignees(ticket.Id, default);
        var offered = JsonSerializer.SerializeToElement(ok.Value).GetProperty("technicians").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()).ToList();

        offered.Should().Equal("29682889");
    }
}
