using System.Security.Claims;
using Desk.Api.Auth;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The one-time bind of a first sign-in to the account an administrator created for that e-mail.
///
/// An e-mail is unique within an organization or a client company, not across them. Once the same
/// contact can exist under two PSA connections - or two organizations invite the same person - two
/// accounts can be waiting on one address. The lookup expected at most one and threw, on every
/// request, so that person could not sign in at all.
/// </summary>
public class FirstSignInBindTests
{
    private const string Email = "pat@client.test";

    private static ClaimsPrincipal SignedIn(string subject, string email = Email)
        => new(new ClaimsIdentity([new Claim("sub", subject), new Claim("email", email)], "test"));

    /// <summary>As the API has it at claims time: no tenant established yet.</summary>
    private static DeskDbContext NewDb(string name) => TestDbContextFactory.Unscoped(name);

    private static ClientUser Invited(Guid org, Guid company, string email = Email) => new()
    {
        MspOrganizationId = org, ClientCompanyId = company, Email = email, DisplayName = "Pat",
    };

    private static async Task SeedAsync(string name, params object[] rows)
    {
        await using var seed = TestDbContextFactory.ForPlatform(name);
        seed.AddRange(rows);
        await seed.SaveChangesAsync();
    }

    private static bool HasOrganization(ClaimsPrincipal p) => p.HasClaim(c => c.Type == CurrentUser.OrgClaim);

    [Fact]
    public async Task A_single_client_invitation_is_bound_on_first_sign_in()
    {
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        await SeedAsync(name, Invited(org, Guid.NewGuid()));
        await using var db = NewDb(name);

        var principal = await new DeskClaimsTransformation(db, new TestClock()).TransformAsync(SignedIn("sub-1"));

        principal.FindFirstValue(CurrentUser.OrgClaim).Should().Be(org.ToString());
        (await db.ClientUsers.IgnoreQueryFilters().SingleAsync()).IdpSubject.Should().Be("sub-1");
    }

    [Fact]
    public async Task The_same_contact_invited_under_two_companies_binds_to_neither()
    {
        // Two PSA connections, the same customer contact in each: two client companies, two rows.
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        await SeedAsync(name, Invited(org, Guid.NewGuid()), Invited(org, Guid.NewGuid()));
        await using var db = NewDb(name);

        var principal = await new DeskClaimsTransformation(db, new TestClock()).TransformAsync(SignedIn("sub-1"));

        HasOrganization(principal).Should().BeFalse("which company they meant is not ours to guess");
        (await db.ClientUsers.IgnoreQueryFilters().Select(u => u.IdpSubject).ToListAsync())
            .Should().OnlyContain(s => s == null);
    }

    [Fact]
    public async Task The_same_address_invited_by_two_organizations_binds_to_neither()
    {
        var name = Guid.NewGuid().ToString();
        await SeedAsync(name, Invited(Guid.NewGuid(), Guid.NewGuid()), Invited(Guid.NewGuid(), Guid.NewGuid()));
        await using var db = NewDb(name);

        var principal = await new DeskClaimsTransformation(db, new TestClock()).TransformAsync(SignedIn("sub-1"));

        HasOrganization(principal).Should().BeFalse();
    }

    [Fact]
    public async Task Once_one_invitation_is_left_the_sign_in_binds_to_it()
    {
        var name = Guid.NewGuid().ToString();
        var keep = Guid.NewGuid();
        var withdrawn = Invited(Guid.NewGuid(), Guid.NewGuid());
        withdrawn.IsActive = false;
        await SeedAsync(name, Invited(keep, Guid.NewGuid()), withdrawn);
        await using var db = NewDb(name);

        var principal = await new DeskClaimsTransformation(db, new TestClock()).TransformAsync(SignedIn("sub-1"));

        principal.FindFirstValue(CurrentUser.OrgClaim).Should().Be(keep.ToString());
    }

    [Fact]
    public async Task A_staff_address_waiting_in_two_organizations_binds_to_neither()
    {
        var name = Guid.NewGuid().ToString();
        await SeedAsync(name,
            new AppUser { MspOrganizationId = Guid.NewGuid(), Email = Email, DisplayName = "Pat" },
            new AppUser { MspOrganizationId = Guid.NewGuid(), Email = Email, DisplayName = "Pat" });
        await using var db = NewDb(name);

        var principal = await new DeskClaimsTransformation(db, new TestClock()).TransformAsync(SignedIn("sub-1"));

        HasOrganization(principal).Should().BeFalse();
        (await db.AppUsers.Select(u => u.IdpSubject).ToListAsync()).Should().OnlyContain(s => s == null);
    }

    [Fact]
    public async Task A_single_staff_invitation_is_still_bound_on_first_sign_in()
    {
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var clock = new TestClock();
        // Seen a moment ago, so the "last active" write is skipped: it is a bulk update, which the
        // in-memory provider these tests run on cannot execute, and it is not what is under test.
        await SeedAsync(name, new AppUser { MspOrganizationId = org, Email = Email, DisplayName = "Pat", LastActiveAt = clock.GetUtcNow() });
        await using var db = NewDb(name);

        var principal = await new DeskClaimsTransformation(db, clock).TransformAsync(SignedIn("sub-1"));

        principal.FindFirstValue(CurrentUser.OrgClaim).Should().Be(org.ToString());
        (await db.AppUsers.SingleAsync()).IdpSubject.Should().Be("sub-1");
    }
}
