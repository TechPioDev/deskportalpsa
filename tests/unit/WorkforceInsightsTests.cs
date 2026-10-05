using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Reporting;
using Desk.Infrastructure.Workforce;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Phase 8: management insights, the capacity forecast, quality signals and the report center. Same
/// world as the planning, work-execution and analytics tests (see WorkforceAnalyticsTests). The clock
/// starts on Thu 1 Jan 2026; the window under test is the week after, Mon 5 - Sun 11 Jan, unless a
/// test moves the clock.
///
/// Every figure asserted here is defined in docs/workforce-scheduling/PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md.
/// </summary>
public partial class WorkPlanTests
{
    private static InsightsQuery NextWeek => new("custom", Monday, Sunday);
    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    private static int _insightTickets;

    /// <summary>An open ticket: the team's own unless <paramref name="psa"/>, held by <paramref name="holder"/> or by nobody.</summary>
    private static async Task<Ticket> OpenWork(World w, string title, AppUser? holder, Guid? team = null, DateTimeOffset? due = null, bool psa = false, bool paused = false,
        string status = "IN_PROGRESS", string priority = "NORMAL", string? category = null, Guid? client = null, string? login = null, TicketSyncStatus sync = TicketSyncStatus.Synced)
    {
        var n = Interlocked.Increment(ref _insightTickets);
        var t = new Ticket
        {
            MspOrganizationId = OrgA, Origin = psa ? TicketOrigin.Psa : TicketOrigin.Internal, BoardId = psa ? null : w.Board.Id, Number = psa ? null : $"INT-8{n:00000}",
            Provider = psa ? ProviderType.AutotaskPsa : null, ExternalTicketId = psa ? $"8{n:0000}" : null, PsaConnectionId = psa ? w.Autotask.PsaConnectionId : null,
            RequesterName = "Lena Lead", RequesterEmail = "lena@techpio.test", Title = title, PortalStatus = status, PortalPriority = priority, PortalCategory = category,
            AssignedAppUserId = holder?.Id, AssignedTeamId = team, AssignedTechnicianExternalId = psa ? login ?? "" : null, SlaDueAt = due, SlaPausedAt = paused ? w.Clock.GetUtcNow() : null,
            ClientCompanyId = client ?? w.JasonsTicket.ClientCompanyId, SyncStatus = sync, UpdateHash = "hash-" + title, CreatedByUserId = w.Lead.Id,
        };
        w.Db.Tickets.Add(t);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return t;
    }

    /// <summary>Whole working half-days (four hours each) for one person on one ticket: <paramref name="halfDays"/> of them from <paramref name="from"/>, mornings first.</summary>
    private static async Task HalfDays(World w, Ticket ticket, AppUser who, DateOnly from, int halfDays, bool tentative = false, bool morningsOnly = false)
    {
        var admin = w.As(w.Admin);
        for (var i = 0; i < halfDays; i++)
        {
            var day = from.AddDays(morningsOnly ? i : i / 2);
            var (start, end) = morningsOnly || i % 2 == 0 ? ("08:30", "12:30") : ("13:30", "17:30");
            await admin.Plans.CreateAsync(w.Admin.Id, Place(ticket, who, day, start, end) with { Tentative = tentative });
        }
    }

    private static async Task Estimate(World w, Ticket ticket, int minutes, Guid? skill = null, DateTimeOffset? earliest = null, DateTimeOffset? latest = null)
        => await w.As(w.Admin).Plans.SetRequirementAsync(w.Admin.Id, ticket.Id, new PlanningRequirementInput(minutes, earliest, latest, true, skill));

    /// <summary>Sam works mornings only from Monday: with Jason and Abbie on eight hours, the three of them have exactly 100 hours in the week.</summary>
    private static async Task<World> HundredHourWorldAsync()
    {
        var w = await WorldAsync();
        await w.As(w.Admin).Schedules.SaveAsync(w.Admin.Id, w.Sam.Id, new WorkScheduleInput(Monday, Zone, Weekdays.Select(d => new WorkDayInput(d, "08:30", "12:30", [])).ToList()));
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static async Task Grant(World w, string role, string permission)
    {
        var r = await w.Db.Roles.AsNoTracking().SingleAsync(x => x.Name == role);
        w.Db.Add(new RolePermission { RoleId = r.Id, PermissionKey = permission, Scope = PermissionScope.All });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    // ---- windows and comparison periods -------------------------------------------------------------------

    [Fact]
    public void Forecast_windows_start_today_or_later_and_a_comparison_is_a_period_against_the_one_before()
    {
        var today = new DateOnly(2026, 1, 7); // a Wednesday
        AnalyticsPeriodDto W(string? key, DateOnly? from = null, DateOnly? to = null, DateOnly? on = null) => WorkforceAnalyticsService.ResolveWindow(new InsightsQuery(key, from, to), on ?? today, Zone);

        W("next-7").Should().Be(new AnalyticsPeriodDto("next-7", today, new DateOnly(2026, 1, 13), Zone, "Wed 7 Jan – Tue 13 Jan 2026", 7, true));
        W("next-14").Should().Match<AnalyticsPeriodDto>(p => p.From == today && p.To == new DateOnly(2026, 1, 20) && p.Days == 14);
        W("next-30").Should().Match<AnalyticsPeriodDto>(p => p.From == today && p.To == new DateOnly(2026, 2, 5) && p.Days == 30);
        W("this-week").Should().Match<AnalyticsPeriodDto>(p => p.From == today && p.To == new DateOnly(2026, 1, 11) && p.Days == 5, "what is left of the week, not the days already gone");
        W("this-week", on: new DateOnly(2026, 1, 11)).Should().Match<AnalyticsPeriodDto>(p => p.Days == 1 && p.Label == "Sun 11 Jan 2026");
        W("next-week").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2026, 1, 12) && p.To == new DateOnly(2026, 1, 18));
        W("this-month").Should().Match<AnalyticsPeriodDto>(p => p.From == today && p.To == new DateOnly(2026, 1, 31) && p.Days == 25);
        W(null).Key.Should().Be("next-7", "the default");
        W(null, new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 20)).Should().Match<AnalyticsPeriodDto>(p => p.Key == "custom" && p.Days == 11 && p.TimeZone == Zone);
        W("custom", today, today.AddDays(61)).Days.Should().Be(62);

        ((Func<AnalyticsPeriodDto>)(() => W("custom"))).Should().Throw<ValidationFailedException>().WithMessage("A custom window needs both a first and a last date.");
        ((Func<AnalyticsPeriodDto>)(() => W("custom", today.AddDays(-1), today))).Should().Throw<ValidationFailedException>().WithMessage("A forecast starts today or later.");
        ((Func<AnalyticsPeriodDto>)(() => W("custom", today, today.AddDays(62)))).Should().Throw<ValidationFailedException>().WithMessage("A forecast covers at most 62 days.");
        ((Func<AnalyticsPeriodDto>)(() => W("custom", today.AddDays(3), today))).Should().Throw<ValidationFailedException>().WithMessage("The last date is before the first.");
        ((Func<AnalyticsPeriodDto>)(() => W("next-quarter"))).Should().Throw<ValidationFailedException>().WithMessage("Unknown window*");

