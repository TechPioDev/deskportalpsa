using Desk.Application.Analytics;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Who worked each ticket, on the staff list - the data behind the Technician filter, and the far
/// end of every name link in Client workload's People list. What is under test is mostly that the
/// two ends agree: a name that was counted must open the tickets it was counted from.
/// </summary>
public class TicketListPeopleTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly DateTimeOffset Jun1 = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static Ticket T(string title, string? tech = null, string? techName = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
        ExternalTicketId = title, ClientCompanyId = Acme, RequesterName = "r", RequesterEmail = "r@a.test",
        Title = title, PortalStatus = "NEW", PortalPriority = "NORMAL", PsaCreatedAt = Jun1,
        AssignedTechnicianExternalId = tech, AssignedTechnicianName = techName,
    };

    private static TicketTimeEntry Entry(Guid ticket, decimal hours, Guid? appUser = null, string? psaTech = null) => new()
    {
        MspOrganizationId = Org, TicketId = ticket, Hours = hours, Billable = true,
        AppUserId = appUser, TechnicianExternalId = psaTech, EntryDate = Jun1,
    };

    private static async Task<DeskDbContext> SeedAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        db.ClientCompanies.Add(new ClientCompany
        { Id = Acme, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" });
        var basit = new AppUser { MspOrganizationId = Org, Email = "basit@techpio.com", DisplayName = "Basit Lone" };
        var komal = new AppUser { MspOrganizationId = Org, Email = "komal@techpio.com", DisplayName = "Komal Sharma" };
        db.AppUsers.AddRange(basit, komal);

        // Held in the portal; the PSA sees the integration account as its assignee.
        var portalHeld = T("portal-held", tech: "api-user", techName: "API User");
        portalHeld.AssignedAppUserId = basit.Id;
        // Held in the PSA as "TechX"; the same resource logs time on the next ticket as "techx".
        var psaHeld = T("psa-held", tech: "TechX", techName: "Kamal Arora");
        var loggedOnly = T("logged-only");
        db.Tickets.AddRange(portalHeld, psaHeld, loggedOnly);
        await db.SaveChangesAsync();

        db.TicketTimeEntries.AddRange(
            Entry(portalHeld.Id, 1.5m, appUser: basit.Id, psaTech: "api-user"),
            Entry(portalHeld.Id, 2.0m, appUser: komal.Id, psaTech: "api-user"),
            Entry(loggedOnly.Id, 1.0m, psaTech: "techx"));
        await db.SaveChangesAsync();
        return db;
    }

    private static TicketReadService Reads(DeskDbContext db) =>
        new(db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));

    [Fact]
    public async Task The_staff_list_names_who_holds_each_ticket_and_who_logged_time_on_it()
    {
        await using var db = await SeedAsync();

        var list = await Reads(db).ListAllAsync();

        list.Single(t => t.Title == "portal-held").People!
            .Select(p => (p.Name, p.Holds)).Should().Equal(("Basit Lone", true), ("Komal Sharma", false));
        list.Single(t => t.Title == "psa-held").People!
            .Should().ContainSingle().Which.Name.Should().Be("Kamal Arora");
        list.SelectMany(t => t.People!).Should().NotContain(p => p.Key == PersonKey.For(null, "api-user"),
            "the integration account is how a portal technician's work reaches the PSA, not a person");
    }

    [Fact]
    public async Task A_client_list_carries_no_technicians()
    {
        // Staff identities are the MSP's business. Null, not empty: empty would read as "nobody
        // worked these", and the page uses null to know there is no filter to offer.
        await using var db = await SeedAsync();

        var list = await Reads(db).ListAsync(new ClientAccess(Org, Acme, Guid.NewGuid(), IsCompanyAdministrator: true));

        list.Should().HaveCount(3).And.OnlyContain(t => t.People == null);
    }

    [Fact]
    public async Task Every_name_in_People_opens_exactly_the_tickets_it_was_counted_from()
    {
        await using var db = await SeedAsync();

        var people = (await new ClientWorkloadService(db).ForClientsAsync(new MetricsFilter())).Clients.Single().People;
        var list = await Reads(db).ListAllAsync();

        people.Select(p => p.Name).Should().BeEquivalentTo("Basit Lone", "Komal Sharma", "Kamal Arora");
        foreach (var person in people)
        {
            // What the tickets page shows for ?company=Acme&tech=<key>.
            var landed = list.Where(t => t.CustomerName == "Acme" && t.People!.Any(p => p.Key == person.Key)).ToList();
            landed.Should().NotBeEmpty($"{person.Name} was counted, so their link cannot open an empty list");
            landed.Count(t => t.People!.Single(p => p.Key == person.Key).Holds)
                .Should().Be(person.AssignedTickets, $"{person.Name}'s line says how many they hold");
        }

        // One PSA resource, spelled "TechX" on the ticket and "techx" on the time entry: one person in
        // People, so their link must reach BOTH tickets, not only the one whose spelling it kept.
        var kamal = people.Single(p => p.Name == "Kamal Arora");
        list.Count(t => t.People!.Any(p => p.Key == kamal.Key)).Should().Be(2);
    }
}
