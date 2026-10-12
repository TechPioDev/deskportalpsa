using Desk.Application.Analytics;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A PSA login is a person only within its PSA account.
///
/// Someone with no portal account was identified by the PSA's id alone. Two PSA accounts can each
/// have a resource 42, and with two connections they were one person everywhere a person is
/// counted: one row in the team table with both people's tickets, one day of hours with both
/// people's time, one satisfaction score, one name for both in the ticket list, and a filter that
/// returned both people's tickets. Every figure here is asserted from what the services return, so
/// the same tests run against the code as it was and fail there.
/// </summary>
public class PersonAcrossConnectionsTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid AccountB = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Globex = Guid.NewGuid();
    private static readonly DateTimeOffset Jun1 = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static Ticket T(Guid connection, Guid client, string title, string techName) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = connection, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
        ExternalTicketId = title, ClientCompanyId = client, RequesterName = "r", RequesterEmail = "r@a.test",
        Title = title, PortalStatus = "CLOSED", PortalPriority = "NORMAL", PsaCreatedAt = Jun1,
        ResolvedAt = Jun1.AddHours(3), ClosedAt = Jun1.AddHours(3),
        // The same id in both PSA accounts. Two people.
        AssignedTechnicianExternalId = "42", AssignedTechnicianName = techName,
    };

    private static async Task<DeskDbContext> SeedAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.AddRange(
            new PsaConnection { Id = AccountA, MspOrganizationId = Org, Name = "Autotask A", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://a", CredentialSecretRef = "mem://a" },
            new PsaConnection { Id = AccountB, MspOrganizationId = Org, Name = "Autotask B", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://b", CredentialSecretRef = "mem://b" });
        db.ClientCompanies.AddRange(
            new ClientCompany { Id = Acme, MspOrganizationId = Org, PsaConnectionId = AccountA, Name = "Acme", ExternalCompanyId = "1" },
            new ClientCompany { Id = Globex, MspOrganizationId = Org, PsaConnectionId = AccountB, Name = "Globex", ExternalCompanyId = "1" });
        var ashas = T(AccountA, Acme, "asha's ticket", "Asha Rao");
        var bilals = T(AccountB, Globex, "bilal's ticket", "Bilal Khan");
        db.Tickets.AddRange(ashas, bilals);
        await db.SaveChangesAsync();

        db.TicketTimeEntries.AddRange(
            new TicketTimeEntry { MspOrganizationId = Org, TicketId = ashas.Id, Hours = 2m, Billable = true, TechnicianExternalId = "42", TechnicianName = "Asha Rao", EntryDate = Jun1, SyncStatus = TimeEntrySyncStatus.Synced },
            new TicketTimeEntry { MspOrganizationId = Org, TicketId = bilals.Id, Hours = 3m, Billable = true, TechnicianExternalId = "42", TechnicianName = "Bilal Khan", EntryDate = Jun1, SyncStatus = TimeEntrySyncStatus.Synced });
        db.TicketSatisfactions.AddRange(
            new TicketSatisfaction { MspOrganizationId = Org, TicketId = ashas.Id, ClientCompanyId = Acme, ClientUserId = Guid.NewGuid(), Rating = 5, RatedAt = Jun1.AddHours(4), TechnicianExternalId = "42", TechnicianName = "Asha Rao" },
            new TicketSatisfaction { MspOrganizationId = Org, TicketId = bilals.Id, ClientCompanyId = Globex, ClientUserId = Guid.NewGuid(), Rating = 2, RatedAt = Jun1.AddHours(4), TechnicianExternalId = "42", TechnicianName = "Bilal Khan" });
        await db.SaveChangesAsync();
        return db;
    }

    private static TicketReadService Reads(DeskDbContext db) =>
        new(db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));

    private static MetricsFilter June => new() { From = Jun1.AddDays(-1), To = Jun1.AddDays(1) };

    [Fact]
    public async Task The_ticket_list_names_two_people_and_a_name_filters_to_that_persons_tickets()
    {
        await using var db = await SeedAsync();

        var list = await Reads(db).ListAllAsync();
        var asha = list.Single(t => t.Title == "asha's ticket").People!.Single(p => p.Holds);
        var bilal = list.Single(t => t.Title == "bilal's ticket").People!.Single(p => p.Holds);

        (asha.Name, bilal.Name).Should().Be(("Asha Rao", "Bilal Khan"), "each ticket is named from its own PSA account, not from whichever was read first");
        asha.Key.Should().NotBe(bilal.Key, "the same id in two PSA accounts is two people");

        (await Reads(db).PageAsync(new TicketQuery(PersonKey: asha.Key))).Items.Select(t => t.Title)
            .Should().Equal(["asha's ticket"], "her name opens her tickets, not his as well");
        (await Reads(db).PageAsync(new TicketQuery(PersonKey: bilal.Key))).Items.Select(t => t.Title).Should().Equal("bilal's ticket");
    }

    [Fact]
    public async Task The_list_of_people_to_filter_by_offers_both()
    {
        await using var db = await SeedAsync();

        var people = (await Reads(db).FacetsAsync()).People;

        people.Select(p => p.Name).Should().BeEquivalentTo(["Asha Rao", "Bilal Khan"]);
        people.Select(p => p.Key).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_key_saved_before_keys_named_their_connection_still_finds_that_login_everywhere()
    {
        await using var db = await SeedAsync();

        // An old saved view holds "x:42". It meant "resource 42", and still does.
        (await Reads(db).PageAsync(new TicketQuery(PersonKey: "x:42"))).Total.Should().Be(2);
        (await Reads(db).PageAsync(new TicketQuery(PersonKey: "x:43"))).Total.Should().Be(0);
    }

    [Fact]
    public void A_key_names_its_connection_and_reads_back()
    {
        var key = PersonKey.For(null, AccountA, " Tech-X ");

        PersonKey.TryParseExternal(key, out var connection, out var id).Should().BeTrue();
        (connection, id).Should().Be((AccountA, "tech-x"));
        key.Should().NotBe(PersonKey.For(null, AccountB, "tech-x"));

        PersonKey.TryParseExternal("x:42", out var none, out var legacy).Should().BeTrue();
        (none, legacy).Should().Be(((Guid?)null, "42"));
        var user = Guid.NewGuid();
        PersonKey.For(user, AccountA, "42").Should().Be(PersonKey.For(user, AccountB, "9"), "a portal user is one person across every PSA account");
        PersonKey.TryParseExternal(PersonKey.For(user, null, null), out _, out _).Should().BeFalse();
        PersonKey.TryParseExternal("x:", out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_team_table_has_a_row_for_each_and_each_day_of_hours_is_their_own()
    {
        await using var db = await SeedAsync();
        var metrics = new TechnicianMetricsService(db, new ProductivityScorer(), new TestClock(Jun1.AddDays(1)));

        var team = await metrics.TeamAsync(June, ProductivityWeights.Default);

        team.Select(r => (r.TechnicianName, r.Resolved)).Should().BeEquivalentTo([("Asha Rao", 1), ("Bilal Khan", 1)],
            "it was one row, with her name and both people's tickets");
        team.Select(r => r.Key).Distinct().Should().HaveCount(2, "a row needs a key of its own: the PSA id is the same on both");

        var days = await metrics.DailyAsync(June);

        days.Select(d => (d.Name, d.Hours, d.Resolved)).Should().BeEquivalentTo([("Asha Rao", 2m, 1), ("Bilal Khan", 3m, 1)],
            "it was one day of five hours");
        days.Select(d => d.PsaConnectionId).Should().BeEquivalentTo(new Guid?[] { AccountA, AccountB });
    }

    [Fact]
    public async Task Each_clients_people_are_named_from_that_clients_PSA_account()
    {
        await using var db = await SeedAsync();

        var clients = (await new ClientWorkloadService(db).ForClientsAsync(June)).Clients;

        var asha = clients.Single(c => c.ClientName == "Acme").People.Single();
        var bilal = clients.Single(c => c.ClientName == "Globex").People.Single();
        (asha.Name, asha.HoursLogged).Should().Be(("Asha Rao", 2m));
        (bilal.Name, bilal.HoursLogged).Should().Be(("Bilal Khan", 3m), "he was shown under her name: one list of names served every PSA account");
        asha.Key.Should().NotBe(bilal.Key);

        // The name in this list opens the ticket list on exactly the tickets it was counted from.
        (await Reads(db).PageAsync(new TicketQuery(PersonKey: bilal.Key))).Items.Select(t => t.Title).Should().Equal("bilal's ticket");
    }

    [Fact]
    public async Task Portal_coverage_measures_each_of_them()
    {
        await using var db = await SeedAsync();

        var report = await new PortalCoverageService(db).CoverageAsync(June);

        report.Technicians.Select(t => (t.TechnicianExternalId, t.PsaHours, t.PsaEntries)).Should().BeEquivalentTo(
            [("42", 2m, 1), ("42", 3m, 1)], "it was one technician with five hours");
    }

    [Fact]
    public async Task Satisfaction_is_scored_for_each_of_them()
    {
        await using var db = await SeedAsync();

        var summary = await new SatisfactionService(db, new TestClock(Jun1.AddDays(1))).SummaryAsync(Jun1.AddDays(-1), Jun1.AddDays(1));

        summary.ByTechnician.Select(g => (g.Name, g.Ratings, g.Average)).Should().BeEquivalentTo(
            [("Asha Rao", 1, 5.0), ("Bilal Khan", 1, 2.0)], "it was one technician with two ratings averaging 3.5");
    }
}
