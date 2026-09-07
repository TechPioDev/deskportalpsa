using Desk.Application.Marketing;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Marketing;
using Desk.Infrastructure.Marketing;
using Desk.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The public forms are the only unauthenticated write in the product, so what they accept and
/// refuse is a security boundary, not a validation nicety.
/// </summary>
public class EnquiryTests
{
    private static (EnquiryService Svc, DeskDbContext Db) Build()
    {
        var h = AdminHarness.Create(Guid.NewGuid());
        return (new EnquiryService(h.Db, h.Clock), h.Db);
    }

    private static SubmitEnquiryInput Input(
        string name = "Dana Reed", string email = "dana@acme.test", string message = "Can you sync Halo?",
        string? website = null) =>
        new(EnquiryKind.Contact, name, email, "Acme", "555", message, null, "/contact", website);

    [Fact]
    public async Task A_valid_enquiry_is_stored()
    {
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Input())).Accepted.Should().BeTrue();

        var row = await db.Enquiries.SingleAsync();
        row.Name.Should().Be("Dana Reed");
        row.Status.Should().Be(EnquiryStatus.New);
        row.SourcePage.Should().Be("/contact");
    }

    [Theory]
    [InlineData("", "dana@acme.test", "hello")]
    [InlineData("Dana", "", "hello")]
    [InlineData("Dana", "dana@acme.test", "")]
    [InlineData("Dana", "not-an-email", "hello")]
    [InlineData("Dana", "dana@localhost", "hello")]  // no dot in host: unreachable in practice
    public async Task Unusable_submissions_are_refused_and_store_nothing(string name, string email, string message)
    {
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Input(name, email, message))).Accepted.Should().BeFalse();
        (await db.Enquiries.CountAsync()).Should().Be(0);
    }

    private static SubmitEnquiryInput Meeting(
        string? company = "Acme", string? phone = "+44 7700 900123", string? preferred = "Tuesday 10:00 (Europe/London)") =>
        new(EnquiryKind.Meeting, "Dana Reed", "dana@acme.test", company, phone, "Can we see it?", preferred, "/book", null);

    [Fact]
    public async Task A_meeting_request_carries_the_scheduling_details()
    {
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Meeting())).Accepted.Should().BeTrue();

        var row = await db.Enquiries.SingleAsync();
        row.Kind.Should().Be(EnquiryKind.Meeting);
        row.Company.Should().Be("Acme");
        row.Phone.Should().Be("+44 7700 900123");
        row.PreferredTime.Should().Be("Tuesday 10:00 (Europe/London)");
    }

    [Theory]
    [InlineData(null, "+44 7700 900123", "Tuesday 10:00")]   // no company
    [InlineData("Acme", null, "Tuesday 10:00")]               // no phone
    [InlineData("Acme", "+44 7700 900123", null)]             // no time
    [InlineData("Acme", "   ", "Tuesday 10:00")]              // whitespace is not a phone number
    public async Task A_meeting_request_missing_a_required_detail_is_refused(string? company, string? phone, string? preferred)
    {
        // The browser marks these required, but the endpoint is anonymous — a caller can skip the
        // form entirely, so the rule has to hold here or it does not hold at all.
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Meeting(company, phone, preferred))).Accepted.Should().BeFalse();
        (await db.Enquiries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_contact_message_still_needs_no_company_or_phone()
    {
        // The same endpoint serves both forms; tightening the meeting rules must not quietly make
        // a general question harder to send.
        var (svc, db) = Build();
        await using var _ = db;

        var bare = new SubmitEnquiryInput(
            EnquiryKind.Contact, "Dana Reed", "dana@acme.test", null, null, "Do you support HaloPSA?", null, "/contact", null);

        (await svc.SubmitAsync(bare)).Accepted.Should().BeTrue();
        (await db.Enquiries.SingleAsync()).Company.Should().BeNull();
    }

    [Fact]
    public async Task A_tripped_honeypot_looks_like_success_but_stores_nothing()
    {
        var (svc, db) = Build();
        await using var _ = db;

        // Reporting the block would tell a bot exactly which field to stop filling in.
        (await svc.SubmitAsync(Input(website: "http://spam.example"))).Accepted.Should().BeTrue();
        (await db.Enquiries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_oversized_message_is_refused_and_names_its_limit()
    {
        // This used to store the first 4,000 characters and answer 202. The sender was thanked,
        // the rest of what they wrote was gone, and the message staff read ended mid-sentence
        // with nothing to mark the cut - so the reply answered a question nobody had finished.
        var (svc, db) = Build();
        await using var _ = db;

        var result = await svc.SubmitAsync(Input(message: new string('x', 9_000)));

        result.Accepted.Should().BeFalse();
        result.Refusal.Should().Be(EnquiryRefusal.TooLong);
        result.Field.Should().Be("message");
        result.Limit.Should().Be(Enquiry.MessageMax);
        (await db.Enquiries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_message_exactly_at_the_limit_is_still_accepted()
    {
        // The boundary in the other direction: a rule that rejects one character early is a rule
        // that turns away real enquiries, and nobody would ever be told why.
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Input(message: new string('x', Enquiry.MessageMax))))
            .Accepted.Should().BeTrue();

        (await db.Enquiries.SingleAsync()).Message.Length.Should().Be(Enquiry.MessageMax);
    }

    [Fact]
    public async Task Whitespace_past_the_limit_is_trimmed_rather_than_counted()
    {
        // A textarea that ends in a newline should not cost someone their enquiry over a character
        // they cannot see. Trim happens before the length check, so this is the full limit plus
        // padding and it belongs.
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Input(message: new string('x', Enquiry.MessageMax) + "\n   \t")))
            .Accepted.Should().BeTrue();

        (await db.Enquiries.SingleAsync()).Message.Length.Should().Be(Enquiry.MessageMax);
    }

    [Theory]
    [InlineData("name", 120)]
    [InlineData("email address", 200)]
    [InlineData("company", 160)]
    [InlineData("phone number", 60)]
    [InlineData("preferred time", 200)]
    public async Task Every_visitor_supplied_field_is_refused_when_oversized_not_shortened(string field, int limit)
    {
        // Not only the message. A clipped phone number reaches nobody and a clipped email address
        // can still parse as valid - a truncated address is the one that silently routes a reply
        // to the wrong person, or to no one.
        var (svc, db) = Build();
        await using var _ = db;

        var big = new string('x', limit + 1);
        var input = new SubmitEnquiryInput(
            EnquiryKind.Meeting,
            field == "name" ? big : "Dana Reed",
            field == "email address" ? big + "@acme.test" : "dana@acme.test",
            field == "company" ? big : "Acme",
            field == "phone number" ? big : "+44 7700 900123",
            "Can we see it?",
            field == "preferred time" ? big : "Tuesday 10:00",
            "/book", null);

        var result = await svc.SubmitAsync(input);

        result.Refusal.Should().Be(EnquiryRefusal.TooLong);
        result.Field.Should().Be(field);
        result.Limit.Should().Be(limit);
        (await db.Enquiries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_page_the_form_sat_on_is_still_clipped_because_no_visitor_typed_it()
    {
        // The one field the site fills in itself. Refusing a genuine enquiry over the length of
        // our own URL would discard the lead to protect a piece of context.
        var (svc, db) = Build();
        await using var _ = db;

        var input = new SubmitEnquiryInput(
            EnquiryKind.Contact, "Dana Reed", "dana@acme.test", null, null,
            "Do you support HaloPSA?", null, "/contact?" + new string('q', 400), null);

        (await svc.SubmitAsync(input)).Accepted.Should().BeTrue();
        (await db.Enquiries.SingleAsync()).SourcePage!.Length.Should().Be(Enquiry.SourcePageMax);
    }

    [Fact]
    public async Task A_refusal_reports_a_missing_field_differently_from_an_oversized_one()
    {
        // Two refusals that both used to be "false". The form says something different for each,
        // so collapsing them again would send a visitor to check a field that was never wrong.
        var (svc, db) = Build();
        await using var _ = db;

        (await svc.SubmitAsync(Input(name: ""))).Refusal.Should().Be(EnquiryRefusal.Incomplete);
        (await svc.SubmitAsync(Input(name: new string('n', 200)))).Refusal.Should().Be(EnquiryRefusal.TooLong);
        (await db.Enquiries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_list_reports_how_many_are_still_unanswered()
    {
        var (svc, db) = Build();
        await using var _ = db;
        await svc.SubmitAsync(Input(name: "One"));
        await svc.SubmitAsync(Input(name: "Two"));

        var first = (await svc.ListAsync()).Items.First();
        (await svc.SetStatusAsync(first.Id, EnquiryStatus.Closed)).Should().BeTrue();

        var after = await svc.ListAsync();
        after.Total.Should().Be(2);
        after.NewCount.Should().Be(1);
        (await svc.ListAsync(EnquiryStatus.New)).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task An_enquiry_arrives_before_any_tenant_exists_so_it_is_not_tenant_scoped()
    {
        // A tenant filter here would either invent an organization or hide every row from the
        // admin who needs to answer it. This asserts the row is readable with no tenant scope.
        var (svc, db) = Build();
        await using var _ = db;
        await svc.SubmitAsync(Input());

        db.Model.FindEntityType(typeof(Enquiry))!.GetQueryFilter().Should().BeNull();
        (await db.Enquiries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Administrators_and_managers_can_see_enquiries_but_technicians_cannot()
    {
        // The claim has to actually be granted, or the page is unreachable for everyone.
        static IEnumerable<string> Keys(RoleType r) => Permissions.ForRole(r).Select(p => p.Key);

        Keys(RoleType.MspAdministrator).Should().Contain(Permissions.EnquiriesView);
        Keys(RoleType.Manager).Should().Contain(Permissions.EnquiriesView);
        Keys(RoleType.Technician).Should().NotContain(Permissions.EnquiriesView);
        Keys(RoleType.ClientUser).Should().NotContain(Permissions.EnquiriesView);
        Permissions.All.Should().Contain(Permissions.EnquiriesView);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_role_seeded_before_a_permission_existed_gains_it_on_the_next_start()
    {
        // The live symptom this prevents: ship a new claim, deploy, and the feature is invisible
        // because the deployed role rows predate it and the seeder used to skip them.
        // Each phase gets its own context over one database: the scenario is a deployment
        // restarting against storage that already holds roles from an older build.
        var dbName = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();

        await using (var first = AdminHarness.Create(org, dbName).Db)
            await DatabaseSeeder.SeedBuiltInRolesAsync(first);

        await using (var older = AdminHarness.Create(org, dbName).Db)
        {
            var admin = await older.Roles.IgnoreQueryFilters().Include(r => r.Permissions)
                .SingleAsync(r => r.BuiltInType == RoleType.MspAdministrator);
            foreach (var stale in admin.Permissions.Where(p => p.PermissionKey == Permissions.EnquiriesView).ToList())
                admin.Permissions.Remove(stale);
            await older.SaveChangesAsync();
        }

        await using (var restarted = AdminHarness.Create(org, dbName).Db)
            await DatabaseSeeder.SeedBuiltInRolesAsync(restarted);

        await using var check = AdminHarness.Create(org, dbName).Db;
        var after = await check.Roles.IgnoreQueryFilters().Include(r => r.Permissions)
            .SingleAsync(r => r.BuiltInType == RoleType.MspAdministrator);
        after.Permissions.Select(p => p.PermissionKey).Should().Contain(Permissions.EnquiriesView);
    }
}
