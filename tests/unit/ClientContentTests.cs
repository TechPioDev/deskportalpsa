using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.ControlPanel;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

public class ClientContentTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid AdminUser = Guid.NewGuid();
    private static readonly Guid RegularUser = Guid.NewGuid();

    private static ClientAccess AdminAccess => new(Org, CompanyA, AdminUser, true);
    private static ClientAccess UserAccess => new(Org, CompanyA, RegularUser, false);

    private static (ClientContentService svc, AdminHarness h) Build()
    {
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection { Id = Conn, MspOrganizationId = Org, Name = "CW", Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://x", CredentialSecretRef = "mem://x" });
        h.Db.ClientCompanies.Add(new ClientCompany { Id = CompanyA, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme Dental", ExternalCompanyId = "42" });
        h.Db.ClientUsers.Add(new ClientUser { Id = AdminUser, MspOrganizationId = Org, ClientCompanyId = CompanyA, Email = "admin@a.test", DisplayName = "Admin A", IdpSubject = "sub-admin", IsCompanyAdministrator = true });
        h.Db.ClientUsers.Add(new ClientUser { Id = RegularUser, MspOrganizationId = Org, ClientCompanyId = CompanyA, Email = "user@a.test", DisplayName = "User A", IdpSubject = "sub-user" });
        h.Db.SaveChanges();
        return (new ClientContentService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Clock, new FakeDelivery()), h);
    }

    private sealed class FakeDelivery : IReportDelivery
    {
        public Task<ReportDeliveryResult> DeliverAsync(Guid organizationId, string? recipients, string subject, string fileName, string csv, CancellationToken ct = default)
            => Task.FromResult(new ReportDeliveryResult(false, "test"));
    }

    private static Ticket Ticket(string status, decimal worked, decimal billable) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.ConnectWisePsa,
        ExternalTicketId = Guid.NewGuid().ToString()[..8], ClientCompanyId = CompanyA, RequesterUserId = AdminUser,
        RequesterName = "R", RequesterEmail = "r@test", Title = "T " + status, PortalStatus = status, PortalPriority = "NORMAL",
        TimeWorkedHours = worked, BillableHours = billable,
    };

    [Fact]
    public async Task Announcement_publish_stamps_published_at_and_sorts_pinned_first()
    {
        var (svc, _) = Build();
        var draft = await svc.SaveAnnouncementAsync(AdminAccess, new AnnouncementInput(null, "Draft notice", "body", false, false));
        draft.PublishedAt.Should().BeNull();

        var published = await svc.SaveAnnouncementAsync(AdminAccess, new AnnouncementInput(null, "Live notice", "body", true, true));
        published.PublishedAt.Should().NotBeNull();

        var list = await svc.ListAnnouncementsAsync(AdminAccess);
        list.Should().HaveCount(2);
        list[0].Title.Should().Be("Live notice"); // pinned + published sorts first
        list[0].AuthorName.Should().Be("Admin A");
    }

    [Fact]
    public async Task Announcement_requires_a_title_and_delete_works()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<ValidationFailedException>(() => svc.SaveAnnouncementAsync(AdminAccess, new AnnouncementInput(null, "  ", null, false, true)));
        var a = await svc.SaveAnnouncementAsync(AdminAccess, new AnnouncementInput(null, "X", null, false, true));
        await svc.DeleteAnnouncementAsync(AdminAccess, a.Id);
        (await svc.ListAnnouncementsAsync(AdminAccess)).Should().BeEmpty();
    }

    [Fact]
    public async Task Branding_upserts_a_single_row()
    {
        var (svc, h) = Build();
        await svc.SaveBrandingAsync(AdminAccess, new BrandingInput("Acme", "https://x/logo.png", "#123456"));
        await svc.SaveBrandingAsync(AdminAccess, new BrandingInput("Acme Portal", null, "#654321"));
        h.Db.ClientBrandings.Count(b => b.ClientCompanyId == CompanyA).Should().Be(1);
        var b = await svc.GetBrandingAsync(AdminAccess);
        b.DisplayName.Should().Be("Acme Portal");
        b.AccentColor.Should().Be("#654321");
        b.LogoUrl.Should().BeNull();
    }

    [Fact]
    public async Task Report_counts_by_status_and_sums_hours()
    {
        var (svc, h) = Build();
        h.Db.Tickets.AddRange(
            Ticket("NEW", 1.0m, 1.0m), Ticket("NEW", 0.5m, 0m),
            Ticket("IN_PROGRESS", 2.0m, 2.0m), Ticket("CLOSED", 3.0m, 1.5m));
        await h.Db.SaveChangesAsync();

        var r = await svc.GetReportAsync(AdminAccess);
        r.TotalTickets.Should().Be(4);
        r.OpenTickets.Should().Be(3); // NEW x2 + IN_PROGRESS (CLOSED is not open)
        r.HoursLogged.Should().Be(6.5m);
        r.BillableHours.Should().Be(4.5m);
        r.ByStatus.First().Status.Should().Be("NEW"); // most common first
        r.Recent.Should().HaveCount(4);
    }

    [Fact]
    public async Task Non_admin_without_grant_is_forbidden_on_each_section()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAnnouncementsAsync(UserAccess));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetBrandingAsync(UserAccess));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetReportAsync(UserAccess));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListFaqAsync(UserAccess));
    }

    [Fact]
    public async Task Faq_saves_updates_groups_by_category_and_deletes()
    {
        var (svc, _) = Build();
        await svc.SaveFaqAsync(AdminAccess, new FaqArticleInput(null, "Reset password?", "Use the portal link.", "Access", true, 1));
        var draft = await svc.SaveFaqAsync(AdminAccess, new FaqArticleInput(null, "Order more licenses?", "Contact billing.", "Billing", false, 1));

        var list = await svc.ListFaqAsync(AdminAccess);
        list.Should().HaveCount(2);
        list.Select(f => f.Category).Should().Contain(new[] { "Access", "Billing" });
        list.Single(f => f.Question == "Order more licenses?").IsPublished.Should().BeFalse();

        // Update in place (same row, edited answer + published).
        var updated = await svc.SaveFaqAsync(AdminAccess, new FaqArticleInput(draft.Id, "Order more licenses?", "Email billing@acme.", "Billing", true, 1));
        updated.Id.Should().Be(draft.Id);
        updated.IsPublished.Should().BeTrue();
        (await svc.ListFaqAsync(AdminAccess)).Should().HaveCount(2);

        await svc.DeleteFaqAsync(AdminAccess, draft.Id);
        (await svc.ListFaqAsync(AdminAccess)).Should().ContainSingle();
    }

    [Fact]
    public async Task Faq_requires_a_question()
        => await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Build().svc.SaveFaqAsync(AdminAccess, new FaqArticleInput(null, "   ", "a", null, true, 0)));

    [Fact]
    public async Task Export_renders_csv_with_the_report_numbers()
    {
        var (svc, h) = Build();
        h.Db.Tickets.AddRange(Ticket("NEW", 1.0m, 1.0m), Ticket("CLOSED", 2.5m, 0m));
        await h.Db.SaveChangesAsync();

        var export = await svc.ExportReportAsync(AdminAccess);
        export.FileName.Should().EndWith(".csv");
        export.Csv.Should().Contain("Total tickets,2");
        export.Csv.Should().Contain("Hours logged,3.50");
        export.Csv.Should().Contain("Tickets by status");
    }

    [Fact]
    public async Task Schedule_save_sets_a_future_next_run_and_run_now_produces_a_downloadable_run()
    {
        var (svc, h) = Build();
        h.Db.Tickets.Add(Ticket("NEW", 1m, 1m));
        await h.Db.SaveChangesAsync();

        var sched = await svc.SaveScheduleAsync(AdminAccess, new ReportScheduleInput(null, "Weekly summary", "weekly", "ops@acme.test", true));
        sched.Frequency.Should().Be("weekly");
        sched.NextRunAt.Should().BeAfter(h.Clock.GetUtcNow());

        var run = await svc.RunScheduleNowAsync(AdminAccess, sched.Id);
        run.Summary.Should().Contain("1 tickets");
        run.Delivered.Should().BeFalse(); // no email provider in tests
        run.DeliveryNote.Should().Be("test");

        var runs = await svc.ListRunsAsync(AdminAccess);
        runs.Should().ContainSingle();
        var dl = await svc.DownloadRunAsync(AdminAccess, run.Id);
        dl.Csv.Should().Contain("Total tickets,1");
    }

    [Fact]
    public async Task Scheduled_runner_generates_due_schedules_and_advances_next_run()
    {
        var (_, h) = Build();
        h.Db.Tickets.Add(Ticket("NEW", 1m, 1m));
        // A schedule already due (NextRunAt in the past).
        var past = h.Clock.GetUtcNow().AddMinutes(-1);
        h.Db.ReportSchedules.Add(new ReportSchedule { MspOrganizationId = Org, ClientCompanyId = CompanyA, Name = "Due", Frequency = ReportFrequency.Daily, IsEnabled = true, NextRunAt = past });
        await h.Db.SaveChangesAsync();

        var runner = new ScheduledReportRunner(h.Db, h.Clock, new FakeDelivery(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduledReportRunner>.Instance);
        var count = await runner.RunDueAsync();
        count.Should().Be(1);

        h.Db.ReportRuns.Count(r => r.ClientCompanyId == CompanyA).Should().Be(1);
        var sched = h.Db.ReportSchedules.Single();
        sched.LastRunAt.Should().NotBeNull();
        sched.NextRunAt.Should().BeAfter(h.Clock.GetUtcNow()); // advanced into the future
    }

    [Fact]
    public async Task A_knowledge_base_grant_opens_only_faq()
    {
        var (svc, h) = Build();
        h.Db.ClientAccessGrants.Add(new ClientAccessGrant { ClientUserId = RegularUser, Section = ControlPanelSection.KnowledgeBase, MspOrganizationId = Org });
        await h.Db.SaveChangesAsync();

        (await svc.ListFaqAsync(UserAccess)).Should().BeEmpty(); // allowed
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAnnouncementsAsync(UserAccess)); // still closed
    }

    [Fact]
    public async Task A_reports_grant_opens_only_reports()
    {
        var (svc, h) = Build();
        h.Db.ClientAccessGrants.Add(new ClientAccessGrant { ClientUserId = RegularUser, Section = ControlPanelSection.Reports, MspOrganizationId = Org });
        await h.Db.SaveChangesAsync();

        (await svc.GetReportAsync(UserAccess)).TotalTickets.Should().Be(0); // allowed
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListAnnouncementsAsync(UserAccess)); // still closed
    }
}
