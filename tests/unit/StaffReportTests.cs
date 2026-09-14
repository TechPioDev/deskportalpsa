using Desk.Application.Abstractions;
using Desk.Application.Analytics;
using Desk.Application.Common;
using Desk.Application.Reporting;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Reporting;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Reporting;
using Desk.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>Scheduled staff reports: which period a run covers, what it counts, and who it reaches.</summary>
public class StaffReportTests
{
    // The API and worker run on Linux, where IANA ids resolve. This repo builds with invariant
    // globalization, which on a Windows dev box leaves only Windows ids - so pick the platform's name.
    private static readonly string KolkataId = OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata";
    private static readonly TimeZoneInfo Kolkata = StaffReportPeriods.Zone(KolkataId);

    // ---- periods -------------------------------------------------------------------------------

    [Theory]
    // now (UTC)                  frequency                           start         end
    [InlineData("2026-09-14T03:00Z", StaffReportFrequency.Daily,     "2026-09-13", "2026-09-13")]
    [InlineData("2026-09-14T03:00Z", StaffReportFrequency.Weekly,    "2026-09-07", "2026-09-13")] // Mon 14 Sep → last Mon–Sun
    [InlineData("2026-09-14T03:00Z", StaffReportFrequency.Monthly,   "2026-08-01", "2026-08-31")]
    [InlineData("2026-09-14T03:00Z", StaffReportFrequency.Quarterly, "2026-04-01", "2026-06-30")]
    [InlineData("2026-01-01T03:00Z", StaffReportFrequency.Monthly,   "2025-12-01", "2025-12-31")]
    [InlineData("2026-01-01T03:00Z", StaffReportFrequency.Quarterly, "2025-10-01", "2025-12-31")]
    [InlineData("2024-03-01T03:00Z", StaffReportFrequency.Monthly,   "2024-02-01", "2024-02-29")] // leap year
    public void A_run_covers_the_last_complete_period(string now, StaffReportFrequency frequency, string start, string end)
    {
        var (s, e) = StaffReportPeriods.LastComplete(frequency, DateTimeOffset.Parse(now), Kolkata);

        (s.ToString("yyyy-MM-dd"), e.ToString("yyyy-MM-dd")).Should().Be((start, end));
    }

    [Fact]
    public void Yesterday_is_the_organizations_yesterday_not_UTCs()
    {
        // 20:00 UTC on the 13th is already 01:30 on the 14th in India: yesterday there is the 13th.
        var (start, _) = StaffReportPeriods.LastComplete(StaffReportFrequency.Daily, DateTimeOffset.Parse("2026-09-13T20:00Z"), Kolkata);

        start.Should().Be(new DateOnly(2026, 9, 13));
    }

    [Theory]
    [InlineData("2026-09-14T00:30Z", StaffReportFrequency.Daily,     "2026-09-14T01:30Z")] // 07:00 IST today, not yet passed
    [InlineData("2026-09-14T02:00Z", StaffReportFrequency.Daily,     "2026-09-15T01:30Z")] // already sent today → tomorrow
    [InlineData("2026-09-14T02:00Z", StaffReportFrequency.Weekly,    "2026-09-21T01:30Z")] // next Monday
    [InlineData("2026-09-14T02:00Z", StaffReportFrequency.Monthly,   "2026-10-01T01:30Z")]
    [InlineData("2026-09-14T02:00Z", StaffReportFrequency.Quarterly, "2026-10-01T01:30Z")]
    [InlineData("2026-10-02T00:00Z", StaffReportFrequency.Quarterly, "2027-01-01T01:30Z")]
    public void The_next_run_is_seven_in_the_morning_local_after_the_period_closes(string now, StaffReportFrequency frequency, string expected)
    {
        StaffReportPeriods.NextRun(frequency, DateTimeOffset.Parse(now), Kolkata)
            .Should().Be(DateTimeOffset.Parse(expected));
    }

    [Fact]
    public void Period_bounds_run_from_local_midnight_to_the_end_of_the_last_local_day()
    {
        var (from, to) = StaffReportPeriods.UtcBounds(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), Kolkata);

