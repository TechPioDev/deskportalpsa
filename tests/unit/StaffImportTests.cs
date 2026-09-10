using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Application.Attachments;
using Desk.Domain.Organization;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Importing staff from a spreadsheet.
///
/// The behaviour that matters is what happens to the rows that are WRONG. A file of forty is past
/// the number anyone checks by eye, so an import that aborts on the first bad row costs the other
/// thirty-nine, and one that silently overwrites an existing colleague is worse than either.
/// </summary>
public class StaffImportTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed class Fixture
    {
        public required AdminHarness H { get; init; }
        public required UserAdminService Svc { get; init; }
        public required Guid TechnicianRole { get; init; }
        public required Guid ItSupport { get; init; }
    }

    private static async Task<Fixture> SetupAsync()
    {
        var h = AdminHarness.Create(Org);
        await DatabaseSeeder.SeedBuiltInRolesAsync(h.Db);

        var technician = await h.Db.Roles.IgnoreQueryFilters()
            .FirstAsync(r => r.BuiltInType == RoleType.Technician);

        var dept = new Department { MspOrganizationId = Org, Name = "IT Support", IsActive = true };
        var retired = new Department { MspOrganizationId = Org, Name = "Old Team", IsActive = false };
        h.Db.Departments.AddRange(dept, retired);
        await h.Db.SaveChangesAsync();

        return new Fixture
        {
            H = h,
            Svc = new UserAdminService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Tenant, h.User,
                new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock),
                new EffectivePermissionService(h.Db), h.Clock),
            TechnicianRole = technician.Id,
            ItSupport = dept.Id,
        };
    }

    private static ImportStaffUsersInput Input(Fixture f, bool dryRun, params ImportStaffUserRow[] rows)
        => new(rows, [f.TechnicianRole], dryRun);

    private static ImportStaffUserRow Row(string name, string email, string? dept = "IT Support")
        => new(name, email, dept);

    [Fact]
    public async Task A_dry_run_reports_what_would_happen_and_writes_nothing()
    {
        // The reason the endpoint has two modes. Someone about to create forty accounts should see
        // the outcome first, and seeing it must not be the thing that creates them.
        var f = await SetupAsync();

        var result = await f.Svc.ImportAsync(Input(f, dryRun: true,
            Row("Basit Lone", "basit@techpio.com"),
            Row("Komal Sharma", "komal@techpio.com")));

        result.DryRun.Should().BeTrue();
        result.Created.Should().Be(2);
        (await f.H.Db.AppUsers.CountAsync()).Should().Be(0, "a preview that creates users is not a preview");
    }

    [Fact]
    public async Task A_real_run_creates_the_users_with_their_role_and_department()
    {
        var f = await SetupAsync();

        var result = await f.Svc.ImportAsync(Input(f, dryRun: false, Row("Basit Lone", "basit@techpio.com")));

        result.Created.Should().Be(1);
        var user = await f.H.Db.AppUsers.Include(u => u.Roles).SingleAsync();
        user.DisplayName.Should().Be("Basit Lone");
        user.IdpSubject.Should().BeNull("a new row is an invitation until its owner first signs in");
        user.Roles.Should().ContainSingle().Which.RoleId.Should().Be(f.TechnicianRole);

        var dept = await f.H.Db.UserDepartments.SingleAsync();
        dept.DepartmentId.Should().Be(f.ItSupport);
        dept.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_colleague_is_skipped_never_overwritten()
    {
        // The dangerous case. An import that updates on conflict would let a stale spreadsheet
        // rename a colleague or move them between departments, and the run would report success.
        var f = await SetupAsync();
        await f.Svc.ImportAsync(Input(f, dryRun: false, Row("Basit Lone", "basit@techpio.com")));

        var again = await f.Svc.ImportAsync(Input(f, dryRun: false,
            Row("Basit Lone WRONG NAME", "BASIT@techpio.com", dept: null)));

        again.AlreadyExisted.Should().Be(1);
        again.Created.Should().Be(0);
        (await f.H.Db.AppUsers.SingleAsync()).DisplayName.Should().Be("Basit Lone",
            "matching is case-insensitive, and the existing record wins");
    }

    [Fact]
    public async Task One_bad_row_does_not_cost_the_good_ones()
    {
        // A single malformed address in a file of forty must not throw away the other thirty-nine,
        // and the caller has to be told precisely which row failed.
        var f = await SetupAsync();

        var result = await f.Svc.ImportAsync(Input(f, dryRun: false,
            Row("Basit Lone", "basit@techpio.com"),
            Row("Broken Row", "not-an-email"),
            Row("Komal Sharma", "komal@techpio.com")));

        result.Created.Should().Be(2);
        result.Invalid.Should().Be(1);
        result.Rows.Single(r => r.Outcome == ImportRowOutcome.Invalid).Email.Should().Be("not-an-email");
        (await f.H.Db.AppUsers.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task The_preview_and_the_real_run_agree_row_for_row()
    {
        // Found by using the feature rather than by reading it: the preview reported a malformed
        // address as "will be created" because the format check lived only inside the single-user
        // create, which a dry run never calls. It promised five and would have delivered four.
        //
        // A preview that over-promises is worse than none, because it is believed - so this asserts
        // the two paths reach the SAME verdict for every row, not merely that each is sane alone.
        var f = await SetupAsync();
        var file = Input(f, dryRun: true,
            Row("Basit Lone", "basit@techpio.com"),
            Row("Broken Row", "not-an-email"),
            Row("X", "shortname@techpio.com"),
            Row("Typo Dept", "typo@techpio.com", dept: "IT Suport"),
            Row("Komal Sharma", "komal@techpio.com"));

        var preview = await f.Svc.ImportAsync(file);
        var applied = await f.Svc.ImportAsync(file with { DryRun = false });

        preview.Rows.Select(r => (r.Email, r.Outcome))
            .Should().BeEquivalentTo(applied.Rows.Select(r => (r.Email, r.Outcome)));
        preview.Created.Should().Be(applied.Created).And.Be(2);
        preview.Invalid.Should().Be(applied.Invalid).And.Be(3);
    }

    [Fact]
    public async Task A_department_that_does_not_exist_is_refused_rather_than_created()
    {
        // A typo would otherwise become a permanent department, and the row that caused it would
        // look like it imported perfectly. An inactive one is refused for the same reason: it was
        // retired deliberately, and an import is not the place to quietly resurrect it.
        var f = await SetupAsync();

        var result = await f.Svc.ImportAsync(Input(f, dryRun: false,
            Row("Typo Row", "typo@techpio.com", dept: "IT Suport"),
            Row("Retired Row", "retired@techpio.com", dept: "Old Team")));

        result.Invalid.Should().Be(2);
        result.Rows.Should().OnlyContain(r => r.Reason!.Contains("No active department"));
        (await f.H.Db.Departments.CountAsync()).Should().Be(2, "no department was invented");
        (await f.H.Db.AppUsers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Someone_listed_twice_in_one_file_is_created_once()
    {
        // Caught here rather than by the second insert failing: a spreadsheet listing someone twice
        // is an ordinary mistake, and it should read as "already exists", not as an error.
        var f = await SetupAsync();

        var result = await f.Svc.ImportAsync(Input(f, dryRun: false,
            Row("Basit Lone", "basit@techpio.com"),
            Row("Basit Lone", "basit@techpio.com")));

        result.Created.Should().Be(1);
        result.AlreadyExisted.Should().Be(1);
        (await f.H.Db.AppUsers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_row_with_no_department_still_imports()
    {
        // The column is optional. Refusing the row would make a department mandatory by accident,
        // which is not what the single-user form asks for either.
        var f = await SetupAsync();

        var result = await f.Svc.ImportAsync(Input(f, dryRun: false, Row("No Dept", "nodept@techpio.com", dept: null)));

        result.Created.Should().Be(1);
        (await f.H.Db.UserDepartments.CountAsync()).Should().Be(0);
    }
}
