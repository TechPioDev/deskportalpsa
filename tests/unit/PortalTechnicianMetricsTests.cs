using Desk.Application.Analytics;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Productivity for technicians who exist only in this portal.
///
/// Every figure in the product used to be keyed on the PSA's technician id. On a desk whose
/// technicians have no PSA account — their work reaches the provider under the integration's API
/// identity — that meant the entire team aggregated into one anonymous row, or vanished. These
/// tests are the ones that fail if attribution ever collapses back onto the provider's id.
/// </summary>
public class PortalTechnicianMetricsTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset D(int day) => new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public required TechnicianMetricsService Svc { get; init; }
        public required DeskDbContextHolder Db { get; init; }
        public required Guid Basit { get; init; }
        public required Guid Komal { get; init; }
    }

    /// <summary>Keeps the context alive alongside the service so tests can add rows after setup.</summary>
    public sealed class DeskDbContextHolder(Desk.Infrastructure.Persistence.DeskDbContext db)
    {
        public Desk.Infrastructure.Persistence.DeskDbContext Value { get; } = db;
    }

    private static async Task<Fixture> SetupAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "m",
        });
        db.ClientCompanies.Add(new ClientCompany
        {
            Id = Company, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "C", ExternalCompanyId = "1",
        });

        var basit = new AppUser { MspOrganizationId = Org, Email = "basit@techpio.com", DisplayName = "Basit Lone" };
        var komal = new AppUser { MspOrganizationId = Org, Email = "komal@techpio.com", DisplayName = "Komal Sharma" };
        db.AppUsers.AddRange(basit, komal);
        await db.SaveChangesAsync();

        return new Fixture
        {
            Svc = new TechnicianMetricsService(db, new ProductivityScorer(), new TestClock(Now)),
            Db = new DeskDbContextHolder(db),
            Basit = basit.Id,
            Komal = komal.Id,
        };
    }

    private static Ticket Ticket(Guid? appUser, string? psaTech, int created, int? resolved,
        string? psaTechName = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
        ClientCompanyId = Company, RequesterName = "R", RequesterEmail = "r@x", Title = "t",
        PortalStatus = "NEW", PortalPriority = "NORMAL",
        AssignedAppUserId = appUser, AssignedTechnicianExternalId = psaTech,
        AssignedTechnicianName = psaTechName,
        CreatedAt = D(created), PsaCreatedAt = D(created),
        ResolvedAt = resolved is null ? null : D(resolved.Value),
    };

    private static TicketTimeEntry Time(Guid ticketId, Guid? appUser, string? psaTech, int day, decimal hours, bool billable) => new()
    {
        MspOrganizationId = Org, TicketId = ticketId, AppUserId = appUser, TechnicianExternalId = psaTech,
        Hours = hours, Billable = billable, EntryDate = D(day),
    };

    [Fact]
    public async Task The_team_view_names_portal_technicians_instead_of_dropping_them()
    {
        // Before: TeamAsync filtered on `r.Tech is not null`, so a ticket assigned only in the
        // portal never appeared at all — and one assigned to the API user on everyone's behalf
        // appeared as a single row holding the whole desk's work.
        var f = await SetupAsync();
        f.Db.Value.Tickets.AddRange(
            Ticket(f.Basit, psaTech: "api-user", created: 1, resolved: 2),
            Ticket(f.Basit, psaTech: "api-user", created: 2, resolved: 3),
            Ticket(f.Komal, psaTech: "api-user", created: 3, resolved: 4));
        await f.Db.Value.SaveChangesAsync();

        var team = await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);

        team.Select(r => r.TechnicianName).Should().BeEquivalentTo(["Basit Lone", "Komal Sharma"]);
        team.Single(r => r.TechnicianName == "Basit Lone").Resolved.Should().Be(2);
        team.Single(r => r.TechnicianName == "Komal Sharma").Resolved.Should().Be(1);
    }

    [Fact]
    public async Task A_PSA_technician_still_appears_when_nobody_holds_the_ticket_here()
    {
        // The other half. A desk mid-migration has both, and a change that attributed only portal
        // work would lose everything the PSA assigned — the exact mirror of the bug being fixed.
        var f = await SetupAsync();
        f.Db.Value.Tickets.Add(Ticket(appUser: null, psaTech: "29682889", created: 1, resolved: 2));
        await f.Db.Value.SaveChangesAsync();

        var team = await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);

        team.Should().ContainSingle();
        team[0].TechnicianExternalId.Should().Be("29682889");
        team[0].AppUserId.Should().BeNull();
    }

    [Fact]
    public async Task A_PSA_technician_is_named_not_numbered()
    {
        // "29682889" in a table headed Technician Performance is not a name, and nobody reading the
        // dashboard can turn it into one. The sync already caches the provider's display name on
        // the ticket; this is the code that uses it.
        var f = await SetupAsync();
        f.Db.Value.Tickets.Add(Ticket(appUser: null, psaTech: "29682889", created: 1, resolved: 2,
            psaTechName: "Basit Lone"));
        await f.Db.Value.SaveChangesAsync();

        var team = await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);

        team.Should().ContainSingle();
        team[0].TechnicianName.Should().Be("Basit Lone");
        team[0].TechnicianExternalId.Should().Be("29682889", "the id stays as the stable key");
    }

    [Fact]
    public async Task A_PSA_technician_with_no_cached_name_still_shows_something_stable()
    {
        // Fallback, not a blank cell: an empty Technician column reads as missing data and sends
        // someone hunting for a bug that is not there.
        var f = await SetupAsync();
        f.Db.Value.Tickets.Add(Ticket(appUser: null, psaTech: "29682889", created: 1, resolved: 2));
        await f.Db.Value.SaveChangesAsync();

        var team = await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);

        team[0].TechnicianName.Should().Be("29682889");
    }

    [Fact]
    public async Task Hours_come_from_time_entries_so_one_persons_afternoon_is_not_credited_to_another()
    {
        // The ticket's own worked total is the sum of everyone who touched it. Reading hours from
        // there would credit the current assignee with a colleague's work, and would move that
        // work to somebody new the moment the ticket was reassigned.
        var f = await SetupAsync();
        var ticket = Ticket(f.Basit, psaTech: "api-user", created: 1, resolved: 2);
        f.Db.Value.Tickets.Add(ticket);
        f.Db.Value.TicketTimeEntries.AddRange(
            Time(ticket.Id, f.Basit, psaTech: null, day: 1, hours: 2.5m, billable: true),
            Time(ticket.Id, f.Komal, psaTech: null, day: 1, hours: 1.0m, billable: false));
        await f.Db.Value.SaveChangesAsync();

        var days = await f.Svc.DailyAsync(new MetricsFilter());

        var basitDay = days.Single(d => d.Name == "Basit Lone" && d.Date == new DateOnly(2026, 1, 1));
        basitDay.Hours.Should().Be(2.5m);
        basitDay.BillableHours.Should().Be(2.5m);

        var komalDay = days.Single(d => d.Name == "Komal Sharma" && d.Date == new DateOnly(2026, 1, 1));
        komalDay.Hours.Should().Be(1.0m);
        komalDay.BillableHours.Should().Be(0m, "an hour marked non-billable is worked time that cannot be charged");
    }

    [Fact]
    public async Task Hours_and_resolutions_on_the_same_day_are_one_row_not_two()
    {
        // Two sources feed one day. Seeding them independently would produce a row with hours and
        // no resolutions beside a row with resolutions and no hours, and every per-day average
        // computed from that would be wrong by exactly a factor of two.
        var f = await SetupAsync();
        var ticket = Ticket(f.Basit, psaTech: null, created: 1, resolved: 1);
        f.Db.Value.Tickets.Add(ticket);
        f.Db.Value.TicketTimeEntries.Add(Time(ticket.Id, f.Basit, psaTech: null, day: 1, hours: 3m, billable: true));
        await f.Db.Value.SaveChangesAsync();

        var days = await f.Svc.DailyAsync(new MetricsFilter());

        days.Should().ContainSingle();
        days[0].Hours.Should().Be(3m);
        days[0].Resolved.Should().Be(1);
        days[0].Name.Should().Be("Basit Lone");
    }

    [Fact]
    public async Task A_range_returns_only_the_days_inside_it()
    {
        // One query serves a week, a month, a quarter and a custom range; this is the bound that
        // makes all four correct, and the only thing that differs between them.
        var f = await SetupAsync();
        var ticket = Ticket(f.Basit, psaTech: null, created: 1, resolved: null);
        f.Db.Value.Tickets.Add(ticket);
        f.Db.Value.TicketTimeEntries.AddRange(
            Time(ticket.Id, f.Basit, null, day: 2, hours: 1m, billable: true),
            Time(ticket.Id, f.Basit, null, day: 5, hours: 1m, billable: true),
            Time(ticket.Id, f.Basit, null, day: 9, hours: 1m, billable: true));
        await f.Db.Value.SaveChangesAsync();

        var days = await f.Svc.DailyAsync(new MetricsFilter { From = D(4), To = D(6) });

        days.Select(d => d.Date).Should().BeEquivalentTo([new DateOnly(2026, 1, 5)]);
    }

    [Fact]
    public async Task One_technicians_series_excludes_everyone_else()
    {
        // What the endpoint relies on when someone without the team permission asks for their own
        // figures: the filter, not the caller's good manners, is what keeps a colleague's numbers out.
        var f = await SetupAsync();
        var a = Ticket(f.Basit, null, created: 1, resolved: 1);
        var b = Ticket(f.Komal, null, created: 1, resolved: 1);
        f.Db.Value.Tickets.AddRange(a, b);
        f.Db.Value.TicketTimeEntries.AddRange(
            Time(a.Id, f.Basit, null, day: 1, hours: 4m, billable: true),
            Time(b.Id, f.Komal, null, day: 1, hours: 8m, billable: true));
        await f.Db.Value.SaveChangesAsync();

        var days = await f.Svc.DailyAsync(new MetricsFilter { AppUserId = f.Basit });

        days.Should().ContainSingle();
        days[0].Name.Should().Be("Basit Lone");
        days[0].Hours.Should().Be(4m);
    }
}