        from.Should().Be(DateTimeOffset.Parse("2026-07-31T18:30Z"));
        to.Should().Be(DateTimeOffset.Parse("2026-08-31T18:30Z").AddTicks(-1));
    }

    // ---- content and delivery ------------------------------------------------------------------

    private sealed class FakeEmail(bool configured = true) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool IsConfigured => configured;
        public string? FromAddress => "reports@techpio.test";
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private sealed record Org(Guid Id, Guid Conn, Guid Client, Guid Ticket, Guid Person);

    private static async Task<Org> SeedOrgAsync(DeskDbContext platformDb, string name, string person, decimal augustHours)
    {
        var org = new MspOrganization { Name = name, Slug = name.ToLowerInvariant(), TimeZone = KolkataId };
        var conn = new PsaConnection { MspOrganizationId = org.Id, Name = "AT", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://x", CredentialSecretRef = "m" };
        var client = new ClientCompany { MspOrganizationId = org.Id, PsaConnectionId = conn.Id, Name = name + " Client", ExternalCompanyId = "1" };
        var user = new AppUser { MspOrganizationId = org.Id, Email = person.Replace(' ', '.') + "@x.test", DisplayName = person };
        var ticket = new Ticket
        {
            MspOrganizationId = org.Id, PsaConnectionId = conn.Id, Provider = ProviderType.AutotaskPsa, ClientCompanyId = client.Id,
            RequesterName = "R", RequesterEmail = "r@x.test", Title = "t", PortalStatus = "RESOLVED", PortalPriority = "NORMAL",
            AssignedAppUserId = user.Id, CreatedAt = D("2026-08-03"), PsaCreatedAt = D("2026-08-03"), ResolvedAt = D("2026-08-20"),
        };
        platformDb.AddRange(org, conn, client, user, ticket,
            Time(org.Id, ticket.Id, user.Id, "2026-08-05", augustHours),
            Time(org.Id, ticket.Id, user.Id, "2026-07-31T12:00", 9m), // before the period (July in India)
            Time(org.Id, ticket.Id, user.Id, "2026-09-01T12:00", 9m)); // after it
        await platformDb.SaveChangesAsync();
        return new Org(org.Id, conn.Id, client.Id, ticket.Id, user.Id);
    }

    private static DateTimeOffset D(string s) => DateTimeOffset.Parse(s.Contains('T') ? s + "Z" : s + "T06:00Z");

    private static TicketTimeEntry Time(Guid org, Guid ticket, Guid user, string day, decimal hours) => new()
    {
        MspOrganizationId = org, TicketId = ticket, AppUserId = user, Hours = hours, Billable = true, EntryDate = D(day),
    };

    /// <summary>The same scoped graph the worker builds, over one shared in-memory database.</summary>
    private static (ServiceProvider Sp, FakeEmail Email, TestClock Clock, string Db) Services(bool emailConfigured = true)
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-01T02:00Z")); // 07:30 IST on 1 Sep
        var email = new FakeEmail(emailConfigured);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IEmailSender>(email);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ISettableTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped(sp => new DeskDbContext(
            // Same options as TestDbContextFactory: EF keys the in-memory store by its whole
            // configuration, so differing options would silently give each side its own database.
            new DbContextOptionsBuilder<DeskDbContext>().UseInMemoryDatabase(dbName).EnableSensitiveDataLogging().Options,
            sp.GetRequiredService<TenantContext>(), clock));
        services.AddScoped<ITechnicianMetricsService>(sp => new TechnicianMetricsService(sp.GetRequiredService<DeskDbContext>(), new ProductivityScorer(), clock));
        services.AddScoped<TechnicianReportBuilder>();
        services.AddScoped<IStaffReportContent, StaffReportContent>();
        services.AddScoped<StaffReportGenerator>();
        services.AddSingleton<IStaffReportRunner, StaffReportRunner>();
        return (services.BuildServiceProvider(), email, clock, dbName);
    }

    [Fact]
    public async Task A_monthly_report_counts_only_the_month_and_emails_the_pdf_and_csv()
    {
        var (sp, email, _, dbName) = Services();
        var platform = TestDbContextFactory.ForPlatform(dbName);
        var org = await SeedOrgAsync(platform, "Techpio", "Basit Lone", 6.5m);
        platform.StaffReportSchedules.Add(new StaffReportSchedule
        {
            MspOrganizationId = org.Id, Name = "Monthly team", Frequency = StaffReportFrequency.Monthly,
            Recipients = "manager@techpio.test", NextRunAt = DateTimeOffset.Parse("2026-09-01T01:30Z"),
        });
        await platform.SaveChangesAsync();

        (await sp.GetRequiredService<IStaffReportRunner>().RunDueAsync()).Should().Be(1);

        var run = await TestDbContextFactory.ForPlatform(dbName).StaffReportRuns.SingleAsync();
        run.Title.Should().Be("Technician productivity — August 2026");
        (run.PeriodStart, run.PeriodEnd).Should().Be((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));
        run.Csv.Should().Contain("Basit Lone,6.5,6.5,1,1,6.5,");
        run.Pdf.Take(4).Should().Equal("%PDF"u8.ToArray());
        run.Delivered.Should().BeTrue();

        var message = email.Sent.Single();
        message.To.Should().Equal("manager@techpio.test");
        message.Attachments!.Select(a => a.FileName).Should().Equal(
            "technician-productivity-august-2026.pdf", "technician-productivity-august-2026.csv");
        message.TextBody.Should().Contain("Basit Lone: 6.5h, 1 resolved");

        var schedule = await TestDbContextFactory.ForPlatform(dbName).StaffReportSchedules.SingleAsync();
        schedule.NextRunAt.Should().Be(DateTimeOffset.Parse("2026-10-01T01:30Z"));
        schedule.LastRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Each_organizations_report_counts_only_its_own_people()
    {
        // The runner finds due schedules under platform scope. If it also GENERATED under platform
        // scope, the tenant filter would be off and one MSP's report would include another's staff.
        var (sp, email, _, dbName) = Services();
        var platform = TestDbContextFactory.ForPlatform(dbName);
        var a = await SeedOrgAsync(platform, "Techpio", "Basit Lone", 6m);
        var b = await SeedOrgAsync(platform, "Globex", "Grace Hopper", 3m);
        foreach (var org in new[] { a, b })
            platform.StaffReportSchedules.Add(new StaffReportSchedule
            {
                MspOrganizationId = org.Id, Name = "Monthly", Frequency = StaffReportFrequency.Monthly,
                Recipients = "m@x.test", NextRunAt = DateTimeOffset.Parse("2026-09-01T00:00Z"),
            });
        await platform.SaveChangesAsync();

        await sp.GetRequiredService<IStaffReportRunner>().RunDueAsync();

        var runs = await TestDbContextFactory.ForPlatform(dbName).StaffReportRuns.ToListAsync();
        runs.Should().HaveCount(2);
        var techpio = runs.Single(r => r.MspOrganizationId == a.Id).Csv;
        techpio.Should().Contain("Basit Lone").And.NotContain("Grace Hopper");
        var globex = runs.Single(r => r.MspOrganizationId == b.Id).Csv;
        globex.Should().Contain("Grace Hopper").And.NotContain("Basit Lone");
    }

    [Fact]
    public async Task Without_email_the_run_is_kept_and_says_why_nobody_received_it()
    {
        var (sp, email, _, dbName) = Services(emailConfigured: false);
        var platform = TestDbContextFactory.ForPlatform(dbName);
        var org = await SeedOrgAsync(platform, "Techpio", "Basit Lone", 2m);
        platform.StaffReportSchedules.Add(new StaffReportSchedule
        {
            MspOrganizationId = org.Id, Name = "Daily", Frequency = StaffReportFrequency.Daily,
            Recipients = "m@x.test", NextRunAt = DateTimeOffset.Parse("2026-09-01T00:00Z"),
        });
        await platform.SaveChangesAsync();

        await sp.GetRequiredService<IStaffReportRunner>().RunDueAsync();

        var run = await TestDbContextFactory.ForPlatform(dbName).StaffReportRuns.SingleAsync();
        run.Delivered.Should().BeFalse();
        run.DeliveryNote.Should().Contain("not configured");
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_schedule_not_yet_due_does_not_run()
    {
        var (sp, email, _, dbName) = Services();
        var platform = TestDbContextFactory.ForPlatform(dbName);
        var org = await SeedOrgAsync(platform, "Techpio", "Basit Lone", 2m);
        platform.StaffReportSchedules.Add(new StaffReportSchedule
        {
            MspOrganizationId = org.Id, Name = "Weekly", Frequency = StaffReportFrequency.Weekly,
            Recipients = "m@x.test", NextRunAt = DateTimeOffset.Parse("2026-09-07T01:30Z"),
        });
        await platform.SaveChangesAsync();

        (await sp.GetRequiredService<IStaffReportRunner>().RunDueAsync()).Should().Be(0);
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_report_counts_only_that_clients_tickets()
    {
        var (sp, _, _, dbName) = Services();
        var platform = TestDbContextFactory.ForPlatform(dbName);
        var org = await SeedOrgAsync(platform, "Techpio", "Basit Lone", 4m);
        var otherClient = new ClientCompany { MspOrganizationId = org.Id, PsaConnectionId = org.Conn, Name = "Other", ExternalCompanyId = "2" };
        var otherTicket = new Ticket
        {
            MspOrganizationId = org.Id, PsaConnectionId = org.Conn, Provider = ProviderType.AutotaskPsa, ClientCompanyId = otherClient.Id,
            RequesterName = "R", RequesterEmail = "r@x.test", Title = "t", PortalStatus = "NEW", PortalPriority = "NORMAL",
            CreatedAt = D("2026-08-03"),
        };
        platform.AddRange(otherClient, otherTicket, Time(org.Id, otherTicket.Id, org.Person, "2026-08-06", 10m));
        await platform.SaveChangesAsync();

        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org.Id);
        var builder = scope.ServiceProvider.GetRequiredService<TechnicianReportBuilder>();
        var (from, to) = StaffReportPeriods.UtcBounds(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), Kolkata);

        var whole = await builder.BuildAsync(org.Id, from, to, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), "August 2026", null, default);
        var oneClient = await builder.BuildAsync(org.Id, from, to, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), "August 2026", org.Client, default);

        whole.TotalHours.Should().Be(14m);
        oneClient.TotalHours.Should().Be(4m);
        oneClient.Title.Should().Be("Technician productivity — Techpio Client — August 2026");
    }

    [Fact]
    public void The_pdf_renders_an_empty_period_and_awkward_names()
    {
        var empty = new TechnicianReport("Techpio", "Yesterday", new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 13),
            null, [], [], DateTimeOffset.Parse("2026-09-14T01:30Z"), "Asia/Kolkata");
        var busy = empty with
        {
            Technicians = Enumerable.Range(1, 60)
                .Select(i => new TechnicianReportRow($"Ñandú Østergård-Łukasiewicz {i} =SUM(A1)", 7.25m, 5m, i % 4, 3, 1)).ToList(),
            Days = Enumerable.Range(1, 30).Select(i => new TechnicianReportDay(new DateOnly(2026, 8, i), 12.5m, 3)).ToList(),
        };

        TechnicianReportRenderer.ToPdf(empty).Take(4).Should().Equal("%PDF"u8.ToArray());
        TechnicianReportRenderer.ToPdf(busy).Length.Should().BeGreaterThan(5_000);
        // A name that starts like a formula stays text in the CSV.
        TechnicianReportRenderer.ToCsv(busy with { Technicians = [new TechnicianReportRow("=cmd", 1, 1, 0, 1, 1)] })
            .Should().Contain("'=cmd,1,1,0,1,1,1");
    }
}
