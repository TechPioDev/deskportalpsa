using Desk.Application.Analytics;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

public class TechnicianMetricsTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset D(int day) => new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);

    private static Ticket T(string tech, int created, int? resolved, int? slaDue, decimal worked, bool note)
    {
        var t = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa,
            ClientCompanyId = Company, RequesterName = "R", RequesterEmail = "r@x", Title = "t",
            AssignedTechnicianExternalId = tech, CreatedAt = D(created),
            ResolvedAt = resolved is null ? null : D(resolved.Value),
            SlaDueAt = slaDue is null ? null : D(slaDue.Value),
            TimeWorkedHours = worked, PortalPriority = "HIGH",
        };
        if (note) t.Notes.Add(new TicketNote { MspOrganizationId = Org, AuthorName = "Tech", Body = "hi", IsPublic = true });
        return t;
    }

    private static async Task<TechnicianMetricsService> ServiceAsync(string dbName)
    {
        var clock = new TestClock(Now);
        var db = TestDbContextFactory.ForPlatform(dbName);
        db.PsaConnections.Add(new PsaConnection { Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://x", CredentialSecretRef = "m" });
        db.ClientCompanies.Add(new ClientCompany { Id = Company, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "C", ExternalCompanyId = "1" });
        // R1: A resolved within SLA (+worked +note), B open+overdue, C resolved but SLA breached (no work, no note)
        db.Tickets.Add(T("R1", created: 1, resolved: 2, slaDue: 3, worked: 2, note: true));
        db.Tickets.Add(T("R1", created: 5, resolved: null, slaDue: 6, worked: 0, note: false));
        db.Tickets.Add(T("R1", created: 3, resolved: 9, slaDue: 4, worked: 0, note: false));
        db.Tickets.Add(T("R2", created: 2, resolved: 3, slaDue: 5, worked: 1, note: true));
        await db.SaveChangesAsync();
        return new TechnicianMetricsService(db, new ProductivityScorer(), clock);
    }

    [Fact]
    public async Task Technician_counts_and_sla_are_computed_correctly()
    {
        var svc = await ServiceAsync(Guid.NewGuid().ToString());
        var m = await svc.ForTechnicianAsync(new MetricsFilter { TechnicianExternalId = "R1" }, ProductivityWeights.Default);

        m.Assigned.Should().Be(3);
        m.Resolved.Should().Be(2);
        m.Open.Should().Be(1);
        m.Overdue.Should().Be(1);          // B: unresolved, SLA due day 6 < now day 10
        m.SlaEligible.Should().Be(2);      // A and C
        m.WithinSla.Should().Be(1);        // A only
        m.SlaCompliancePct.Should().Be(50);
        m.TimeWorkedHours.Should().Be(2);
    }

    [Fact]
    public async Task Average_resolution_time_is_correct()
    {
        var svc = await ServiceAsync(Guid.NewGuid().ToString());
        var m = await svc.ForTechnicianAsync(new MetricsFilter { TechnicianExternalId = "R1" }, ProductivityWeights.Default);
        // A: 1 day = 24h; C: 6 days = 144h; avg = 84h
        m.AvgResolutionHours.Should().Be(84);
    }

    [Fact]
    public async Task Proxy_component_scores_and_overall_are_produced()
    {
        var svc = await ServiceAsync(Guid.NewGuid().ToString());
        var m = await svc.ForTechnicianAsync(new MetricsFilter { TechnicianExternalId = "R1" }, ProductivityWeights.Default);

        m.Components.SlaCompliance.Should().Be(50);
        m.Components.ResolutionRate.Should().BeApproximately(66.7, 0.1);
        m.Components.WorklogQuality.Should().Be(50);       // 1 of 2 resolved had worked>0
        m.Components.DocumentationQuality.Should().Be(50); // 1 of 2 resolved had a note
        m.Components.CustomerSatisfaction.Should().BeNull(); // untracked
        m.Score!.Overall.Should().BeGreaterThan(0);
        m.Score.MeasuredWeightFraction.Should().BeLessThan(1); // CSAT/first-response/reopen unmeasured
    }

    [Fact]
    public async Task Date_filter_narrows_the_window()
    {
        var svc = await ServiceAsync(Guid.NewGuid().ToString());
        // From day 4 → only B (created day 5) qualifies for R1.
        var m = await svc.ForTechnicianAsync(new MetricsFilter { TechnicianExternalId = "R1", From = D(4) }, ProductivityWeights.Default);
        m.Assigned.Should().Be(1);
    }

    [Fact]
    public async Task Team_comparison_groups_by_technician_ranked_by_score()
    {
        var svc = await ServiceAsync(Guid.NewGuid().ToString());
        var team = await svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);
        team.Should().HaveCount(2);
        team.Select(r => r.TechnicianExternalId).Should().Contain(["R1", "R2"]);
        // Ordered by score descending.
        team.Select(r => r.Score ?? -1).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Trend_reports_created_and_resolved_per_day()
    {
        var svc = await ServiceAsync(Guid.NewGuid().ToString());
        var trend = await svc.TrendAsync(new MetricsFilter());
        trend.Sum(p => p.Created).Should().Be(4);   // all four tickets created
        trend.Sum(p => p.Resolved).Should().Be(3);  // A, C, and R2's ticket resolved
    }

    [Fact]
    public async Task Trend_counts_a_resolution_in_the_window_even_when_the_ticket_was_raised_before_it()
    {
        // Ticket C was raised on day 3 and resolved on day 9. A window starting day 8 raised nothing,
        // but a resolution landed in it - windowing resolutions on the raise date reported zero.
        var svc = await ServiceAsync(Guid.NewGuid().ToString());

        var trend = await svc.TrendAsync(new MetricsFilter { From = D(8) });

        trend.Sum(p => p.Created).Should().Be(0);
        trend.Should().ContainSingle(p => p.Resolved > 0).Which.Should().Be(new TrendPoint(new DateOnly(2026, 1, 9), 0, 1));
    }
}
