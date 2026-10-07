using Desk.Application.Workforce;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Time entered in the PSA itself is recorded work in the workforce screens.
///
/// The portal's time entries were only the ones logged from the portal, so an hour written down in
/// the PSA was not in a person's day, the team's day or the workforce figures: someone who worked a
/// full day and logged it in the PSA read as having recorded nothing. It is now kept as a worklog
/// (ProviderWorklogTests) and counted here for the person its login is linked to. These hold that
/// it is counted once, for that person and nobody else, and that it is told apart from time logged
/// in the portal.
///
/// The world is WorkPlanTests': Jason, Abbie and Sam work 08:30-17:30 Asia/Kolkata. Every figure
/// asserted is defined in docs/workforce-scheduling/PHASE7_ANALYTICS_METRIC_SPEC.md.
/// </summary>
public partial class WorkPlanTests
{
    /// <summary>An hour entered in the PSA, as the sync keeps it: no portal author, filed under a PSA login.</summary>
    private static async Task EnteredInPsa(World w, Ticket on, string login, DateOnly date, string hm, decimal hours, string id, bool billable = true)
    {
        w.Db.TicketTimeEntries.Add(new TicketTimeEntry
        {
            MspOrganizationId = OrgA, TicketId = on.Id, PsaConnectionId = on.PsaConnectionId, AppUserId = null, ExternalEntryId = id,
            TechnicianExternalId = login, TechnicianName = "Login " + login, Hours = hours, Billable = billable,
            EntryDate = At(date, hm), Source = TimeEntrySource.Provider, SyncStatus = TimeEntrySyncStatus.Synced,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Time_entered_in_the_PSA_is_recorded_work_for_the_person_its_login_is_linked_to()
    {
        var w = await WorldAsync();
        var connection = w.Autotask.PsaConnectionId!.Value;
        // The account the portal writes as on this connection is login 900; Jason's own login there is 41.
        w.Db.PsaConnections.Add(new PsaConnection
        {
            Id = connection, MspOrganizationId = OrgA, Name = "Customer A", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://a",
            CredentialSecretRef = "mem://a", DefaultTimeEntryResourceId = "900",
        });
        w.Db.UserPsaIdentities.Add(new UserPsaIdentity { MspOrganizationId = OrgA, AppUserId = w.Jason.Id, PsaConnectionId = connection, ExternalTechnicianId = "41" });
        await w.Db.SaveChangesAsync();

        // Monday: Jason logs one hour in the portal, and enters two in the PSA itself.
        await Log(w, w.Jason, w.Autotask, Monday, "09:00", 1m);
        await EnteredInPsa(w, w.Autotask, "41", Monday, "11:00", 2m, "te-1");
        // The same day in the PSA: four hours under a login linked to nobody, eight under the account's own.
        await EnteredInPsa(w, w.Autotask, "77", Monday, "12:00", 4m, "te-2");
        await EnteredInPsa(w, w.Autotask, "900", Monday, "13:00", 8m, "te-3");
        ClockTo(w, At(Monday.AddDays(7), "09:00"));
        var admin = w.As(w.Admin);

        // The workforce figures: three hours for Jason, and the rest is nobody's here.
        var week = await admin.Analytics.OverviewAsync(w.Admin.Id, Week);
        week.People.Single(p => p.AppUserId == w.Jason.Id).Figures.ActualSeconds.Should().Be(3 * 3600, "one hour logged here and two entered in the PSA, each once");
        week.Totals.ActualSeconds.Should().Be(3 * 3600, "an unlinked login and the account the portal writes as are not people of this desk");
        var rows = await admin.Analytics.WorkAsync(w.Admin.Id, Week, AnalyticsWorkKind.Actual, 0, 50);
        rows.Rows.Select(r => (r.Seconds, r.Status)).Should().BeEquivalentTo([((int?)3600, "Recorded"), ((int?)7200, "Logged in the PSA")],
            "the two are told apart: one was logged in the portal and one was not");
        rows.TotalSeconds.Should().Be(week.Totals.ActualSeconds);

        // Jason's own day, and the team's.
        var day = await w.As(w.Jason).Time.MyDayAsync(w.Jason.Id, null, Monday);
        day.Items.Single(i => i.TicketId == w.Autotask.Id).ActualSeconds.Should().Be(3 * 3600);
        day.Summary.ActualSeconds.Should().Be(3 * 3600);
        var team = await admin.Time.TeamTodayAsync(w.Admin.Id, new TeamPlanQuery(Monday, Monday));
        team.People.Single(p => p.AppUserId == w.Jason.Id).ActualSeconds.Should().Be(3 * 3600);
        team.People.Where(p => p.AppUserId != w.Jason.Id).Sum(p => p.ActualSeconds).Should().Be(0);

        // Login 77 turns out to be Abbie's. Linked, its four hours are hers, from the same rows.
        w.Db.UserPsaIdentities.Add(new UserPsaIdentity { MspOrganizationId = OrgA, AppUserId = w.Abbie.Id, PsaConnectionId = connection, ExternalTechnicianId = "77" });
        await w.Db.SaveChangesAsync();
        var linked = await admin.Analytics.OverviewAsync(w.Admin.Id, Week);
        linked.People.Single(p => p.AppUserId == w.Abbie.Id).Figures.ActualSeconds.Should().Be(4 * 3600);
        linked.Totals.ActualSeconds.Should().Be(7 * 3600);
        (await w.Db.TicketTimeEntries.AsNoTracking().CountAsync(e => e.Source == TimeEntrySource.Provider && e.AppUserId != null)).Should().Be(0);
    }
}