        (AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous) C(string? key) => WorkforceAnalyticsService.ResolveComparison(key, today, Zone);
        C("last-7").Should().Match<(AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous)>(x =>
            x.Current.From == new DateOnly(2026, 1, 1) && x.Current.To == today && x.Previous.From == new DateOnly(2025, 12, 25) && x.Previous.To == new DateOnly(2025, 12, 31));
        C("last-30").Should().Match<(AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous)>(x =>
            x.Current.From == new DateOnly(2025, 12, 9) && x.Current.Days == 30 && x.Previous.From == new DateOnly(2025, 11, 9) && x.Previous.To == new DateOnly(2025, 12, 8) && x.Previous.Days == 30);
        C("last-week").Should().Match<(AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous)>(x =>
            x.Current.From == new DateOnly(2025, 12, 29) && x.Current.To == new DateOnly(2026, 1, 4) && x.Previous.From == new DateOnly(2025, 12, 22) && x.Previous.To == new DateOnly(2025, 12, 28));
        C("last-month").Should().Match<(AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous)>(x =>
            x.Current.From == new DateOnly(2025, 12, 1) && x.Current.To == new DateOnly(2025, 12, 31) && x.Previous.From == new DateOnly(2025, 11, 1) && x.Previous.To == new DateOnly(2025, 11, 30),
            "a calendar month against the calendar month before, whatever their lengths");
        (C(null).Current.Key, C(null).Previous.Key, C("last-7").Current.EndsInFuture).Should().Be(("last-30", "previous", false));
        ((Func<object>)(() => C("last-year"))).Should().Throw<ValidationFailedException>().WithMessage("Unknown comparison*");
    }

    // ---- the specification fixtures -------------------------------------------------------------------------------

    [Fact]
    public async Task Fixture_A_capacity_100h_confirmed_60h_tentative_10h_unscheduled_20h_and_five_unestimated_items()
    {
        var w = await HundredHourWorldAsync();
        // Confirmed 60 h: Jason Mon-Thu (32 h), Abbie Mon-Wed (24 h), Sam Monday morning (4 h).
        await HalfDays(w, w.JasonsTicket, w.Jason, Monday, 8);
        await HalfDays(w, w.JasonsTicket, w.Abbie, Monday, 6);
        await HalfDays(w, w.SamsTicket, w.Sam, Monday, 1, morningsOnly: true);
        // Tentative 10 h: Abbie all Thursday and two hours on Friday.
        await HalfDays(w, w.JasonsTicket, w.Abbie, Monday.AddDays(3), 2, tentative: true);
        await w.As(w.Admin).Plans.CreateAsync(w.Admin.Id, Place(w.JasonsTicket, w.Abbie, Friday, "08:30", "10:30") with { Tentative = true });
        // Estimated and not scheduled, 20 h: four hours of Jason's Autotask ticket and sixteen of a ticket Sam holds.
        await Estimate(w, w.Autotask, 240);
        await Estimate(w, await OpenWork(w, "Vault audit", w.Sam), 960);
        // Five open tickets nobody has sized or planned.
        for (var i = 0; i < 5; i++) await OpenWork(w, $"Unsized {i}", w.Abbie);

        var admin = w.As(w.Admin).Analytics;
        var fc = await admin.ForecastAsync(w.Admin.Id, NextWeek);

        fc.Window.Should().Be(new AnalyticsPeriodDto("custom", Monday, Sunday, Zone, "Mon 5 Jan – Sun 11 Jan 2026", 7, true));
        var t = fc.Totals;
        (t.CapacityMinutes, t.ConfirmedMinutes, t.TentativeMinutes, t.UnscheduledMinutes, t.UnscheduledItems, t.UnestimatedItems).Should().Be((6000, 3600, 600, 1200, 2, 5));
        (t.ConfirmedRemainingMinutes, t.ProjectedMinutes, t.GapMinutes).Should().Be((2400, 5400, 600), "40 h left after confirmed work; 90 h projected; 10 h of capacity left");
        (t.ConfirmedPercent, t.ProjectedPercent).Should().Be((60d, 90d));
        // Unestimated work is a count: it is in no hours figure.
        (t.ConfirmedMinutes + t.TentativeMinutes + t.UnscheduledMinutes).Should().Be(t.ProjectedMinutes);

        // Nothing is over: at 90% the capacity statement is a Watch, with every component of the projection attached.
        fc.Attention.Select(a => (a.Key, a.Severity)).Should().Equal(("capacity", InsightSeverity.Watch), ("unscheduled", InsightSeverity.Watch), ("unestimated", InsightSeverity.Info));
        var capacity = fc.Attention[0];
        capacity.Title.Should().Be("Projected demand takes 90% of capacity in this window; 10h is left.");
        capacity.Rule.Should().Contain("(confirmed + tentative + estimated unscheduled) ÷ capacity");
        capacity.Facts.Select(x => (x.Label, x.Value)).Should().Equal(("Capacity", "100h"), ("Confirmed", "60h"), ("Tentative", "10h"), ("Estimated unscheduled", "20h"), ("Projected", "90h"), ("Gap", "+10h"), ("Projected load", "90%"));
        fc.Attention[2].Title.Should().Be("5 open work items have no estimate and no plan, so their demand is unknown.");

        // Per person, in name order; nobody is over 100%.
        fc.People.Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Harpal Admin", "Jason Carter", "Lena Lead", "Sam Shah");
        ForecastFiguresDto Of(string name) => fc.People.Single(p => p.DisplayName == name).Figures;
        Of("Abbie Noor").Should().Be(new ForecastFiguresDto(2400, 1440, 600, 0, 0, 5, 2040, 960, 360, 60d, 85d));
        Of("Jason Carter").Should().Be(new ForecastFiguresDto(2400, 1920, 0, 240, 1, 0, 2160, 480, 240, 80d, 90d));
        Of("Sam Shah").Should().Be(new ForecastFiguresDto(1200, 240, 0, 960, 1, 0, 1200, 960, 0, 20d, 100d));
        Of("Lena Lead").Should().Be(new ForecastFiguresDto(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null), "offered for planned work, with no schedule: zero capacity and no percentage");
        fc.People.Sum(p => p.Figures.CapacityMinutes ?? 0).Should().Be(t.CapacityMinutes, "the rows add up to the total");
        fc.People.Sum(p => p.Figures.ProjectedMinutes).Should().Be(t.ProjectedMinutes);
        fc.Unassigned.Should().BeNull();

        // Per team and per day.
        fc.Teams.Select(g => (g.Name, g.People, g.Figures.CapacityMinutes, g.Figures.ProjectedMinutes, g.Figures.GapMinutes)).Should().Equal(("NOC", 3, 4800, 4200, 600), ("Security", 1, 1200, 1200, 0));
        fc.Daily.Should().HaveCount(7);
        fc.Daily[0].Should().Be(new ForecastDayDto(Monday, 1200, 1200, 0, 0));
        fc.Daily.Single(d => d.Date == Saturday).Should().Be(new ForecastDayDto(Saturday, 0, 0, 0, 0));
        (fc.Daily.Sum(d => d.CapacityMinutes ?? 0), fc.Daily.Sum(d => d.ConfirmedMinutes), fc.Daily.Sum(d => d.TentativeMinutes)).Should().Be((6000, 3600, 600));
        (fc.UnscheduledOverdueMinutes, fc.UnscheduledNoDateMinutes).Should().Be((0, 1200), "nothing here has a due date, so no day is given the unscheduled effort");

        // Coverage and the planning data the forecast rests on.
        fc.Coverage.Should().Be(new ScheduleCoverageDto(9, 2, 7, 1200, 0, 1200, 0d));
        fc.DataQuality.Should().Be(new PlanningDataQualityDto(5, 2, 0, 9, 2, 0, 0));
        fc.Clients.Should().ContainSingle().Which.Should().Match<ForecastGroupDto>(c => c.Name == "ABC Company" && c.Figures.ProjectedMinutes == 5400 && c.Figures.UnestimatedItems == 5 && c.Figures.CapacityMinutes == null);
        fc.Sources.Select(s => (s.Name, s.Figures.ConfirmedMinutes, s.Figures.TentativeMinutes, s.Figures.UnscheduledMinutes)).Should().BeEquivalentTo([("Team boards", 3360, 600, 960), ("Autotask", 240, 0, 240)]);
        (fc.SeesOthers, fc.CanExport, fc.CanSeeHealth).Should().Be((true, true, false));
        fc.Notes.Should().Contain(n => n.StartsWith("5 open work items have no estimate")).And.Contain(n => n.Contains("Nothing is predicted statistically"));

        // Every figure opens the records that make it, and the list's total is the figure.
        foreach (var (kind, rows, minutes) in new[] { (ForecastWorkKind.Confirmed, 15, 3600), (ForecastWorkKind.Tentative, 3, 600), (ForecastWorkKind.Unscheduled, 2, 1200), (ForecastWorkKind.Unestimated, 5, 0) })
        {
            var list = await admin.ForecastWorkAsync(w.Admin.Id, NextWeek, kind, 0, 50);
            (list.Total, list.TotalMinutes).Should().Be((rows, minutes), kind.ToString());
        }
        var page = await admin.ForecastWorkAsync(w.Admin.Id, NextWeek, ForecastWorkKind.Confirmed, 10, 3);
        (page.Rows.Count, page.Total, page.TotalMinutes, page.Skip, page.Take).Should().Be((3, 15, 3600, 10, 3), "paged on the server; the totals are the whole set's");
        var vault = (await admin.ForecastWorkAsync(w.Admin.Id, NextWeek, ForecastWorkKind.Unscheduled, 0, 50)).Rows.Single(r => r.Title == "Vault audit");
        (vault.Kind, vault.PersonName, vault.Minutes, vault.RequiredMinutes, vault.AllocatedMinutes, vault.RemainingMinutes, vault.ClientName, vault.Source, vault.TicketVisible)
            .Should().Be(("ticket", "Sam Shah", 960, 960, 0, 960, "ABC Company", "Team boards", true));
    }

    [Fact]
    public async Task Fixture_B_demand_of_115h_against_100h_is_a_shortage_of_15h_said_with_its_numbers()
    {
        var w = await HundredHourWorldAsync();
        // Confirmed 80 h: Jason all week (40 h), Abbie Mon-Thu (32 h), Sam Monday and Tuesday mornings (8 h).
        await HalfDays(w, w.JasonsTicket, w.Jason, Monday, 10);
        await HalfDays(w, w.JasonsTicket, w.Abbie, Monday, 8);
        await HalfDays(w, w.SamsTicket, w.Sam, Monday, 2, morningsOnly: true);
        // Tentative 10 h: Abbie all Friday, Sam two hours on Wednesday.
        await HalfDays(w, w.JasonsTicket, w.Abbie, Friday, 2, tentative: true);
        await w.As(w.Admin).Plans.CreateAsync(w.Admin.Id, Place(w.SamsTicket, w.Sam, Wednesday, "08:30", "10:30") with { Tentative = true });
        // Estimated and not scheduled: 25 h on Jason's Autotask ticket.
        await Estimate(w, w.Autotask, 1500);

        var fc = await w.As(w.Admin).Analytics.ForecastAsync(w.Admin.Id, NextWeek);

        var t = fc.Totals;
        (t.CapacityMinutes, t.ConfirmedMinutes, t.TentativeMinutes, t.UnscheduledMinutes, t.ProjectedMinutes, t.ConfirmedRemainingMinutes, t.GapMinutes, t.ProjectedPercent)
            .Should().Be((6000, 4800, 600, 1500, 6900, 1200, -900, 115d), "115 h projected against 100 h: 15 h short");
        var shortage = fc.Attention.Single(a => a.Key == "capacity");
        (shortage.Severity, shortage.Title).Should().Be((InsightSeverity.Attention, "Projected demand is 15h over capacity in this window."));
        shortage.Facts.Select(x => (x.Label, x.Value)).Should().Contain([("Capacity", "100h"), ("Confirmed", "80h"), ("Tentative", "10h"), ("Estimated unscheduled", "25h"), ("Projected", "115h"), ("Gap", "−15h"), ("Projected load", "115%")]);
        // The team and the person behind it are named as scheduling conditions, never as a judgement.
        var noc = fc.Attention.Single(a => a.Key == "team:" + w.Noc.Id);
        (noc.Severity, noc.Title, noc.TargetKind, noc.TargetId).Should().Be((InsightSeverity.Critical, "NOC is projected 25h over capacity.", "team", w.Noc.Id.ToString()));
        var over = fc.Attention.Single(a => a.Key == "people-over");
        over.Title.Should().Be("1 person is projected over 100% of their capacity.");
        over.Rule.Should().Contain("A scheduling condition, not a judgement of the person.");
        over.Facts.Should().ContainSingle().Which.Should().Be(new InsightFactDto("Jason Carter", "162.5% (65h of 40h)"));
        fc.Attention.Select(a => a.Severity).Should().BeInDescendingOrder("the most severe comes first");
        // Nothing in the answer scores, ranks or labels anyone.
        var json = JsonSerializer.Serialize(fc);
        json.Should().NotContainAny("score", "Score", "rank", "underperform", "idle", "lazy", "AI insight");
    }

    // ---- today from now --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Today_is_counted_from_now_so_the_morning_is_neither_capacity_nor_demand()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "13:30", "15:30"));
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Tuesday, "09:00", "11:00") with { Tentative = true });
        ClockTo(w, At(Monday, "14:00"));

        var jason = w.As(w.Jason).Analytics;
        var fc = await jason.ForecastAsync(w.Jason.Id, new InsightsQuery("next-7"));

        (fc.Window.From, fc.Window.To).Should().Be((Monday, Sunday));
        // Monday from 14:00 leaves 3 h 30 m of the working day; the 09:00 hour is gone and half of the afternoon block is left.
        (fc.Totals.CapacityMinutes, fc.Totals.ConfirmedMinutes, fc.Totals.TentativeMinutes).Should().Be((210 + 4 * 480, 90, 120));
        fc.Daily[0].Should().Be(new ForecastDayDto(Monday, 210, 90, 0, 0));
        fc.Daily[1].Should().Be(new ForecastDayDto(Tuesday, 480, 0, 120, 0));
        fc.Notes.Should().Contain(n => n.StartsWith("Today is counted from now"));
        var confirmed = await jason.ForecastWorkAsync(w.Jason.Id, new InsightsQuery("next-7"), ForecastWorkKind.Confirmed, 0, 50);
        confirmed.Rows.Should().ContainSingle().Which.Should().Match<ForecastWorkRowDto>(r => r.Kind == "allocation" && r.Minutes == 90 && r.Date == Monday && r.At == At(Monday, "13:30") && r.Status == "Planned");
        // His own figures, and nobody else's.
        fc.People.Select(p => p.DisplayName).Should().Equal("Jason Carter");
        (fc.SeesOthers, fc.CanExport, fc.Unassigned, fc.Recurring).Should().Be((false, false, null, null));
        // The Autotask ticket has no estimate and no plan; the board ticket is planned, so its demand is in the plan.
        fc.Totals.UnestimatedItems.Should().Be(1);

        // A window that starts tomorrow has no "from now": it is Phase 7's capacity against demand, figure for figure.
        var ahead = await jason.ForecastAsync(w.Jason.Id, new InsightsQuery("custom", Tuesday, Sunday));
        var phase7 = await jason.OverviewAsync(w.Jason.Id, new AnalyticsQuery("custom", Tuesday, Sunday));
        (ahead.Totals.CapacityMinutes, ahead.Totals.ConfirmedMinutes, ahead.Totals.TentativeMinutes).Should().Be((phase7.Demand.AvailableMinutes, phase7.Demand.ConfirmedMinutes, phase7.Demand.TentativeMinutes));
        ahead.Notes.Should().NotContain(n => n.StartsWith("Today is counted from now"));
    }

    // ---- scope, teams, unheld work, tenants --------------------------------------------------------------------------

    [Fact]
    public async Task A_forecast_reaches_the_callers_people_and_teams_and_nothing_of_another_team_or_organization()
    {
        var w = await WorldAsync();
        var security = await w.Db.Teams.AsNoTracking().SingleAsync(x => x.Name == "Security");
        // Unheld work routed to a team: five hours for NOC, ten for Security. Two hours of Jason's own.
        var nas = await w.Db.Tickets.SingleAsync(x => x.Id == w.OpenTicket.Id);
        nas.AssignedTeamId = w.Noc.Id;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await Estimate(w, w.OpenTicket, 300);
        await Estimate(w, await OpenWork(w, "Security sweep", null, team: security.Id), 600);
        await Estimate(w, w.Autotask, 120);

        // The lead reaches NOC: its people, its unheld work, and nothing routed to Security.
        var lead = w.As(w.Lead).Analytics;
        var mine = await lead.ForecastAsync(w.Lead.Id, NextWeek);
        mine.People.Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Jason Carter", "Lena Lead");
        (mine.Totals.CapacityMinutes, mine.Totals.UnscheduledMinutes, mine.Totals.UnscheduledItems).Should().Be((4800, 420, 2));
        mine.Unassigned.Should().Match<ForecastFiguresDto>(u => u.UnscheduledMinutes == 300 && u.UnscheduledItems == 1 && u.CapacityMinutes == null);
        mine.Teams.Select(g => (g.Name, g.Figures.UnscheduledMinutes)).Should().Equal(("NOC", 420));
        mine.People.Sum(p => p.Figures.UnscheduledMinutes).Should().Be(120, "work nobody holds is in no person's row");
        mine.Notes.Should().Contain(n => n.StartsWith("Work routed to a team that nobody holds yet"));
        var rows = (await lead.ForecastWorkAsync(w.Lead.Id, NextWeek, ForecastWorkKind.Unscheduled, 0, 50)).Rows;
        rows.Select(r => (r.Reference, r.PersonName, r.TeamName, r.Minutes)).Should().BeEquivalentTo([("INT-000002", (string?)null, "NOC", (int?)300), ("Autotask 43829", "Jason Carter", null, 120)]);
        JsonSerializer.Serialize(mine).Should().NotContainAny("Security sweep", "Sam Shah", "Security");
        (await ((Func<Task>)(() => lead.ForecastAsync(w.Lead.Id, NextWeek with { AppUserId = w.Sam.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        var notMine = await lead.ForecastAsync(w.Lead.Id, NextWeek with { TeamId = security.Id });
        (notMine.People.Count, notMine.Totals.CapacityMinutes, notMine.Totals.UnscheduledMinutes, notMine.Unassigned).Should().Be((0, null, 0, null), "a team outside the scope is nobody and nothing");

        // The administrator sees both teams; a team filter narrows people and unheld work alike; one person has no unheld work.
        var admin = w.As(w.Admin).Analytics;
        var all = await admin.ForecastAsync(w.Admin.Id, NextWeek);
        (all.Totals.UnscheduledMinutes, all.Unassigned!.UnscheduledMinutes).Should().Be((1020, 900));
        all.Teams.Select(g => (g.Name, g.Figures.CapacityMinutes, g.Figures.UnscheduledMinutes)).Should().Equal(("NOC", 4800, 420), ("Security", 2400, 600));
        var nocOnly = await admin.ForecastAsync(w.Admin.Id, NextWeek with { TeamId = w.Noc.Id });
        (nocOnly.People.Count, nocOnly.Totals.UnscheduledMinutes, nocOnly.Unassigned!.UnscheduledMinutes).Should().Be((3, 420, 300));
        nocOnly.Teams.Select(g => g.Name).Should().Equal("NOC");
        var onlyJason = await admin.ForecastAsync(w.Admin.Id, NextWeek with { AppUserId = w.Jason.Id });
        (onlyJason.People.Count, onlyJason.Totals.UnscheduledMinutes, onlyJason.Unassigned).Should().Be((1, 120, null));
        // A forged team: nobody. A forged or foreign client or person: "not found", never a hint.
        (await admin.ForecastAsync(w.Admin.Id, NextWeek with { TeamId = Guid.NewGuid() })).Should().Match<ForecastDto>(f => f.People.Count == 0 && f.Totals.ProjectedMinutes == 0 && f.Unassigned == null);
        (await ((Func<Task>)(() => admin.ForecastAsync(w.Admin.Id, NextWeek with { ClientId = Guid.NewGuid() }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Client was not found.");
        (await ((Func<Task>)(() => admin.ForecastAsync(w.Admin.Id, NextWeek with { AppUserId = w.Outsider.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");

        // A technician: themselves. No unheld work, no one else by any filter.
        var jason = w.As(w.Jason).Analytics;
        var own = await jason.ForecastAsync(w.Jason.Id, NextWeek);
        (own.People.Select(p => p.DisplayName).Single(), own.Totals.UnscheduledMinutes, own.Unassigned, own.SeesOthers).Should().Be(("Jason Carter", 120, null, false));
        (await ((Func<Task>)(() => jason.ForecastAsync(w.Jason.Id, NextWeek with { AppUserId = w.Abbie.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        (await ((Func<Task>)(() => jason.TrendsAsync(w.Jason.Id, new InsightsQuery(AppUserId: w.Abbie.Id)))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        (await jason.ForecastAsync(w.Jason.Id, NextWeek with { TeamId = w.Noc.Id })).People.Select(p => p.DisplayName).Should().Equal(["Jason Carter"], "a team filter never widens the scope");

        // Another organization, with its own database context as every request has: its own person, and none of this work.
        var outsider = World.For(AdminHarness.Create(OrgB, w.DbName).Db, OrgB, w.Outsider, w.Clock).Analytics;
        var elsewhere = await outsider.ForecastAsync(w.Outsider.Id, NextWeek);
        elsewhere.People.Select(p => p.DisplayName).Should().Equal("Other Org");
        (elsewhere.Totals.ProjectedMinutes, elsewhere.Totals.UnestimatedItems, elsewhere.Teams.Count, elsewhere.Clients.Count, elsewhere.Unassigned).Should().Be((0, 0, 0, 0, null));
        (await outsider.ForecastWorkAsync(w.Outsider.Id, NextWeek, ForecastWorkKind.Unscheduled, 0, 50)).Total.Should().Be(0);
        (await ((Func<Task>)(() => outsider.ForecastAsync(w.Outsider.Id, NextWeek with { ClientId = w.JasonsTicket.ClientCompanyId }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Client was not found.");
        (await ((Func<Task>)(() => outsider.ForecastAsync(w.Outsider.Id, NextWeek with { AppUserId = w.Jason.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        (await outsider.TrendsAsync(w.Outsider.Id, new InsightsQuery())).Clients.Should().BeEmpty();
    }

    // ---- skills, eligibility, work at risk ----------------------------------------------------------------------------

    [Fact]
    public async Task Skill_capacity_work_at_risk_overdue_work_and_the_planning_window_are_read_from_records()
    {
        var w = await WorldAsync();
        var sonic = new Skill { MspOrganizationId = OrgA, Name = "SonicWall", NormalizedName = "SONICWALL", IsActive = true };
        var azure = new Skill { MspOrganizationId = OrgA, Name = "Azure", NormalizedName = "AZURE", IsActive = true };
        w.Db.AddRange(sonic, azure, new StaffSkill { MspOrganizationId = OrgA, AppUserId = w.Abbie.Id, SkillId = sonic.Id, Level = SkillLevel.Basic });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        // Ten hours that ask for SonicWall (Abbie holds it); five that ask for Azure (nobody does).
        await Estimate(w, w.JasonsTicket, 600, sonic.Id);
        await Estimate(w, w.Autotask, 300, azure.Id);
        // Abbie has four hours pencilled in on Monday: her free time is what is left after confirmed and tentative work.
        await w.As(w.Admin).Plans.CreateAsync(w.Admin.Id, Place(w.JasonsTicket, w.Abbie, Monday, "08:30", "12:30") with { Tentative = true });
        // A project that may not start for a fortnight is not this week's demand.
        await Estimate(w, await OpenWork(w, "Later project", w.Abbie), 480, earliest: At(Monday.AddDays(14), "08:30"), latest: At(Monday.AddDays(20), "17:30"));
        // Sam: 25 h due Tuesday evening with two working days before it; an hour that is already overdue; and paused work that is neither.
        await Estimate(w, await OpenWork(w, "Due soon", w.Sam, due: At(Tuesday, "17:00")), 1500);
        await Estimate(w, await OpenWork(w, "Late", w.Sam, due: At(Today.AddDays(-1), "10:00")), 60);
        await OpenWork(w, "On hold", w.Sam, due: At(Today.AddDays(-1), "10:00"), paused: true);

        var admin = w.As(w.Admin).Analytics;
        var fc = await admin.ForecastAsync(w.Admin.Id, NextWeek);

        // The board ticket: 600 less the 240 pencilled in. Tentative time is demand already, so it is not counted twice.
        (fc.Totals.TentativeMinutes, fc.Totals.UnscheduledMinutes, fc.Totals.UnscheduledItems).Should().Be((240, 360 + 300 + 1500 + 60, 4), "the project that cannot start yet is not in it");
        fc.Totals.UnestimatedItems.Should().Be(2, "Sam's PSA ticket and the paused one");
        fc.Skills.Should().BeEquivalentTo(
        [
            new SkillCapacityDto(azure.Id, "Azure", 300, 1, 0, 0, -300, []),
            new SkillCapacityDto(sonic.Id, "SonicWall", 360, 1, 1, 2400 - 240, 2400 - 240 - 360, ["Abbie Noor"]),
        ], o => o.WithStrictOrdering());
        var gap = fc.Attention.Single(a => a.Key == "skill:" + azure.Id);
        (gap.Severity, gap.Title, gap.List, gap.TargetKind, gap.TargetId).Should().Be((InsightSeverity.Attention, "Azure: demand is 5h above the free time of the 0 people who hold it.", "skill", "skill", azure.Id.ToString()));
        fc.Attention.Should().NotContain(a => a.Key == "skill:" + sonic.Id);
        fc.Notes.Should().Contain(n => n.Contains("Anyone holding a skill counts, at any level"));
        var skilled = await admin.ForecastWorkAsync(w.Admin.Id, NextWeek with { SkillId = sonic.Id }, ForecastWorkKind.Skill, 0, 50);
        skilled.Rows.Should().ContainSingle().Which.Should().Match<ForecastWorkRowDto>(r => r.Reference == "INT-000001" && r.SkillName == "SonicWall" && r.Minutes == 360 && r.RequiredMinutes == 600 && r.AllocatedMinutes == 240);
        (await ((Func<Task>)(() => admin.ForecastWorkAsync(w.Admin.Id, NextWeek with { SkillId = Guid.NewGuid() }, ForecastWorkKind.Skill, 0, 50))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Skill was not found.");

        // Due work: one item cannot fit before its due date, one is past it; the paused one is neither.
        fc.AtRisk.Should().Be(new WorkAtRiskDto(1, 1, 1));
        var risk = (await admin.ForecastWorkAsync(w.Admin.Id, NextWeek, ForecastWorkKind.AtRisk, 0, 50)).Rows.Should().ContainSingle().Subject;
        (risk.Title, risk.Risk, risk.Minutes, risk.FreeBeforeDueMinutes, risk.DueAt, risk.PersonName).Should().Be(("Due soon", "Not enough free time before the due date", 1500, 960, At(Tuesday, "17:00"), "Sam Shah"));
        var late = (await admin.ForecastWorkAsync(w.Admin.Id, NextWeek, ForecastWorkKind.Overdue, 0, 50)).Rows.Should().ContainSingle().Subject;
        (late.Title, late.Risk, late.Minutes).Should().Be(("Late", "Overdue", 60));
        fc.Attention.Single(a => a.Key == "at-risk").Title.Should().Be("1 work item does not have enough free time before its due date.");
        fc.Attention.Single(a => a.Key == "overdue").Title.Should().Be("1 open work item is past its due date.");
        // Unscheduled effort is shown on the day its work is due, never spread: Tuesday has 25 h, the overdue hour is its own figure.
        fc.Daily.Single(d => d.Date == Tuesday).UnscheduledDueMinutes.Should().Be(1500);
        fc.Daily.Where(d => d.Date != Tuesday).Should().OnlyContain(d => d.UnscheduledDueMinutes == 0);
        (fc.UnscheduledOverdueMinutes, fc.UnscheduledNoDateMinutes).Should().Be((60, 660));
        (fc.Daily.Sum(d => d.UnscheduledDueMinutes) + fc.UnscheduledOverdueMinutes + fc.UnscheduledNoDateMinutes).Should().Be(fc.Totals.UnscheduledMinutes);
        // Coverage counts every estimated open item, the later project included: 240 of 2940 minutes have time allocated.
        fc.Coverage.Should().Match<ScheduleCoverageDto>(c => c.EstimatedItems == 5 && c.EstimatedMinutes == 2940 && c.ScheduledMinutes == 240 && c.UnscheduledMinutes == 2700 && c.CoveragePercent == 8.16d);
        fc.DataQuality.EstimatedWithSkill.Should().Be(2);

        // A window long enough for the project to start includes it.
        var longer = await admin.ForecastAsync(w.Admin.Id, new InsightsQuery("custom", Monday, Monday.AddDays(20)));
        longer.Totals.UnscheduledMinutes.Should().Be(fc.Totals.UnscheduledMinutes + 480);
    }

    [Fact]
    public async Task Work_the_caller_cannot_open_counts_in_the_forecast_and_says_nothing_about_itself()
    {
        var w = await WorldAsync();
        // Jason is planned on Sam's PSA ticket, which his ticket scope (assigned) cannot open.
        w.Db.WorkAllocations.Add(new WorkAllocation
        {
            MspOrganizationId = OrgA, TicketId = w.SamsTicket.Id, AppUserId = w.Jason.Id, StartsAt = At(Monday, "09:00"), EndsAt = At(Monday, "11:00"), PlannedMinutes = 120,
            Status = WorkAllocationStatus.Planned, Method = SchedulingMethod.AuthorizedUser, ScheduledByUserId = w.Lead.Id,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await w.As(w.Lead).Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Tuesday, "09:00", "10:00"));

        var jason = w.As(w.Jason).Analytics;
        var fc = await jason.ForecastAsync(w.Jason.Id, NextWeek);
        fc.Totals.ConfirmedMinutes.Should().Be(180, "the time counts");
        fc.Clients.Select(c => (c.Key, c.Name, c.Figures.ConfirmedMinutes)).Should().BeEquivalentTo([("hidden", WorkforceAnalyticsService.HiddenWork, 120), (w.JasonsTicket.ClientCompanyId!.Value.ToString(), "ABC Company", 60)]);
        fc.Sources.Select(s => (s.Name, s.Figures.ConfirmedMinutes, s.Figures.UnestimatedItems)).Should().BeEquivalentTo([("Team boards", 60, 0), (WorkforceAnalyticsService.HiddenWork, 120, 0), ("Autotask", 0, 1)],
            "his own Autotask ticket, unsized and unplanned, is a count under its source");
        fc.Notes.Should().Contain(n => n.StartsWith("Some demand is on work you cannot open"));
        var hidden = (await jason.ForecastWorkAsync(w.Jason.Id, NextWeek, ForecastWorkKind.Confirmed, 0, 50)).Rows.Single(r => r.TicketId == w.SamsTicket.Id);
        (hidden.Reference, hidden.Title, hidden.ClientName, hidden.Priority, hidden.TicketVisible, hidden.DueAt, hidden.Minutes).Should().Be((WorkforceAnalyticsService.HiddenWork, null, null, null, false, null, 120));
        JsonSerializer.Serialize(fc).Should().NotContainAny("Rotate the vault keys", "Autotask 777");
        // Filtering by the client does not reveal that the hidden ticket is theirs.
        (await jason.ForecastAsync(w.Jason.Id, NextWeek with { ClientId = w.JasonsTicket.ClientCompanyId })).Totals.ConfirmedMinutes.Should().Be(60);
        // Someone who may open it sees it as itself.
        var seen = (await w.As(w.Admin).Analytics.ForecastWorkAsync(w.Admin.Id, NextWeek, ForecastWorkKind.Confirmed, 0, 50)).Rows.Single(r => r.TicketId == w.SamsTicket.Id);
        (seen.Reference, seen.Title, seen.TicketVisible).Should().Be(("Autotask 777", "Rotate the vault keys", true));
    }

    // ---- history: comparison, weeks, estimate variance ----------------------------------------------------------------

    [Fact]
    public async Task A_period_is_compared_with_the_one_before_and_a_percentage_never_hides_an_empty_denominator()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var firewall = await w.Db.Tickets.SingleAsync(x => x.Id == w.JasonsTicket.Id);
        firewall.PortalCategory = "Firewall";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var nextMonday = Monday.AddDays(7);
        // The week of 5 Jan: two hours planned and worked, one reactive hour, two tickets finished.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "11:00"));
        await Log(w, w.Jason, w.JasonsTicket, Monday, "09:00", 2m);
        await Log(w, w.Jason, w.Autotask, Tuesday, "10:00", 1m);
        await Finish(w, "Done before 1", w.Jason, At(Tuesday, "16:00"));
        await Finish(w, "Done before 2", w.Jason, At(Tuesday, "16:30"));
        // The week of 12 Jan: one hour planned and ninety minutes worked on it, three reactive hours, an hour of Abbie's, one ticket finished.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, nextMonday, "09:00", "10:00"));
        await Log(w, w.Jason, w.JasonsTicket, nextMonday, "09:00", 1.5m);
        await Log(w, w.Jason, w.Autotask, nextMonday.AddDays(1), "10:00", 3m);
        await Log(w, w.Abbie, w.OpenTicket, nextMonday.AddDays(2), "10:00", 1m);
        await Finish(w, "Done after", w.Jason, At(nextMonday.AddDays(2), "15:00"));
        ClockTo(w, At(nextMonday.AddDays(7), "09:00"));

        var admin = w.As(w.Admin).Analytics;
        var tr = await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week"));

        (tr.Current.From, tr.Current.To, tr.Previous.From, tr.Previous.To).Should().Be((nextMonday, nextMonday.AddDays(6), Monday, Sunday));
        tr.Totals.Select(x => (x.Key, x.Unit, x.Current, x.Previous, x.Change, x.ChangePercent)).Should().Equal(
            ("actual", "seconds", 19800d, 10800d, 9000d, 83.33d),
            ("planned-actual", "seconds", 5400d, 7200d, -1800d, -25d),
            ("reactive", "seconds", 14400d, 3600d, 10800d, 300d),
            ("reactive-share", "percent", 72.73d, 33.33d, 39.4d, null),
            ("planned", "minutes", 60d, 120d, -60d, -50d),
            ("completed", "count", 1d, 2d, -1d, -50d),
            ("work-items", "count", 3d, 2d, 1d, 50d));
        // The same figures as the Phase 7 dashboard gives for each period: one definition of actual, planned and reactive.
        var phase7 = await admin.OverviewAsync(w.Admin.Id, new AnalyticsQuery("custom", nextMonday, nextMonday.AddDays(6)));
        (phase7.Totals.ActualSeconds, phase7.Totals.ReactiveActualSeconds, phase7.Totals.CompletedWork, phase7.Totals.ReactiveSharePercent).Should().Be((19800, 14400, 1, 72.73d));

        // Eight weeks, Monday to Sunday; the one in progress is marked.
        tr.Weeks.Should().HaveCount(8);
        (tr.Weeks[0].From, tr.Weeks[7].From, tr.Weeks[7].To).Should().Be((nextMonday.AddDays(-42), nextMonday.AddDays(7), nextMonday.AddDays(13)));
        tr.Weeks.Select(x => x.Partial).Should().Equal(false, false, false, false, false, false, false, true);
        tr.Weeks[5].Should().Match<WeekTrendDto>(x => x.From == Monday && x.ActualSeconds == 10800 && x.ReactiveSeconds == 3600 && x.ReactiveSharePercent == 33.33d && x.Completed == 2);
        tr.Weeks[6].Should().Match<WeekTrendDto>(x => x.From == nextMonday && x.ActualSeconds == 19800 && x.PlannedActualSeconds == 5400 && x.Completed == 1 && x.WorkItems == 3);
        tr.Weeks[0].Should().Match<WeekTrendDto>(x => x.ActualSeconds == 0 && x.ReactiveSharePercent == null, "no work: no share, not 0%");

        tr.Clients.Should().ContainSingle().Which.Should().Be(new GroupComparisonDto(w.JasonsTicket.ClientCompanyId!.Value.ToString(), "ABC Company", 19800, 10800, 9000, 83.33d, 14400, 1, 2, 3));
        tr.Sources.Select(s => (s.Name, s.CurrentSeconds, s.PreviousSeconds, s.ChangeSeconds, s.ChangePercent)).Should().Equal(("Autotask", 10800L, 3600L, 7200L, (double?)200d), ("Team boards", 9000L, 7200L, 1800L, 25d));

        // Estimate variance: an hour planned, ninety minutes recorded on it. By kind of work, never by person.
        tr.ByCategory.Should().ContainSingle().Which.Should().Be(new EstimateVarianceRowDto("Firewall", "Firewall", 60, 90, 30, 50d, 30, 50d, 1));
        tr.ByClient.Should().ContainSingle().Which.Should().Match<EstimateVarianceRowDto>(r => r.Name == "ABC Company" && r.PlannedMinutes == 60 && r.ActualMinutes == 90);
        tr.BySource.Should().ContainSingle().Which.Name.Should().Be("Team boards");
        typeof(EstimateVarianceRowDto).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Person") || n.Contains("User") || n.Contains("Technician"));

        // The share of reactive work moved by more than five points: said once, with both values, and not called a fault.
        var moved = tr.Attention.Should().ContainSingle().Subject;
        (moved.Key, moved.Severity, moved.Title).Should().Be(("reactive-share", InsightSeverity.Watch, "Reactive work share went from 33.3% to 72.7% against the previous period (+39.4 points)."));
        moved.Rule.Should().Contain("Reactive work is not a fault");
        moved.Facts.Select(x => x.Value).Should().Equal("72.7% (4h of 5h 30m)", "33.3% (1h of 3h)");

        // Nothing before, something now: both values, and no percentage.
        var abbie = await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week", AppUserId: w.Abbie.Id));
        abbie.Totals.Single(x => x.Key == "actual").Should().Be(new ComparisonDto("actual", "Actual work", "seconds", 3600, 0, 3600, null));
        abbie.Totals.Single(x => x.Key == "reactive-share").Should().Match<ComparisonDto>(x => x.Current == 100 && x.Previous == null && x.Change == null && x.ChangePercent == null);
        abbie.Clients.Single().ChangePercent.Should().BeNull();
        abbie.Attention.Should().BeEmpty("a share that did not exist before cannot have moved");
        // Nothing in either: zeros and no percentages, never a division by zero.
        var sam = await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week", AppUserId: w.Sam.Id));
        sam.Totals.Where(x => x.Unit != "percent").Should().OnlyContain(x => x.Current == 0 && x.Previous == 0 && x.Change == 0 && x.ChangePercent == null);
        (sam.Clients.Count, sam.ByCategory.Count, sam.Weeks.Count).Should().Be((0, 0, 8));

        // The lead's comparison is NOC's: Abbie and Jason, as in the dashboard.
        var leads = await lead.Analytics.TrendsAsync(w.Lead.Id, new InsightsQuery(Compare: "last-week"));
        leads.Totals.Single(x => x.Key == "actual").Current.Should().Be(19800);
        ((Func<Task>)(() => admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "since-forever")))).Should().ThrowAsync<ValidationFailedException>();
    }

    // ---- quality signals -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Quality_signals_come_with_what_they_rest_on_and_say_so_when_there_is_no_data()
    {
        var w = await WorldAsync();
        var week = Monday.AddDays(7);
        async Task<Ticket> Done(string title, AppUser who, DateTimeOffset at, DateTimeOffset? due = null, int reopened = 0, bool reviewed = false, int sentBack = 0,
            DateTimeOffset? replyDue = null, DateTimeOffset? replied = null, bool psa = false, int? rating = null)
        {
            var t = await OpenWork(w, title, who, due: due, psa: psa, status: "CLOSED");
            var row = await w.Db.Tickets.SingleAsync(x => x.Id == t.Id);
            (row.ResolvedAt, row.ClosedAt, row.ResolvedByAppUserId, row.ReopenCount, row.ReviewedAt, row.ReviewSendBacks, row.FirstResponseDueAt, row.FirstRespondedAt)
                = (at, at, who.Id, reopened, reviewed ? at : null, sentBack, replyDue, replied);
            if (rating is { } stars) w.Db.TicketSatisfactions.Add(new TicketSatisfaction { MspOrganizationId = OrgA, TicketId = t.Id, ClientUserId = Guid.NewGuid(), Rating = stars, RatedAt = at });
            await w.Db.SaveChangesAsync();
            w.Db.ChangeTracker.Clear();
            return t;
        }
        // The week of 12 Jan, credited to Jason: on time and reviewed; late, reopened and sent back; a reply promise kept; two PSA tickets, rated 5 and 2.
        await Done("A on time", w.Jason, At(week.AddDays(2), "15:00"), due: At(week.AddDays(2), "17:00"), reviewed: true);
        await Done("B late", w.Jason, At(week.AddDays(2), "10:00"), due: At(week.AddDays(1), "12:00"), reopened: 1, reviewed: true, sentBack: 1);
        await Done("C answered", w.Jason, At(week, "16:00"), replyDue: At(week, "10:00"), replied: At(week, "09:30"));
        await Done("D rated well", w.Jason, At(week.AddDays(3), "12:00"), due: At(week.AddDays(3), "17:00"), psa: true, rating: 5);
        await Done("E rated badly", w.Jason, At(week.AddDays(3), "13:00"), psa: true, rating: 2);
        // One of Abbie's, on time. And one the week before, on time.
        await Done("G Abbie's", w.Abbie, At(week.AddDays(4), "11:00"), due: At(week.AddDays(4), "17:00"));
        await Done("F before", w.Jason, At(Tuesday, "10:00"), due: At(Tuesday, "17:00"));
        ClockTo(w, At(week.AddDays(7), "09:00"));

        var admin = w.As(w.Admin).Analytics;
        var q = (await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week"))).Quality;

        q.Select(s => s.Key).Should().Equal("due-date-met", "first-response-met", "reopened", "satisfaction", "review-first-time", "escalated", "repeat-issues");
        QualitySignalDto S(string key) => q.Single(s => s.Key == key);
        q.Should().OnlyContain(s => s.Population == 6, "the same completed work as the dashboard's Completed card");
        (S("due-date-met").Met, S("due-date-met").Eligible, S("due-date-met").Percent, S("due-date-met").Quality).Should().Be((3, 4, 75d, DataQuality.Partial));
        S("due-date-met").QualityReason.Should().StartWith("4 of 6 completed work items carry a due date.");
        (S("due-date-met").PreviousMet, S("due-date-met").PreviousEligible, S("due-date-met").PreviousPercent).Should().Be((1, 1, 100d));
        (S("first-response-met").Met, S("first-response-met").Eligible, S("first-response-met").Percent, S("first-response-met").Quality).Should().Be((1, 1, 100d, DataQuality.Partial));
        (S("first-response-met").PreviousEligible, S("first-response-met").PreviousPercent).Should().Be((0, null), "no promise the week before: no percentage");
        (S("reopened").Met, S("reopened").Eligible, S("reopened").Percent, S("reopened").Quality).Should().Be((1, 6, 16.67d, DataQuality.Partial));
        S("reopened").QualityReason.Should().Contain("A ticket reopened in the PSA itself is not counted");
        (S("satisfaction").Met, S("satisfaction").Eligible, S("satisfaction").Percent, S("satisfaction").Quality).Should().Be((1, 2, 50d, DataQuality.High));
        (S("review-first-time").Met, S("review-first-time").Eligible, S("review-first-time").Percent, S("review-first-time").Quality).Should().Be((1, 2, 50d, DataQuality.Partial));
        // No source in the records: not available, and not estimated from something else.
        foreach (var key in new[] { "escalated", "repeat-issues" })
            S(key).Should().Match<QualitySignalDto>(s => s.Quality == DataQuality.NotAvailable && s.Met == null && s.Eligible == null && s.Percent == null && s.PreviousPercent == null);
        S("escalated").QualityReason.Should().Be("No escalation is recorded anywhere: a reassignment is not an escalation, and none is inferred from one.");

        // Board work only: every reopen is seen, and nobody rated anything.
        var boards = (await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week", Source: "internal"))).Quality;
        boards.Single(s => s.Key == "reopened").Should().Match<QualitySignalDto>(s => s.Quality == DataQuality.High && s.Met == 1 && s.Eligible == 4 && s.Percent == 25d);
        boards.Single(s => s.Key == "satisfaction").Should().Match<QualitySignalDto>(s => s.Quality == DataQuality.NotAvailable && s.Percent == null && s.QualityReason == "No completed work has a client rating.");
        // Abbie's one ticket has a due date: complete data.
        var abbies = (await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week", AppUserId: w.Abbie.Id))).Quality;
        abbies.Single(s => s.Key == "due-date-met").Should().Match<QualitySignalDto>(s => s.Quality == DataQuality.High && s.Met == 1 && s.Eligible == 1 && s.Percent == 100d && s.Population == 1);
        // Nothing completed: nothing is available, and no figure is a zero that looks like a result.
        var sams = (await admin.TrendsAsync(w.Admin.Id, new InsightsQuery(Compare: "last-week", AppUserId: w.Sam.Id))).Quality;
        sams.Should().OnlyContain(s => s.Quality == DataQuality.NotAvailable && s.Percent == null && s.Met == null && s.Population == 0);
        sams.Single(s => s.Key == "reopened").QualityReason.Should().Be("No work was completed in the period.");
        // A signal is about work. There is no person in it.
        typeof(QualitySignalDto).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Person") || n.Contains("User") || n.Contains("Technician") || n.Contains("Score"));
    }

    // ---- mapping and integration health ------------------------------------------------------------------------------

    [Fact]
    public async Task Integration_health_reports_mapping_and_sync_state_without_a_secret_and_needs_its_own_permission()
    {
        var w = await WorldAsync();
        var conn = w.Autotask.PsaConnectionId!.Value;
        var now = w.Clock.GetUtcNow();
        var placeholder = new ClientCompany { MspOrganizationId = OrgA, Name = "Unknown company", ExternalCompanyId = "unknown", PsaConnectionId = conn };
        w.Db.AddRange(
            new PsaConnection
            {
                Id = conn, MspOrganizationId = OrgA, Name = "Autotask Main", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://webservices.secret.example/atservicesrest", CredentialSecretRef = "secret-ref-123",
                Status = ConnectionStatus.Degraded, IsEnabled = true, LastSuccessfulSyncAt = now.AddDays(-3), LastHealthCheckAt = now.AddMinutes(-5), LastError = "401 Unauthorized: token abc123", DefaultTimeEntryResourceId = "29682885",
            },
            new PsaConnection
            {
                MspOrganizationId = OrgA, Name = "ConnectWise", Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://cw.secret.example", CredentialSecretRef = "secret-ref-456",
                Status = ConnectionStatus.Healthy, IsEnabled = true, LastSuccessfulSyncAt = now.AddHours(-1),
            },
            placeholder,
            new UserPsaIdentity { MspOrganizationId = OrgA, AppUserId = w.Jason.Id, PsaConnectionId = conn, ExternalTechnicianId = "tech-1" });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        // A status and a priority no rule maps, held by a linked login, in sync error; one held by an unlinked login on a placeholder client; one held by the integration account.
        await OpenWork(w, "Unmapped", null, psa: true, status: "Waiting Vendor", priority: "P1", login: "tech-1", sync: TicketSyncStatus.Error);
        await OpenWork(w, "Placeholder client", null, psa: true, status: "NEW", priority: "HIGH", login: "tech-2", client: placeholder.Id);
        await OpenWork(w, "Integration's", null, psa: true, login: "29682885");
        await Log(w, w.Jason, w.Autotask, Today, "09:00", 1m, sync: TimeEntrySyncStatus.Failed);
        await Log(w, w.Jason, w.Autotask, Today, "10:00", 1m, sync: TimeEntrySyncStatus.Pending);
        await Log(w, w.Jason, w.JasonsTicket, Today, "11:00", 1m, sync: TimeEntrySyncStatus.Failed);

        // Without the permission: refused, and the forecast carries no sync statement.
        (await ((Func<Task>)(() => w.As(w.Admin).Analytics.HealthAsync(w.Admin.Id))).Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Contain("integration.health.view");
        var without = await w.As(w.Admin).Analytics.ForecastAsync(w.Admin.Id, NextWeek);
        (without.CanSeeHealth, without.Attention.Any(a => a.Key.StartsWith("sync:"))).Should().Be((false, false));
        // Failed pushes of the people in scope are everyone's to know: the time is here and not in the PSA.
        without.Attention.Single(a => a.Key == "time-not-in-psa").Title.Should().Be("2 time entries are not in the PSA: the push failed.");

        await Grant(w, "Administrator", Permissions.IntegrationHealthView);
        var admin = w.As(w.Admin).Analytics;
        var health = await admin.HealthAsync(w.Admin.Id);

        health.Connections.Select(c => c.Name).Should().Equal("Autotask Main", "ConnectWise");
        var at = health.Connections[0];
        (at.Provider, at.Status, at.IsEnabled, at.HasError, at.Stale, at.Tickets, at.LastSuccessfulSyncAt).Should().Be(("Autotask", "Degraded", true, true, true, 6, now.AddDays(-3)));
        at.StatusMapping.Should().BeEquivalentTo(new MappingCoverageDto(5, 6, 83.33d, ["Waiting Vendor"]));
        at.PriorityMapping.Should().BeEquivalentTo(new MappingCoverageDto(5, 6, 83.33d, ["P1"]));
        at.TechnicianLinks.Should().BeEquivalentTo(new MappingCoverageDto(1, 2, 50d, []), "tech-1 is linked to Jason, tech-2 to nobody; the integration account is not a technician");
        (at.PlaceholderClientTickets, at.TicketsInSyncError, at.TimeEntriesFailed, at.TimeEntriesPending).Should().Be((1, 1, 1, 1));
        health.Connections[1].Should().Match<ConnectionInsightDto>(c => !c.Stale && !c.HasError && c.Tickets == 0 && c.StatusMapping.Percent == null && c.TechnicianLinks.Total == 0);
        health.Attention.Select(a => (a.Key, a.Severity)).Should().Equal(("sync:Autotask Main", InsightSeverity.Attention), ("mapping:Autotask Main", InsightSeverity.Watch));
        health.Attention[0].Title.Should().Be($"Autotask Main has not synced successfully since {now.AddDays(-3):d MMM yyyy HH:mm} UTC.");
        // Nothing that could be used against the PSA leaves: no address, no secret reference, no error text.
        JsonSerializer.Serialize(health).Should().NotContainAny("secret.example", "secret-ref", "abc123", "401", "Unauthorized");
        // With the permission, the forecast says the PSA figures are as old as that sync.
        var with = await admin.ForecastAsync(w.Admin.Id, NextWeek);
        (with.CanSeeHealth, with.Attention.Count(a => a.Key == "sync:Autotask Main")).Should().Be((true, 1));

        // Another organization, holding the permission: none of these connections.
        var dbB = AdminHarness.Create(OrgB, w.DbName).Db;
        var roleB = new Role { MspOrganizationId = OrgB, Name = "Manager B", BuiltInType = RoleType.Manager };
        foreach (var key in new[] { Permissions.ScheduleView, Permissions.IntegrationHealthView, Permissions.WorkforceAnalyticsExport, Permissions.TicketsViewAll })
            roleB.Permissions.Add(new RolePermission { PermissionKey = key, Scope = PermissionScope.All });
        var bea = new AppUser { MspOrganizationId = OrgB, DisplayName = "Bea Manager", Email = "bea@other.test", IsActive = true };
        bea.Roles.Add(new UserRole { RoleId = roleB.Id });
        dbB.AddRange(roleB, bea);
        await dbB.SaveChangesAsync();
        var theirs = World.For(dbB, OrgB, bea, w.Clock).Analytics;
        (await theirs.HealthAsync(bea.Id)).Connections.Should().BeEmpty();
        foreach (var definition in WorkforceAnalyticsService.ReportDefinitions)
            foreach (var format in new[] { "csv", "xlsx" })
            {
                var file = await theirs.ExportReportAsync(bea.Id, definition.Key, new InsightsQuery(), format);
                var text = format == "csv" ? Encoding.UTF8.GetString(file.Content) : SheetXml(file.Content);
                text.Should().NotContainAny("Jason", "Sam Shah", "ABC Company", "NOC", "Autotask Main", "Firewall review", "Waiting Vendor");
            }
    }

    // ---- the report center -------------------------------------------------------------------------------------------

    private static string SheetXml(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Every_report_previews_and_exports_the_same_rows_and_an_export_needs_its_permission_and_is_audited()
    {
        var w = await HundredHourWorldAsync();
        await HalfDays(w, w.JasonsTicket, w.Jason, Monday, 8);
        await Estimate(w, w.Autotask, 240);
        await Log(w, w.Jason, w.JasonsTicket, Today, "09:00", 2m);
        await Finish(w, "Done", w.Jason, At(Today, "08:00"));
        // A person and a ticket named like formulas.
        var abbie = await w.Db.AppUsers.SingleAsync(u => u.Id == w.Abbie.Id);
        abbie.DisplayName = "=HYPERLINK(\"http://evil.test\",\"x\")";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        var admin = w.As(w.Admin).Analytics;
        var lead = w.As(w.Lead).Analytics;

        // The catalogue: eleven reports for someone without integration health, which is not offered and not served.
        var catalogue = await admin.CatalogueAsync(w.Admin.Id);
        catalogue.Select(d => d.Key).Should().Equal("workforce-utilization", "technician-work-summary", "team-work-summary", "capacity-demand", "future-capacity", "client-workload",
            "planned-vs-actual", "reactive-work", "estimate-variance", "work-sources", "operational-quality");
        catalogue.Select(d => d.Category).Distinct().Should().Equal("Workforce", "Capacity", "Clients", "Delivery", "Quality");
        (await ((Func<Task>)(() => admin.ReportAsync(w.Admin.Id, "integration-health", new InsightsQuery()))).Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Contain("integration.health.view");
        // A report is a definition in code: there is no stored report, file or job id to guess. An unknown key is "not found".
        foreach (var key in new[] { "payroll", "", Guid.NewGuid().ToString(), "../workforce-utilization", "future-capacity/export" })
        {
            (await ((Func<Task>)(() => admin.ReportAsync(w.Admin.Id, key, new InsightsQuery()))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Report was not found.");
            (await ((Func<Task>)(() => admin.ExportReportAsync(w.Admin.Id, key, new InsightsQuery(), "csv"))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Report was not found.");
        }

        // The future capacity report is the forecast, figure for figure.
        var fc = await admin.ForecastAsync(w.Admin.Id, NextWeek);
        var report = await admin.ReportAsync(w.Admin.Id, "future-capacity", NextWeek);
        (report.Definition.Title, report.Period, report.TotalRows, report.Truncated, report.CanExport).Should().Be(("Future capacity by technician", fc.Window, 5, false, true));
        report.Summary.Select(x => (x.Label, x.Value)).Should().Contain([("Capacity", "100 h"), ("Confirmed", "32 h"), ("Estimated unscheduled", "4 h in 1 work item"), ("Projected demand", "36 h"), ("Capacity gap", "64 h left"), ("Projected load", "36%")]);
        report.Columns.Select(c => (c.Key, c.Kind)).Should().StartWith([("person", "text"), ("teams", "text"), ("offered", "text"), ("capacity", "hours"), ("confirmed", "hours")]);
        report.Rows.Should().OnlyContain(r => r.Count == report.Columns.Count);
        var jasonsRow = report.Rows.Single(r => (string?)r[0] == "Jason Carter");
        jasonsRow.Skip(3).Should().Equal(40m, 32m, 0m, 4m, 0, 36m, 4m, 90d);
        report.Rows.Sum(r => (decimal?)r[8] ?? 0).Should().Be(fc.Totals.ProjectedMinutes / 60m, "the rows add up to the forecast");
        report.Applied.Should().ContainSingle().Which.Should().Be(new ReportFactDto("Filters", "None: everyone and all the work you may see"));
        (await admin.ReportAsync(w.Admin.Id, "FUTURE-CAPACITY", NextWeek with { TeamId = w.Noc.Id })).Applied.Should().Equal(new ReportFactDto("Team", "NOC"));
        // The utilization report is the Phase 7 dashboard's people, figure for figure.
        var history = new InsightsQuery(Period: "custom", From: Today, To: Sunday);
        var overview = await admin.OverviewAsync(w.Admin.Id, new AnalyticsQuery("custom", Today, Sunday));
        var utilization = await admin.ReportAsync(w.Admin.Id, "workforce-utilization", history);
        utilization.Rows.Select(r => (decimal)r[6]!).Sum().Should().Be(overview.Totals.ActualSeconds / 3600m);
        utilization.Rows.Select(r => (decimal)r[4]!).Sum().Should().Be(overview.Totals.PlannedMinutes / 60m);
        utilization.Notes.Should().BeEquivalentTo(overview.Notes);

        // Export: its own permission, checked in the service too. The lead may preview and may not export.
        (await lead.ReportAsync(w.Lead.Id, "future-capacity", NextWeek)).CanExport.Should().BeFalse();
        (await ((Func<Task>)(() => lead.ExportReportAsync(w.Lead.Id, "future-capacity", NextWeek, "csv"))).Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Contain("workforce.analytics.export");
        (await ((Func<Task>)(() => admin.ExportReportAsync(w.Admin.Id, "future-capacity", NextWeek, "pdf"))).Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Unknown format. Use csv or xlsx.");
        (await ((Func<Task>)(() => admin.ReportAsync(w.Admin.Id, "estimate-variance", new InsightsQuery(By: "technician")))).Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Unknown grouping. Use category, client or source.");
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.report.exported")).Should().Be(0, "a refused export is not an export");

        var csv = await admin.ExportReportAsync(w.Admin.Id, "future-capacity", NextWeek, "csv");
        (csv.FileName, csv.ContentType, csv.Rows).Should().Be(($"pio-manage-future-capacity-{Monday:yyyy-MM-dd}-{Sunday:yyyy-MM-dd}.csv", "text/csv", 5));
        csv.Content.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var text = Encoding.UTF8.GetString(csv.Content, 3, csv.Content.Length - 3);
        text.Should().StartWith("PIO MANAGE: Future capacity by technician\r\nPeriod,Mon 5 Jan – Sun 11 Jan 2026,Asia/Kolkata,2026-01-05,2026-01-11\r\nGenerated,");
        text.Should().Contain("Summary,Projected demand,36 h\r\n").And.Contain("Summary,Capacity gap,64 h left\r\n");
        text.Should().Contain("Technician,Teams,Offered for planned work,Capacity (h),Confirmed (h),Tentative (h),Estimated unscheduled (h),Unestimated work items,Projected demand (h),Capacity gap (h),Projected load %\r\n");
        text.Should().Contain("Jason Carter,NOC,yes,40,32,0,4,0,36,4,90\r\n");
        text.Should().Contain("\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"x\"\")\",NOC,yes,40,0,0,0,0,0,40,0\r\n", "a name that is a formula is neutralised and quoted");
        text.Should().Contain("Lena Lead,NOC,yes,0,0,0,0,0,0,0,N/A\r\n", "no capacity: no percentage, and N/A rather than a zero");

        var xlsx = await admin.ExportReportAsync(w.Admin.Id, "future-capacity", NextWeek, "xlsx");
        (xlsx.FileName, xlsx.ContentType, xlsx.Rows).Should().Be(($"pio-manage-future-capacity-{Monday:yyyy-MM-dd}-{Sunday:yyyy-MM-dd}.xlsx", XlsxWriter.ContentType, 5));
        using (var zip = new ZipArchive(new MemoryStream(xlsx.Content), ZipArchiveMode.Read))
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml"],
                "one sheet and its styles: no macros, links or external parts");
        var sheet = SheetXml(xlsx.Content);
        sheet.Should().Contain("<is><t xml:space=\"preserve\">=HYPERLINK(&quot;http://evil.test&quot;,&quot;x&quot;)</t></is>", "text is an inline string, which a spreadsheet shows and never evaluates");
        sheet.Should().NotContain("<f>").And.NotContain("<f ");
        sheet.Should().Contain("<is><t xml:space=\"preserve\">Jason Carter</t></is>").And.Contain("<v>40</v>").And.Contain("<v>90</v>");
        System.Xml.Linq.XDocument.Parse(sheet).Root!.Name.LocalName.Should().Be("worksheet");

        // Audited: who, which report, which format, what period and how many rows.
        var entries = await w.Db.AuditLog.Where(a => a.Action == "workforce.report.exported").OrderBy(a => a.CreatedAt).ToListAsync();
        entries.Should().HaveCount(2);
        entries[0].DetailJson.Should().Contain("\"report\":\"future-capacity\"").And.Contain("\"format\":\"csv\"").And.Contain("\"rows\":5");
        entries[1].DetailJson.Should().Contain("\"format\":\"xlsx\"");

        // Every report: the preview and both exports are built from the same rows.
        await Grant(w, "Administrator", Permissions.IntegrationHealthView);
        admin = w.As(w.Admin).Analytics;
        (await admin.CatalogueAsync(w.Admin.Id)).Should().HaveCount(12).And.Contain(d => d.Key == "integration-health" && d.NeedsIntegrationHealth && d.Category == "Integrations");
        foreach (var definition in WorkforceAnalyticsService.ReportDefinitions)
        {
            var preview = await admin.ReportAsync(w.Admin.Id, definition.Key, new InsightsQuery());
            preview.Columns.Should().NotBeEmpty(definition.Key);
            preview.Rows.All(r => r.Count == preview.Columns.Count).Should().BeTrue(definition.Key);
            preview.Summary.Should().NotBeEmpty(definition.Key);
            foreach (var format in new[] { "csv", "xlsx" })
                (await admin.ExportReportAsync(w.Admin.Id, definition.Key, new InsightsQuery(), format)).Rows.Should().Be(preview.TotalRows, $"{definition.Key} as {format}");
            JsonSerializer.Serialize(preview).Should().NotBeNullOrEmpty();
        }
        // A person filter is named by the person; someone outside the scope is "not found" in every report.
        (await admin.ReportAsync(w.Admin.Id, "client-workload", new InsightsQuery(AppUserId: w.Jason.Id))).Applied.Should().Equal(new ReportFactDto("Technician", "Jason Carter"));
        foreach (var definition in WorkforceAnalyticsService.ReportDefinitions.Where(d => d.Filters.Count > 0))
            (await ((Func<Task>)(() => lead.ReportAsync(w.Lead.Id, definition.Key, new InsightsQuery(AppUserId: w.Sam.Id)))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.", definition.Key);
    }

    [Fact]
    public void A_workbook_is_one_sheet_of_typed_cells_and_never_a_formula()
    {
        var bytes = XlsxWriter.Write("Capacity: Q1/2026 [draft]",
        [
            ["PIO MANAGE: test"],
            ["Name", "Hours", "Items", "Share"],
            ["=1+1", 12.5m, 3, 33.33d],
            ["+SUM(A1:A2)", null, 0L, null],
            ["@cmd", "-2+3", true, "a\u0001b <&> \"q\""],
        ], new HashSet<int> { 0, 1 });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string Part(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        Part("xl/workbook.xml").Should().Contain("<sheet name=\"Capacity  Q1 2026  draft\"", "a sheet name cannot hold : / [ ]");
        var sheet = Part("xl/worksheets/sheet1.xml");
        var doc = System.Xml.Linq.XDocument.Parse(sheet);
        var ns = doc.Root!.Name.Namespace;
        var cells = doc.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c);
        // Text, whatever it begins with, is an inline string.
        foreach (var (at, value) in new[] { ("A3", "=1+1"), ("A4", "+SUM(A1:A2)"), ("A5", "@cmd"), ("B5", "-2+3"), ("C5", "yes"), ("D5", "ab <&> \"q\"") })
        {
            ((string?)cells[at].Attribute("t"), cells[at].Value).Should().Be(("inlineStr", value), at);
        }
        // Numbers are numbers, with a format; an empty cell is absent.
        ((string?)cells["B3"].Attribute("t"), cells["B3"].Value, (string?)cells["B3"].Attribute("s")).Should().Be((null, "12.5", "2"));
        (cells["C3"].Value, (string?)cells["C3"].Attribute("s"), cells["D3"].Value, cells["C4"].Value).Should().Be(("3", "3", "33.33", "0"));
        cells.Should().NotContainKey("B4").And.NotContainKey("D4");
        // The title and the column heads are bold.
        ((string?)cells["A1"].Attribute("s"), (string?)cells["B2"].Attribute("s"), (string?)cells["A3"].Attribute("s")).Should().Be(("1", "1", null));
        doc.Descendants(ns + "f").Should().BeEmpty("no cell ever carries a formula");
        (XlsxWriter.Column(0), XlsxWriter.Column(25), XlsxWriter.Column(26), XlsxWriter.Column(701), XlsxWriter.Column(702)).Should().Be(("A", "Z", "AA", "ZZ", "AAA"));
    }

    // ---- recurring work ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recurring_work_is_counted_as_occurrences_for_those_who_manage_boards_and_never_as_hours()
    {
        var w = await WorldAsync();
        w.Db.RecurringTickets.Add(new RecurringTicket
        {
            MspOrganizationId = OrgA, BoardId = w.Board.Id, Title = "Check the backups", Frequency = RecurrenceFrequency.Daily, Hour = 9,
            AssignedAppUserId = w.Jason.Id, IsActive = true, CreatedByUserId = w.Lead.Id, NextRunAt = At(Monday, "09:00"),
        });
        w.Db.RecurringTickets.Add(new RecurringTicket
        {
            MspOrganizationId = OrgA, BoardId = w.Board.Id, Title = "Retired check", Frequency = RecurrenceFrequency.Daily, Hour = 9,
            IsActive = false, CreatedByUserId = w.Lead.Id, NextRunAt = At(Monday, "09:00"),
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        var fc = await w.As(w.Admin).Analytics.ForecastAsync(w.Admin.Id, NextWeek);
        fc.Recurring.Should().NotBeNull();
        (fc.Recurring!.Definitions, fc.Recurring.Occurrences).Should().Be((1, 7), "every day of the week, the retired one not at all");
        fc.Recurring.Items.Single().Should().Match<RecurringItemDto>(i => i.Title == "Check the backups" && i.AssigneeName == "Jason Carter" && i.Occurrences == 7 && i.Next == At(Monday, "09:00"));
        fc.Totals.ProjectedMinutes.Should().Be(0, "an occurrence carries no estimate: it is a count and adds no hours");
        // The existing gate for the recurring list: someone who does not manage boards is told nothing about it.
        (await w.As(w.Jason).Analytics.ForecastAsync(w.Jason.Id, NextWeek)).Recurring.Should().BeNull();
        // A person filter keeps the recurring work assigned to that person only.
        (await w.As(w.Admin).Analytics.ForecastAsync(w.Admin.Id, NextWeek with { AppUserId = w.Abbie.Id })).Recurring!.Definitions.Should().Be(0);
    }
}
