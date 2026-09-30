using Desk.Api.Controllers;
using Desk.Application.Analytics;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The productivity figures, counted the way management reads them: each piece of work once, split by
/// where it came from, only the hours the PSA actually holds, a person's own view including everything
/// they did, and quality measured where there is something to measure.
/// </summary>
public class AnalyticsCorrectnessTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record World(AdminHarness H, TechnicianMetricsService Metrics, AppUser Anika, PsaConnection Conn);

    private static async Task<World> WorldAsync()
    {
        var h = AdminHarness.Create(Org);
        var anika = new AppUser { MspOrganizationId = Org, DisplayName = "Anika", Email = "anika@techpio.test", IsActive = true };
        var conn = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        };
        h.Db.AddRange(anika, conn);
        await h.Db.SaveChangesAsync();
        return new World(h, new TechnicianMetricsService(h.Db, new ProductivityScorer(), h.Clock), anika, conn);
    }

    private static Ticket T(World w, TicketOrigin origin, string title, Guid? holder = null, string? psaTech = null) => new()
    {
        MspOrganizationId = Org, Origin = origin, Title = title, RequesterName = "r", RequesterEmail = "r@x.test",
        PortalStatus = "IN_PROGRESS", PortalPriority = "NORMAL", AssignedAppUserId = holder, AssignedTechnicianExternalId = psaTech,
        PsaConnectionId = origin == TicketOrigin.Psa ? w.Conn.Id : null, Provider = origin == TicketOrigin.Psa ? ProviderType.AutotaskPsa : null,
        CreatedAt = w.H.Clock.GetUtcNow().AddDays(-2), PsaCreatedAt = w.H.Clock.GetUtcNow().AddDays(-2),
    };

    private static TicketTimeEntry Hours(Ticket t, Guid? who, decimal hours, TimeEntrySyncStatus status, DateTimeOffset at) => new()
    {
        MspOrganizationId = Org, TicketId = t.Id, AppUserId = who, Hours = hours, Billable = true, EntryDate = at, SyncStatus = status,
    };

    [Fact]
    public async Task Work_is_split_three_ways_and_by_psa_not_lumped_as_internal()
    {
        var w = await WorldAsync();
        w.H.Db.Tickets.AddRange(
            T(w, TicketOrigin.Psa, "Printer", w.Anika.Id),
            T(w, TicketOrigin.Internal, "Laptop rebuild", w.Anika.Id),
            T(w, TicketOrigin.Rmm, "Disk 95% full", w.Anika.Id));
        await w.H.Db.SaveChangesAsync();

        var m = await w.Metrics.ForTechnicianAsync(new MetricsFilter { AppUserId = w.Anika.Id }, ProductivityWeights.Default);

        // Monitoring used to count as the team's own work.
        m.Should().BeEquivalentTo(new { AssignedClient = 1, AssignedInternal = 1, AssignedMonitoring = 1 });
        m.BySource.Select(s => s.Label).Should().BeEquivalentTo(["Autotask", "Team boards", "Monitoring"]);
    }

    [Fact]
    public async Task Daily_hours_count_only_time_the_psa_holds_and_split_by_source()
    {
        var w = await WorldAsync();
        var psa = T(w, TicketOrigin.Psa, "Printer", w.Anika.Id);
        var board = T(w, TicketOrigin.Internal, "Laptop", w.Anika.Id);
        var alert = T(w, TicketOrigin.Rmm, "Disk", w.Anika.Id);
        var at = w.H.Clock.GetUtcNow().AddHours(-1);
        w.H.Db.Tickets.AddRange(psa, board, alert);
        w.H.Db.TicketTimeEntries.AddRange(
            Hours(psa, w.Anika.Id, 2m, TimeEntrySyncStatus.Synced, at),
            Hours(psa, w.Anika.Id, 5m, TimeEntrySyncStatus.Failed, at),   // rejected by the PSA
            Hours(psa, w.Anika.Id, 3m, TimeEntrySyncStatus.Pending, at),  // not there yet
            Hours(board, w.Anika.Id, 1.5m, TimeEntrySyncStatus.Synced, at),
            Hours(alert, w.Anika.Id, 0.5m, TimeEntrySyncStatus.Synced, at));
        await w.H.Db.SaveChangesAsync();

        var day = (await w.Metrics.DailyAsync(new MetricsFilter { AppUserId = w.Anika.Id })).Single();

        day.Should().BeEquivalentTo(new { Hours = 4m, InternalHours = 1.5m, MonitoringHours = 0.5m });
    }

    [Fact]
    public async Task A_linked_technician_s_own_view_includes_their_board_work_as_one_person()
    {
        var w = await WorldAsync();
        var psa = T(w, TicketOrigin.Psa, "Held in Autotask", psaTech: "123");
        var board = T(w, TicketOrigin.Internal, "Held on a board", holder: w.Anika.Id);
        var at = w.H.Clock.GetUtcNow().AddHours(-1);
        w.H.Db.Tickets.AddRange(psa, board);
        w.H.Db.TicketTimeEntries.AddRange(
            new TicketTimeEntry { MspOrganizationId = Org, TicketId = psa.Id, TechnicianExternalId = "123", Hours = 1m, EntryDate = at, SyncStatus = TimeEntrySyncStatus.Synced },
            Hours(board, w.Anika.Id, 2m, TimeEntrySyncStatus.Synced, at));
        await w.H.Db.SaveChangesAsync();
        var self = new MetricsFilter { AppUserId = w.Anika.Id, TechnicianExternalId = "123", EitherIdentity = true };

        (await w.Metrics.ForTechnicianAsync(self, ProductivityWeights.Default)).Assigned.Should().Be(2,
            "pinned to the PSA id alone, the board ticket was not hers");
        (await w.Metrics.DailyAsync(self)).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Hours = 3m, Name = "Anika" });
    }

    [Fact]
    public async Task First_response_reopens_and_satisfaction_are_measured_where_there_is_something_to_measure()
    {
        var w = await WorldAsync();
        var now = w.H.Clock.GetUtcNow();
        Ticket Done(string title, bool reopened)
        {
            var t = T(w, TicketOrigin.Internal, title, w.Anika.Id);
            t.PortalStatus = "RESOLVED";
            t.ResolvedAt = now.AddHours(-1);
            t.ReopenCount = reopened ? 1 : 0;
            return t;
        }
        var kept = Done("Kept promise", reopened: false);
        kept.FirstResponseDueAt = kept.CreatedAt.AddHours(4);
        kept.FirstRespondedAt = kept.CreatedAt.AddHours(1);
        var missed = Done("Missed promise", reopened: true);
        missed.FirstResponseDueAt = missed.CreatedAt.AddHours(1);
        missed.FirstRespondedAt = missed.CreatedAt.AddHours(3);
        w.H.Db.Tickets.AddRange(kept, missed);
        w.H.Db.TicketSatisfactions.AddRange(
            new TicketSatisfaction { MspOrganizationId = Org, TicketId = kept.Id, ClientCompanyId = Guid.NewGuid(), ClientUserId = Guid.NewGuid(), Rating = 5, RatedAt = now },
            new TicketSatisfaction { MspOrganizationId = Org, TicketId = missed.Id, ClientCompanyId = Guid.NewGuid(), ClientUserId = Guid.NewGuid(), Rating = 2, RatedAt = now });
        await w.H.Db.SaveChangesAsync();

        var m = await w.Metrics.ForTechnicianAsync(new MetricsFilter { AppUserId = w.Anika.Id }, ProductivityWeights.Default);

        m.Should().BeEquivalentTo(new
        {
            FirstResponseEligible = 2, FirstResponseMet = 1, AvgFirstResponseHours = (double?)2.0, FirstResponseSample = 2,
            Reopened = 1, ReopenRatePct = (double?)50.0, Rated = 2, Satisfied = 1,
        });
        // The three were always in the model and always empty; now they count towards the score.
        m.Components.Should().BeEquivalentTo(new { FirstResponse = (double?)50.0, ReopenScore = (double?)50.0, CustomerSatisfaction = (double?)50.0 });
    }

    private sealed class CapturingMetrics : ITechnicianMetricsService
    {
        public MetricsFilter? Asked;
        public Task<TechnicianMetrics> ForTechnicianAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default)
        {
            Asked = filter;
            return Task.FromResult(new TechnicianMetrics { TechnicianExternalId = "x" });
        }
        public Task<IReadOnlyList<TeamComparisonRow>> TeamAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrendPoint>> TrendAsync(MetricsFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TechnicianDay>> DailyAsync(MetricsFilter filter, CancellationToken ct = default)
        {
            Asked = filter;
            return Task.FromResult<IReadOnlyList<TechnicianDay>>([]);
        }
    }

    [Fact]
    public async Task A_technician_asking_for_their_own_figures_gets_both_identities_and_nobody_else_s()
    {
        var metrics = new CapturingMetrics();
        var me = Guid.NewGuid();
        var user = new TestCurrentUser(Org, userId: me, technicianExternalId: "123",
            permissions: new HashSet<string> { Permissions.ProductivityViewOwn });
        var controller = new DashboardController(metrics, null!, null!, user);

        // Asking for a colleague is ignored; the answer is pinned to the caller, by either identity.
        await controller.Technician(new DashboardController.DashboardQuery { Technician = "999" }, default);

        metrics.Asked.Should().BeEquivalentTo(new { AppUserId = (Guid?)me, TechnicianExternalId = "123", EitherIdentity = true });
    }
}
