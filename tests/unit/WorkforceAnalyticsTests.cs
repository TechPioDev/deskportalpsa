using System.Text;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Workforce;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Phase 7: workforce analytics. Same world as the planning and work-execution tests: Jason and
/// Abbie (technicians, NOC), the lead (NOC, scope Team), Sam (Security), the administrator, an
/// outsider in another organization; Jason, Abbie and Sam work 08:30-17:30 Asia/Kolkata with an
/// hour's break (480 usable minutes a day); the lead and the administrator have no schedule. The
/// clock starts on 1 Jan 2026; the week under test is Mon 5 - Sun 11 Jan.
///
/// Every figure asserted here is defined in docs/workforce-scheduling/PHASE7_ANALYTICS_METRIC_SPEC.md.
/// </summary>
public partial class WorkPlanTests
{
    // Properties, not fields: a static field here could initialize before Monday, which lives in another file of this partial class.
    private static DateOnly Wednesday => Monday.AddDays(2);
    private static DateOnly Friday => Monday.AddDays(4);
    private static DateOnly Sunday => Monday.AddDays(6);
    private static AnalyticsQuery Week => new("custom", Monday, Sunday);

    /// <summary>A recorded hour, as the time panel or a stopped clock writes it (the day is the entry's start in the person's zone).</summary>
    private static async Task Log(World w, AppUser who, Ticket on, DateOnly date, string hm, decimal hours, bool billable = true, string? workType = null, TimeEntrySyncStatus sync = TimeEntrySyncStatus.Synced)
    {
        w.Db.TicketTimeEntries.Add(new TicketTimeEntry
        {
            MspOrganizationId = OrgA, TicketId = on.Id, AppUserId = who.Id, Hours = hours, Billable = billable, WorkTypeLabel = workType,
            EntryDate = At(date, hm), Source = TimeEntrySource.Portal, SyncStatus = sync, Notes = null,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>A finished board ticket credited to <paramref name="who"/>, finished at <paramref name="at"/> (null: finished with no date, as some imports arrive).</summary>
    private static async Task<Ticket> Finish(World w, string title, AppUser who, DateTimeOffset? at, Guid? client = null, string number = "")
    {
        var t = new Ticket
        {
            MspOrganizationId = OrgA, Origin = TicketOrigin.Internal, BoardId = w.Board.Id, Number = number == "" ? $"INT-{Random.Shared.Next(100000, 999999)}" : number,
            RequesterName = "Lena Lead", RequesterEmail = "lena@techpio.test", Title = title, PortalStatus = "CLOSED", PortalPriority = "NORMAL",
            AssignedAppUserId = who.Id, ResolvedByAppUserId = who.Id, ResolvedAt = at, ClosedAt = at, ClientCompanyId = client ?? w.JasonsTicket.ClientCompanyId,
            SyncStatus = TicketSyncStatus.Synced, UpdateHash = "hash-" + title, CreatedByUserId = w.Lead.Id,
        };
        w.Db.Tickets.Add(t);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return t;
    }

    // ---- the specification fixture ---------------------------------------------------------------------

    [Fact]
    public async Task The_specification_fixture_reconciles_exactly()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        // Planned: Mon-Thu, 08:30-12:30 and 13:30-17:30 on Jason's ticket = 32 h confirmed.
        for (var day = 0; day < 4; day++)
        {
            await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday.AddDays(day), "08:30", "12:30"));
            await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday.AddDays(day), "13:30", "17:30"));
        }
        // Actual: 6 h 15 m on the planned ticket on each of those days (25 h), and 5 h on Friday on a ticket with nothing planned.
        for (var day = 0; day < 4; day++) await Log(w, w.Jason, w.JasonsTicket, Monday.AddDays(day), "09:00", 6.25m);
        await Log(w, w.Jason, w.OpenTicket, Friday, "10:00", 5m);
        // Completed: ten tickets credited to Jason on Tuesday, one to Abbie on Monday, one with no completion date.
        for (var i = 0; i < 10; i++) await Finish(w, $"Done {i}", w.Jason, At(Tuesday, "16:00"));
        await Finish(w, "Abbie's work", w.Abbie, At(Monday, "11:00"));
        await Finish(w, "Imported closed, undated", w.Jason, null);

        // A technician sees their own figures and nobody else's.
        var mine = await w.As(w.Jason).Analytics.OverviewAsync(w.Jason.Id, Week);
        var jason = mine.People.Should().ContainSingle().Subject;
        jason.DisplayName.Should().Be("Jason Carter");
        var f = jason.Figures;
        (f.CapacityMinutes, f.PlannedMinutes, f.TentativeMinutes).Should().Be((2400, 1920, 0), "5 x 8 h; 4 x 8 h planned");
        (f.ActualSeconds, f.PlannedActualSeconds, f.ReactiveActualSeconds, f.LiveSeconds).Should().Be((108_000, 90_000, 18_000, 0), "30 h, of which 25 h on the day's planned work");
        (f.ScheduledUtilizationPercent, f.CapacityUtilizationPercent, f.ReactiveSharePercent).Should().Be((80d, 75d, 16.67d));
        (f.VarianceMinutes, f.VariancePercent).Should().Be((-120, -6.25d), "30 h against 32 h planned");
        (f.AbsoluteVarianceMinutes, f.EstimateVariancePercent, f.PlannedItemsCompared).Should().Be((420, 21.88d, 4), "four planned days, each 105 minutes under");
        (f.CompletedWork, f.WorkItems, f.ReactiveWorkItems).Should().Be((10, 2, 1), "ten finished tickets; the undated one is in no period");
        (f.BillableSeconds, f.InternalSeconds, f.ClientSeconds, f.MonitoringSeconds).Should().Be((108_000, 108_000, 0, 0));
        (f.OverCapacityMinutes, f.OverCapacityPersonDays).Should().Be((0, 0));
        mine.Totals.Should().BeEquivalentTo(f, "a technician's totals are their own figures");
        (mine.SeesOthers, mine.CanExport).Should().Be((false, false));
        mine.Period.Should().Be(new AnalyticsPeriodDto("custom", Monday, Sunday, Zone, "Mon 5 Jan – Sun 11 Jan 2026", 7, true));
        mine.Notes.Should().Contain(n => n.Contains("1 finished work item has no completion date"));

        // The administrator sees everyone; the lead and the administrator have no schedule and say so.
        var all = await w.As(w.Admin).Analytics.OverviewAsync(w.Admin.Id, Week);
        all.People.Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Harpal Admin", "Jason Carter", "Lena Lead", "Sam Shah");
        (all.Totals.CapacityMinutes, all.Totals.PlannedMinutes, all.Totals.ActualSeconds, all.Totals.CompletedWork).Should().Be((7200, 1920, 108_000, 11));
        (all.Totals.ScheduledUtilizationPercent, all.Totals.CapacityUtilizationPercent).Should().Be((26.67d, 25d));
        all.Notes.Should().Contain(n => n.StartsWith("2 people have no working schedule"));
        all.People.Single(p => p.DisplayName == "Lena Lead").Figures.Should().Match<AnalyticsFiguresDto>(x => x.CapacityMinutes == 0 && x.CapacityUtilizationPercent == null && x.ScheduledUtilizationPercent == null, "N/A, never 0 %");
        (all.SeesOthers, all.CanExport).Should().Be((true, true));

        // Breakdowns are the same rows grouped, so they add up.
        var noc = all.Teams.Single(t => t.Name == "NOC");
        (noc.People, noc.Figures.CapacityMinutes, noc.Figures.PlannedMinutes, noc.Figures.ActualSeconds, noc.Figures.CompletedWork).Should().Be((3, 4800, 1920, 108_000, 11));
        var security = all.Teams.Single(t => t.Name == "Security");
        (security.People, security.Figures.CapacityMinutes, security.Figures.ActualSeconds, security.Figures.CompletedWork).Should().Be((1, 2400, 0, 0));
        all.Clients.Should().ContainSingle().Which.Should().Match<AnalyticsGroupDto>(c => c.Name == "ABC Company" && c.Figures.ActualSeconds == 108_000 && c.Figures.CompletedWork == 11 && c.Figures.PlannedMinutes == 1920 && c.Figures.CapacityMinutes == null);
        all.Sources.Should().ContainSingle().Which.Should().Match<AnalyticsGroupDto>(s => s.Key == "internal" && s.Name == "Team boards" && s.Figures.ActualSeconds == 108_000);
        all.Priorities.Should().ContainSingle().Which.Name.Should().Be("NORMAL");
        all.WorkTypes.Should().ContainSingle().Which.Should().Match<AnalyticsGroupDto>(x => x.Name == WorkforceAnalyticsService.NotSet && x.Figures.ActualSeconds == 108_000);
        all.Daily.Should().HaveCount(7);
        all.Daily.Single(d => d.Date == Tuesday).Figures.Should().Match<AnalyticsFiguresDto>(d => d.CompletedWork == 10 && d.CapacityMinutes == 1440 && d.PlannedMinutes == 480 && d.ActualSeconds == 22_500);
        all.Daily.Single(d => d.Date == Friday).Figures.Should().Match<AnalyticsFiguresDto>(d => d.ActualSeconds == 18_000 && d.ReactiveActualSeconds == 18_000 && d.CompletedWork == 0);
        all.Daily.Single(d => d.Date == Sunday).Figures.Should().Match<AnalyticsFiguresDto>(d => d.CapacityMinutes == 0 && d.ActualSeconds == 0 && d.CapacityUtilizationPercent == null);
        all.Heatmap.Unavailable.Should().BeNull();
        all.Heatmap.Dates.Should().HaveCount(7);
        all.Heatmap.Rows.Single(r => r.AppUserId == w.Jason.Id).Cells[0].Should().Be(new HeatmapCellDto(480, 480, 22_500));
        all.Heatmap.Rows.Single(r => r.AppUserId == w.Lead.Id).Cells[0].Should().Be(new HeatmapCellDto(0, 0, 0));
        all.Demand.Should().Be(new CapacityDemandDto(7200, 1920, 0, 1920, 0, 5280));
        all.Now.Should().Match<WorkNowDto>(n => n.Open == 3 && n.Unscheduled == 2 && n.Overdue == 0, "Jason's two open tickets and Sam's are held; only Jason's board ticket has a plan ahead of now");
    }

    // ---- tentative, time away, over-capacity, zero capacity, the clock -------------------------------------

    [Fact]
    public async Task Tentative_work_time_away_over_capacity_and_a_running_clock_are_reported_as_facts()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var admin = w.As(w.Admin);
        // Abbie: pencilled-in work on Monday, Wednesday off.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Abbie, Monday, "09:00", "11:00") with { Tentative = true });
        await lead.Exceptions.AddAsync(w.Lead.Id, w.Abbie.Id, new CapacityExceptionInput(CapacityExceptionKind.Unavailable, true, Wednesday, Wednesday, null, null, CapacityExceptionReason.TimeOff, null));
        // Jason: planned straight through the break on Monday by override - 9 h into an 8 h day.
        await admin.Plans.CreateAsync(w.Admin.Id, Place(w.JasonsTicket, w.Jason, Monday, "08:30", "17:30", reason: "Change window, all day"));
        // The lead, with no schedule, records two hours.
        await Log(w, w.Lead, w.OpenTicket, Monday, "10:00", 2m);

        var all = await admin.Analytics.OverviewAsync(w.Admin.Id, Week);
        var abbie = all.People.Single(p => p.AppUserId == w.Abbie.Id).Figures;
        (abbie.CapacityMinutes, abbie.PlannedMinutes, abbie.TentativeMinutes, abbie.ScheduledUtilizationPercent).Should().Be((1920, 0, 120, 0d), "four working days; tentative work is apart and takes no capacity");
        var jason = all.People.Single(p => p.AppUserId == w.Jason.Id).Figures;
        (jason.PlannedMinutes, jason.OverCapacityMinutes, jason.OverCapacityPersonDays, jason.ScheduledUtilizationPercent).Should().Be((540, 60, 1, 22.5d), "540 planned into 480 on Monday");
        var lena = all.People.Single(p => p.AppUserId == w.Lead.Id).Figures;
        (lena.CapacityMinutes, lena.ActualSeconds, lena.CapacityUtilizationPercent, lena.VarianceMinutes).Should().Be((0, 7200, null, null));
        all.Demand.Should().Be(new CapacityDemandDto(6720, 540, 120, 660, 0, 6180));
        (all.Totals.OverCapacityMinutes, all.Totals.TentativeMinutes).Should().Be((60, 120));
        all.Notes.Should().Contain("This period has not ended: its later days hold capacity but no recorded time yet, so utilization reads low until it is over.");

        // The clock: a stopped clock is its entry and nothing else; a running one is counted live, once.
        var jasonsDesk = w.As(w.Jason);
        ClockTo(w, At(Tuesday, "09:00"));
        var first = await jasonsDesk.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        await Minutes(w, 40);
        await jasonsDesk.Time.StopAsync(w.Jason.Id, first.Id, new StopWorkInput(first.Version, "Patched"));
        ClockTo(w, At(Tuesday, "10:00"));
        await jasonsDesk.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        await Minutes(w, 30);

        var today = await jasonsDesk.Analytics.OverviewAsync(w.Jason.Id, new AnalyticsQuery("today"));
        today.Period.Should().Match<AnalyticsPeriodDto>(p => p.From == Tuesday && p.To == Tuesday && p.Key == "today");
        (today.Totals.ActualSeconds, today.Totals.LiveSeconds, today.Totals.WorkItems).Should().Be((2400 + 1800, 1800, 1));
        (await w.Db.WorkSessions.CountAsync(), await w.Db.TicketTimeEntries.CountAsync(e => e.AppUserId == w.Jason.Id)).Should().Be((2, 1));
        today.Notes.Should().Contain("30 m on clocks still running is included in today's actual time.");
        today.WorkTypes.Should().Contain(x => x.Name == WorkforceAnalyticsService.RunningClock && x.Figures.ActualSeconds == 1800);
        var rows = await jasonsDesk.Analytics.WorkAsync(w.Jason.Id, new AnalyticsQuery("today"), AnalyticsWorkKind.Actual, 0, 50);
        rows.Rows.Select(r => (r.Kind, r.Seconds, r.Status)).Should().BeEquivalentTo([("entry", (int?)2400, "Recorded"), ("live", 1800, "Running")]);
        rows.TotalSeconds.Should().Be(today.Totals.ActualSeconds);
    }

    // ---- reactive classification -------------------------------------------------------------------------------

    [Fact]
    public async Task Reactive_time_is_time_on_a_ticket_day_with_nothing_planned_and_a_cancelled_plan_counts_for_nothing()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        // Monday: planned 09:00-10:00; three hours recorded in the afternoon, outside the slot - still the day's planned work.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        await Log(w, w.Jason, w.JasonsTicket, Monday, "14:00", 3m);
        // Tuesday: only pencilled in; an hour recorded. Expected work, so not reactive; nothing confirmed, so no variance.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Tuesday, "09:00", "10:00") with { Tentative = true });
        await Log(w, w.Jason, w.JasonsTicket, Tuesday, "11:00", 1m);
        // Wednesday: planned, then taken out of the plan; an hour recorded anyway - reactive.
        var cancelled = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Wednesday, "09:00", "10:00"));
        await lead.Plans.CancelAsync(w.Lead.Id, cancelled.Id, "Client postponed");
        await Log(w, w.Jason, w.JasonsTicket, Wednesday, "11:00", 1m);

        var jason = w.As(w.Jason).Analytics;
        var all = await jason.OverviewAsync(w.Jason.Id, Week);
        var f = all.Totals;
        (f.PlannedMinutes, f.TentativeMinutes, f.ActualSeconds, f.PlannedActualSeconds, f.ReactiveActualSeconds).Should().Be((60, 60, 18_000, 14_400, 3600));
        (f.WorkItems, f.ReactiveWorkItems, f.VarianceMinutes, f.VariancePercent).Should().Be((1, 1, 240, 400d), "5 h recorded against the one confirmed hour");
        (f.AbsoluteVarianceMinutes, f.PlannedItemsCompared).Should().Be((120, 1), "only Monday had a confirmed plan to compare: 180 against 60");
        all.Daily.Single(d => d.Date == Monday).Figures.Should().Match<AnalyticsFiguresDto>(d => d.PlannedActualSeconds == 10_800 && d.ReactiveActualSeconds == 0 && d.VarianceMinutes == 120);
        all.Daily.Single(d => d.Date == Tuesday).Figures.Should().Match<AnalyticsFiguresDto>(d => d.PlannedActualSeconds == 3600 && d.TentativeMinutes == 60 && d.PlannedMinutes == 0 && d.VarianceMinutes == null);
        all.Daily.Single(d => d.Date == Wednesday).Figures.Should().Match<AnalyticsFiguresDto>(d => d.ReactiveActualSeconds == 3600 && d.PlannedMinutes == 0);

        // The kind filter narrows the recorded time only; planned minutes and capacity stay.
        var reactive = await jason.OverviewAsync(w.Jason.Id, Week with { Kind = ActualKindFilter.Reactive });
        (reactive.Totals.ActualSeconds, reactive.Totals.PlannedMinutes, reactive.Totals.CapacityMinutes, reactive.Totals.WorkItems).Should().Be((3600, 60, 2400, 1));
        (reactive.Totals.VarianceMinutes, reactive.Totals.AbsoluteVarianceMinutes).Should().Be((240, 120), "variance is the plan against all the recorded time, whatever the kind filter");
        var planned = await jason.OverviewAsync(w.Jason.Id, Week with { Kind = ActualKindFilter.Planned });
        (planned.Totals.ActualSeconds, planned.Totals.ReactiveActualSeconds).Should().Be((14_400, 0));
        // The same split My day shows for Wednesday.
        var myDay = await w.As(w.Jason).Time.MyDayAsync(w.Jason.Id, null, Wednesday);
        myDay.Summary.UnplannedActualSeconds.Should().Be(3600);
    }

    // ---- breakdowns, filters, scope and tenant isolation -------------------------------------------------------

    [Fact]
    public async Task Breakdowns_add_up_to_the_totals_filters_combine_and_nothing_leaks_across_scope_or_organization()
    {
        var w = await WorldAsync();
        var xyz = new ClientCompany { MspOrganizationId = OrgA, Name = "XYZ Co", ExternalCompanyId = "xyz", PsaConnectionId = w.Autotask.PsaConnectionId!.Value };
        var xyzTicket = new Ticket
        {
            MspOrganizationId = OrgA, Origin = TicketOrigin.Psa, Provider = ProviderType.AutotaskPsa, ExternalTicketId = "5151", PsaConnectionId = xyz.PsaConnectionId,
            RequesterName = "XYZ", RequesterEmail = "it@xyz.test", Title = "VPN down", PortalStatus = "IN_PROGRESS", PortalPriority = "HIGH",
            AssignedAppUserId = w.Sam.Id, ClientCompanyId = xyz.Id, SyncStatus = TicketSyncStatus.Synced, UpdateHash = "hash-vpn", AssignedTechnicianExternalId = "",
        };
        w.Db.AddRange(xyz, xyzTicket);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await Log(w, w.Sam, xyzTicket, Monday, "09:00", 2m, workType: "Remote support");
        await Log(w, w.Jason, w.Autotask, Monday, "09:00", 1m, workType: "Onsite");
        await Log(w, w.Jason, w.JasonsTicket, Monday, "11:00", 1m, billable: false);

        var admin = w.As(w.Admin).Analytics;
        var all = await admin.OverviewAsync(w.Admin.Id, Week);
        all.Totals.ActualSeconds.Should().Be(14_400);
        (all.Totals.ClientSeconds, all.Totals.InternalSeconds, all.Totals.BillableSeconds).Should().Be((10_800, 3600, 10_800));
        foreach (var breakdown in new[] { all.Teams, all.Clients, all.Sources, all.Priorities, all.WorkTypes })
            breakdown.Sum(g => g.Figures.ActualSeconds).Should().Be(14_400, "every breakdown is the same rows grouped");
        all.Clients.Select(c => (c.Name, c.Figures.ActualSeconds, c.People)).Should().BeEquivalentTo([("ABC Company", 7200, 1), ("XYZ Co", 7200, 1)]);
        all.Sources.Select(s => (s.Name, s.Figures.ActualSeconds)).Should().BeEquivalentTo([("Autotask", 10_800), ("Team boards", 3600)]);
        all.Priorities.Select(p => (p.Name, p.Figures.ActualSeconds)).Should().BeEquivalentTo([("NORMAL", 7200), ("HIGH", 7200)]);
        all.WorkTypes.Select(x => (x.Name, x.Figures.ActualSeconds)).Should().BeEquivalentTo([("Remote support", 7200), ("Onsite", 3600), (WorkforceAnalyticsService.NotSet, 3600)]);
        all.Teams.Select(t => (t.Name, t.Figures.ActualSeconds)).Should().BeEquivalentTo([("NOC", 7200), ("Security", 7200)]);

        // Filters combine: the client, the kind of source, the priority and the team all narrow the same set.
        var narrowed = await admin.OverviewAsync(w.Admin.Id, Week with { ClientId = xyz.Id, Source = "client", Priority = "high", TeamId = w.Db.Teams.Single(t => t.Name == "Security").Id });
        narrowed.People.Select(p => p.DisplayName).Should().Equal("Sam Shah");
        (narrowed.Totals.ActualSeconds, narrowed.Totals.CapacityMinutes, narrowed.Totals.WorkItems).Should().Be((7200, 2400, 1), "capacity is the person's, the hours are the client's");
        var crossed = await admin.OverviewAsync(w.Admin.Id, Week with { ClientId = xyz.Id, TeamId = w.Noc.Id });
        (crossed.People.Count, crossed.Totals.ActualSeconds).Should().Be((3, 0), "nobody in NOC worked for XYZ");
        var internalOnly = await admin.OverviewAsync(w.Admin.Id, Week with { Source = "internal" });
        (internalOnly.Totals.ActualSeconds, internalOnly.Totals.ClientSeconds).Should().Be((3600, 0));

        // A forged or foreign id: nobody, or "not found" - never an error that confirms anything exists.
        var forgedTeam = await admin.OverviewAsync(w.Admin.Id, Week with { TeamId = Guid.NewGuid() });
        (forgedTeam.People.Count, forgedTeam.Totals.ActualSeconds, forgedTeam.Totals.CapacityMinutes).Should().Be((0, 0, null));
        (await ((Func<Task>)(() => admin.OverviewAsync(w.Admin.Id, Week with { ClientId = Guid.NewGuid() }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Client was not found.");
        (await ((Func<Task>)(() => admin.OverviewAsync(w.Admin.Id, Week with { Source = "psa:" + Guid.NewGuid() }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Connection was not found.");
        await ((Func<Task>)(() => admin.OverviewAsync(w.Admin.Id, Week with { Source = "everything" }))).Should().ThrowAsync<ValidationFailedException>();
        (await ((Func<Task>)(() => admin.OverviewAsync(w.Admin.Id, Week with { AppUserId = w.Outsider.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");

        // Scope: the lead reaches NOC; a technician reaches themselves; asking for anyone else is "not found".
        var leads = await w.As(w.Lead).Analytics.OverviewAsync(w.Lead.Id, Week);
        leads.People.Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Jason Carter", "Lena Lead");
        (leads.Totals.ActualSeconds, leads.SeesOthers, leads.CanExport).Should().Be((7200, true, false));
        leads.Clients.Select(c => c.Name).Should().Equal("ABC Company");
        (await ((Func<Task>)(() => w.As(w.Lead).Analytics.OverviewAsync(w.Lead.Id, Week with { AppUserId = w.Sam.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        (await ((Func<Task>)(() => w.As(w.Jason).Analytics.TechnicianAsync(w.Jason.Id, w.Abbie.Id, Week))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        var jasonFilters = await w.As(w.Jason).Analytics.FiltersAsync(w.Jason.Id);
        jasonFilters.People.Select(p => p.Name).Should().Equal("Jason Carter");
        jasonFilters.Clients.Select(c => c.Name).Should().NotContain("XYZ Co", "a technician is offered only the clients of tickets they may open");
        (jasonFilters.SeesOthers, jasonFilters.CanExport).Should().Be((false, false));

        // Another organization: its own people only, and none of this organization's work. Its own
        // database context too, as every request has: the tenant filter is the context's.
        var outsider = World.For(AdminHarness.Create(OrgB, w.DbName).Db, OrgB, w.Outsider, w.Clock).Analytics;
        var elsewhere = await outsider.OverviewAsync(w.Outsider.Id, Week);
        elsewhere.People.Select(p => p.DisplayName).Should().Equal("Other Org");
        (elsewhere.Totals.ActualSeconds, elsewhere.Totals.CompletedWork, elsewhere.Teams.Count, elsewhere.Clients.Count).Should().Be((0, 0, 0, 0));
        (await ((Func<Task>)(() => outsider.OverviewAsync(w.Outsider.Id, Week with { ClientId = xyz.Id }))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Client was not found.");
        (await ((Func<Task>)(() => outsider.TechnicianAsync(w.Outsider.Id, w.Jason.Id, Week))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Person was not found.");
        (await outsider.WorkAsync(w.Outsider.Id, Week, AnalyticsWorkKind.Actual, 0, 50)).Total.Should().Be(0);

        // That organization's own manager, holding the export right, exports that organization and nothing of this one.
        var dbB = AdminHarness.Create(OrgB, w.DbName).Db;
        var roleB = new Role { MspOrganizationId = OrgB, Name = "Manager B", BuiltInType = RoleType.Manager };
        foreach (var key in new[] { Permissions.ScheduleView, Permissions.WorkforceAnalyticsExport, Permissions.TicketsViewAll })
            roleB.Permissions.Add(new RolePermission { PermissionKey = key, Scope = PermissionScope.All });
        var bea = new AppUser { MspOrganizationId = OrgB, DisplayName = "Bea Manager", Email = "bea@other.test", IsActive = true };
        bea.Roles.Add(new UserRole { RoleId = roleB.Id });
        dbB.AddRange(roleB, bea);
        await dbB.SaveChangesAsync();
        var theirs = World.For(dbB, OrgB, bea, w.Clock).Analytics;
        foreach (var report in new[] { AnalyticsExportReport.Technicians, AnalyticsExportReport.Clients, AnalyticsExportReport.Work, AnalyticsExportReport.Teams })
        {
            var file = Encoding.UTF8.GetString((await theirs.ExportAsync(bea.Id, Week, report)).Content);
            file.Should().NotContainAny("Jason", "Sam Shah", "ABC Company", "XYZ Co", "VPN down", "NOC", "Autotask 5151");
        }
        (await theirs.ExportAsync(bea.Id, Week, AnalyticsExportReport.Technicians)).Rows.Should().Be(2, "Bea and the other person of that organization");
        (await ((Func<Task>)(() => theirs.ExportAsync(bea.Id, Week with { ClientId = xyz.Id }, AnalyticsExportReport.Work))).Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("Client was not found.");

        // A team filter shows that team's row only: Sam's other team would be a half-row.
        var nocOnly = await admin.OverviewAsync(w.Admin.Id, Week with { TeamId = w.Noc.Id });
        nocOnly.Teams.Select(t => t.Name).Should().Equal("NOC");
    }

    // ---- periods ----------------------------------------------------------------------------------------------

    [Fact]
    public void Periods_resolve_in_the_organizations_zone_from_the_servers_today()
    {
        var today = new DateOnly(2026, 1, 7); // a Wednesday
        AnalyticsPeriodDto R(string? key, DateOnly? from = null, DateOnly? to = null) => WorkforceAnalyticsService.ResolvePeriod(new AnalyticsQuery(key, from, to), today, Zone);

        R("today").Should().Match<AnalyticsPeriodDto>(p => p.From == today && p.To == today && p.Days == 1 && !p.EndsInFuture && p.Label == "Wed 7 Jan 2026");
        R("yesterday").Should().Match<AnalyticsPeriodDto>(p => p.From == today.AddDays(-1) && p.To == today.AddDays(-1));
        R("this-week").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2026, 1, 5) && p.To == new DateOnly(2026, 1, 11) && p.EndsInFuture && p.Label == "Mon 5 Jan – Sun 11 Jan 2026");
        R("last-week").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2025, 12, 29) && p.To == new DateOnly(2026, 1, 4) && p.Label == "Mon 29 Dec 2025 – Sun 4 Jan 2026");
        R("this-month").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2026, 1, 1) && p.To == new DateOnly(2026, 1, 31) && p.Days == 31);
        R("last-month").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2025, 12, 1) && p.To == new DateOnly(2025, 12, 31));
        R("7d").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2026, 1, 1) && p.To == today && p.Days == 7);
        R("30d").Should().Match<AnalyticsPeriodDto>(p => p.From == new DateOnly(2025, 12, 9) && p.To == today && p.Days == 30);
        R(null).Key.Should().Be("this-week", "the default");
        R(null, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 7)).Should().Match<AnalyticsPeriodDto>(p => p.Key == "custom" && p.Days == 7 && p.TimeZone == Zone);
        R("custom", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).Days.Should().Be(365);

        ((Func<AnalyticsPeriodDto>)(() => R("custom"))).Should().Throw<ValidationFailedException>().WithMessage("A custom period needs both a first and a last date.");
        ((Func<AnalyticsPeriodDto>)(() => R("custom", today, today.AddDays(-1)))).Should().Throw<ValidationFailedException>().WithMessage("The last date is before the first.");
        ((Func<AnalyticsPeriodDto>)(() => R("custom", new DateOnly(2025, 1, 7), new DateOnly(2026, 1, 8)))).Should().Throw<ValidationFailedException>().WithMessage("Ask for at most 366 days at a time.");
        ((Func<AnalyticsPeriodDto>)(() => R("custom", new DateOnly(2024, 12, 1), new DateOnly(2024, 12, 2)))).Should().Throw<ValidationFailedException>().WithMessage("Choose dates within a year of today.");
        ((Func<AnalyticsPeriodDto>)(() => R("this-quarter"))).Should().Throw<ValidationFailedException>().WithMessage("Unknown period*");
    }

    [Fact]
    public async Task Completed_work_is_dated_at_the_organizations_midnight_and_a_clock_change_moves_no_date()
    {
        var w = await WorldAsync();
        // Sunday 23:30 in Mohali (18:00Z) is in the week; Monday 00:30 (Sunday 19:00Z) is not.
        await Finish(w, "Late Sunday", w.Jason, At(Sunday, "23:30"));
        await Finish(w, "Early Monday", w.Jason, At(Sunday.AddDays(1), "00:30"));
        var week = await w.As(w.Jason).Analytics.OverviewAsync(w.Jason.Id, Week);
        week.Totals.CompletedWork.Should().Be(1);
        week.Daily.Single(d => d.Date == Sunday).Figures.CompletedWork.Should().Be(1);
        (await w.As(w.Jason).Analytics.OverviewAsync(w.Jason.Id, new AnalyticsQuery("custom", Sunday.AddDays(1), Sunday.AddDays(7)))).Totals.CompletedWork.Should().Be(1);

        // The bounds a period uses are local midnights, so a clock change makes a 23- or 25-hour day rather than moving the date.
        // (A development host without IANA zone data resolves London as UTC and sees 48 hours; the Linux servers and CI see 47.)
        var london = Desk.Domain.Common.TimeZones.Resolve("Europe/London");
        var (from, to) = Desk.Application.Reporting.StaffReportPeriods.UtcBounds(new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 29), london);
        var springForward = london.GetUtcOffset(new DateTime(2026, 3, 30)) - london.GetUtcOffset(new DateTime(2026, 3, 28));
        (to - from).Add(TimeSpan.FromTicks(1)).Should().Be(TimeSpan.FromHours(48) - springForward, "the Sunday the clocks go forward has 23 hours");
        if (Desk.Domain.Common.TimeZones.HostKnowsIana) springForward.Should().Be(TimeSpan.FromHours(1));
    }

    // ---- drill-down and hidden work ------------------------------------------------------------------------

    [Fact]
    public async Task Every_card_opens_the_records_that_make_it_and_hides_work_the_caller_cannot_open()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        await Log(w, w.Jason, w.JasonsTicket, Monday, "09:00", 1m);
        // Jason logged two hours on Sam's PSA ticket, which his ticket scope (assigned) cannot open.
        await Log(w, w.Jason, w.SamsTicket, Tuesday, "09:00", 2m, sync: TimeEntrySyncStatus.Pending);
        var finished = await Finish(w, "Printer fixed", w.Jason, At(Wednesday, "15:00"));

        var jason = w.As(w.Jason).Analytics;
        var overview = await jason.OverviewAsync(w.Jason.Id, Week);
        (overview.Totals.ActualSeconds, overview.Totals.ReactiveActualSeconds, overview.Totals.PlannedMinutes, overview.Totals.CompletedWork).Should().Be((10_800, 7200, 60, 1));
        overview.Clients.Select(c => (c.Key, c.Name, c.Figures.ActualSeconds)).Should().BeEquivalentTo([(w.JasonsTicket.ClientCompanyId!.Value.ToString(), "ABC Company", 3600), ("hidden", WorkforceAnalyticsService.HiddenWork, 7200)]);

        var actual = await jason.WorkAsync(w.Jason.Id, Week, AnalyticsWorkKind.Actual, 0, 50);
        (actual.Total, actual.TotalSeconds).Should().Be((2, 10_800L));
        actual.TotalSeconds.Should().Be(overview.Totals.ActualSeconds, "the list reconciles with the card");
        var hidden = actual.Rows.Single(r => r.TicketId == w.SamsTicket.Id);
        (hidden.Reference, hidden.Title, hidden.ClientName, hidden.TicketVisible, hidden.Source, hidden.PlannedWork, hidden.Status, hidden.Seconds)
            .Should().Be((WorkforceAnalyticsService.HiddenWork, null, null, false, "Autotask", false, "Not in the PSA yet", 7200));
        var shown = actual.Rows.Single(r => r.TicketId == w.JasonsTicket.Id);
        (shown.Reference, shown.Title, shown.ClientName, shown.PlannedWork, shown.Status, shown.Minutes, shown.PersonName).Should().Be(("INT-000001", "Firewall review", "ABC Company", true, "Recorded", 60, "Jason Carter"));
        var reactive = await jason.WorkAsync(w.Jason.Id, Week, AnalyticsWorkKind.Reactive, 0, 50);
        (reactive.Total, reactive.TotalSeconds).Should().Be((1, 7200L));
        var planned = await jason.WorkAsync(w.Jason.Id, Week, AnalyticsWorkKind.Planned, 0, 50);
        (planned.Total, planned.TotalMinutes, planned.Rows.Single().Status).Should().Be((1, 60, "Planned"));
        var completed = await jason.WorkAsync(w.Jason.Id, Week, AnalyticsWorkKind.Completed, 0, 50);
        completed.Rows.Should().ContainSingle().Which.Should().Match<AnalyticsWorkRowDto>(r => r.TicketId == finished.Id && r.FinishedAt == At(Wednesday, "15:00") && r.Date == Wednesday && r.Kind == "ticket");
        // Paged on the server: the totals are the whole set's whatever the page.
        var second = await jason.WorkAsync(w.Jason.Id, Week, AnalyticsWorkKind.Actual, 1, 1);
        (second.Rows.Count, second.Total, second.TotalSeconds, second.Skip, second.Take).Should().Be((1, 2, 10_800L, 1, 1));

        // Open work right now, for the administrator: Jason's two open tickets and Sam's; the planned one is not unscheduled.
        var admin = w.As(w.Admin).Analytics;
        var now = (await admin.OverviewAsync(w.Admin.Id, Week)).Now;
        (now.Open, now.Unscheduled, now.Overdue, now.DueToday, now.DueSoon, now.UnscheduledDue).Should().Be((3, 2, 0, 0, 0, 0));
        var open = await admin.WorkAsync(w.Admin.Id, Week, AnalyticsWorkKind.Open, 0, 50);
        open.Rows.Select(r => (r.Reference, r.PlannedWork)).Should().BeEquivalentTo([("INT-000001", (bool?)true), ("Autotask 43829", false), ("Autotask 777", false)]);
        (await admin.WorkAsync(w.Admin.Id, Week, AnalyticsWorkKind.Unscheduled, 0, 50)).Total.Should().Be(2);

        // One person's detail: the items behind their figures.
        var detail = await admin.TechnicianAsync(w.Admin.Id, w.Jason.Id, Week);
        detail.Person.DisplayName.Should().Be("Jason Carter");
        detail.Items.Select(i => (i.Reference, i.PlannedMinutes, i.ActualSeconds, i.ReactiveActualSeconds, i.VarianceMinutes, i.Entries, i.Days))
            .Should().BeEquivalentTo([("Autotask 777", 0, 7200, 7200, (int?)null, 1, 1), ("INT-000001", 60, 3600, 0, 0, 1, 1)]);
        detail.Items.Single(i => i.Reference == "Autotask 777").Should().Match<AnalyticsWorkItemDto>(i => i.TicketVisible && i.Title == "Rotate the vault keys" && i.ClientName == "ABC Company", "the administrator may open it");
        detail.Daily.Should().HaveCount(7);
        detail.Person.Figures.CompletedWork.Should().Be(1);
    }

    // ---- export -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_is_a_sanitized_CSV_that_needs_its_own_permission_and_is_audited()
    {
        var w = await WorldAsync();
        var trap = await Finish(w, "=HYPERLINK(\"http://evil.test\",\"click\")", w.Jason, null);
        trap.PortalStatus = "IN_PROGRESS";
        w.Db.Tickets.Update(trap);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await Log(w, w.Jason, trap, Monday, "09:00", 1m);

        var admin = w.As(w.Admin).Analytics;
        var work = await admin.ExportAsync(w.Admin.Id, Week, AnalyticsExportReport.Work);
        work.FileName.Should().Be($"workforce-analytics-work-{Monday:yyyy-MM-dd}-{Sunday:yyyy-MM-dd}.csv");
        work.ContentType.Should().Be("text/csv");
        work.Content.Take(3).Should().Equal(Encoding.UTF8.GetPreamble(), "a byte-order mark, so a spreadsheet reads the names correctly");
        var text = Encoding.UTF8.GetString(work.Content, 3, work.Content.Length - 3);
        text.Should().StartWith("Workforce analytics: recorded work\r\nPeriod,");
        text.Should().Contain("\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"click\"\")\"", "a formula in a title is neutralised and quoted");
        text.Should().Contain("Jason Carter").And.Contain(",60,yes,reactive,Recorded");
        work.Rows.Should().Be(1);

        var technicians = await admin.ExportAsync(w.Admin.Id, Week, AnalyticsExportReport.Technicians);
        technicians.Rows.Should().Be(5);
        Encoding.UTF8.GetString(technicians.Content).Should().Contain("Technician,Teams,Offered for planned work,Capacity (h),Planned (h)").And.Contain("Jason Carter,NOC,yes,40,0,0,1,0,1,100,0,2.5,N/A,N/A,0,1,1,0,1,0,0");
        (await admin.ExportAsync(w.Admin.Id, Week, AnalyticsExportReport.Daily)).Rows.Should().Be(7);

        (await w.Db.AuditLog.Where(a => a.Action == "workforce.analytics.exported").CountAsync()).Should().Be(3);
        var entry = await w.Db.AuditLog.Where(a => a.Action == "workforce.analytics.exported").OrderBy(a => a.CreatedAt).FirstAsync();
        entry.DetailJson.Should().Contain("\"report\":\"Work\"").And.Contain("\"rows\":1").And.Contain("\"people\":5");

        // The right to read is not the right to take it out of the system.
        (await ((Func<Task>)(() => w.As(w.Lead).Analytics.ExportAsync(w.Lead.Id, Week, AnalyticsExportReport.Teams))).Should().ThrowAsync<ForbiddenException>())
            .Which.Message.Should().Be("Exporting workforce analytics needs the workforce.analytics.export permission.");
        await ((Func<Task>)(() => w.As(w.Jason).Analytics.ExportAsync(w.Jason.Id, Week, AnalyticsExportReport.Technicians))).Should().ThrowAsync<ForbiddenException>();
        (await w.Db.AuditLog.Where(a => a.Action == "workforce.analytics.exported").CountAsync()).Should().Be(3, "a refused export is not an export");
    }
}
