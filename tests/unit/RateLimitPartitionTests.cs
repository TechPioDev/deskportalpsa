using System.Net;
using System.Security.Claims;
using Desk.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Whose allowance a request spends. The answer used to be "the organization's", which made a desk
/// of forty people share one budget a few of them could exhaust.
/// </summary>
public class RateLimitPartitionTests
{
    private static HttpContext Request(string? userId = null, string? subject = null, string? org = null, string address = "10.0.0.1")
    {
        var claims = new List<Claim>();
        if (userId is not null) claims.Add(new Claim(CurrentUser.UserIdClaim, userId));
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (org is not null) claims.Add(new Claim(CurrentUser.OrgClaim, org));
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Count > 0 ? "test" : null)),
            Connection = { RemoteIpAddress = IPAddress.Parse(address) },
        };
    }

    [Fact]
    public void Two_colleagues_do_not_share_one_budget()
    {
        var org = Guid.NewGuid().ToString();
        var asha = RateLimitPartitions.UserKey(Request(userId: "user-a", org: org));
        var dalbir = RateLimitPartitions.UserKey(Request(userId: "user-b", org: org));

        asha.Should().NotBe(dalbir, "one technician's busy afternoon must not throttle the person beside them");
    }

    [Fact]
    public void The_same_person_keeps_one_budget_across_their_requests()
    {
        var first = RateLimitPartitions.UserKey(Request(userId: "user-a", address: "10.0.0.1"));
        var second = RateLimitPartitions.UserKey(Request(userId: "user-a", address: "10.0.0.9"));

        second.Should().Be(first, "it is the person that is limited, not the machine they happen to be on");
    }

    [Fact]
    public void A_token_issued_before_the_portal_user_id_existed_still_identifies_its_caller()
    {
        var key = RateLimitPartitions.UserKey(Request(subject: "keycloak-subject-123"));

        key.Should().Be("user:keycloak-subject-123");
    }

    [Fact]
    public void Anonymous_callers_are_told_apart_by_address()
    {
        var one = RateLimitPartitions.UserKey(Request(address: "203.0.113.5"));
        var two = RateLimitPartitions.UserKey(Request(address: "203.0.113.6"));

        one.Should().NotBe(two);
        one.Should().StartWith("addr:");
    }

    [Fact]
    public void The_organization_ceiling_applies_to_signed_in_callers_only()
    {
        var org = Guid.NewGuid().ToString();

        RateLimitPartitions.OrganizationKey(Request(userId: "user-a", org: org)).Should().Be("org:" + org);
        // Anonymous traffic has no organization, and putting it all in one bucket would let any
        // caller spend the allowance of every other anonymous caller.
        RateLimitPartitions.OrganizationKey(Request()).Should().BeNull();
    }

    [Fact]
    public void The_ceiling_is_far_above_one_person_s_allowance()
    {
        // Otherwise the ceiling, not the person's own limit, is what a desk meets first — which is
        // the fault this change exists to remove.
        RateLimitPartitions.PerOrganizationPermitLimit.Should()
            .BeGreaterThan(RateLimitPartitions.PerUserPermitLimit * 5);
    }
}
