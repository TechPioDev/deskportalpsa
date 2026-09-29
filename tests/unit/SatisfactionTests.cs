using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Customer satisfaction: a client rates a finished ticket of their own, once, changeably for a while;
/// the rating keeps who worked it; the figures come back per technician and per client.
/// </summary>
public class SatisfactionTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid ClientUser = Guid.NewGuid();

    private static ClientAccess Client(Guid? company = null) => new(Org, company ?? Company, ClientUser, IsCompanyAdministrator: true);

    private static async Task<(AdminHarness H, SatisfactionService Svc, Guid Tech)> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        var tech = new AppUser { MspOrganizationId = Org, DisplayName = "Basit", Email = "b@techpio.test", IsActive = true };
        h.Db.AppUsers.Add(tech);
        h.Db.ClientCompanies.AddRange(
            new Desk.Domain.Tenancy.ClientCompany { Id = Company, MspOrganizationId = Org, Name = "Acme", ExternalCompanyId = "1", PsaConnectionId = Guid.NewGuid() },
            new Desk.Domain.Tenancy.ClientCompany { Id = Other, MspOrganizationId = Org, Name = "Globex", ExternalCompanyId = "2", PsaConnectionId = Guid.NewGuid() });
        await h.Db.SaveChangesAsync();
        return (h, new SatisfactionService(h.Db, h.Clock), tech.Id);
    }

    private static Ticket Psa(string title, string status, Guid? company = null, Guid? holder = null, DateTimeOffset? resolved = null) => new()
    {
        MspOrganizationId = Org, Origin = TicketOrigin.Psa, PsaConnectionId = Guid.NewGuid(), Provider = ProviderType.AutotaskPsa,
        ExternalTicketId = "T" + Random.Shared.Next(1000, 9999), ClientCompanyId = company ?? Company,
        RequesterName = "Priya", RequesterEmail = "p@acme.test", Title = title, PortalStatus = status,
        AssignedAppUserId = holder, ResolvedAt = resolved,
    };

    [Fact]
    public async Task A_client_rates_a_finished_ticket_and_can_change_their_mind()
    {
        var (h, svc, tech) = await BuildAsync();
        var t = Psa("Printer", "RESOLVED", holder: tech, resolved: h.Clock.GetUtcNow());
        h.Db.Tickets.Add(t);
        await h.Db.SaveChangesAsync();

        (await svc.StateAsync(Client(), t.Id)).CanRate.Should().BeTrue();
        await svc.RateAsync(Client(), t.Id, 2, "Took two days");
        var after = await svc.RateAsync(Client(), t.Id, 5, "Actually it was fixed properly");

        after.Should().BeEquivalentTo(new { Rating = 5, Comment = "Actually it was fixed properly" });
        // Replaced, not added: one answer per ticket.
        (await h.Db.TicketSatisfactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_open_ticket_says_why_it_cannot_be_rated_yet_and_refuses_a_rating()
    {
        var (h, svc, _) = await BuildAsync();
        var t = Psa("Still going", "IN_PROGRESS");
        h.Db.Tickets.Add(t);
        await h.Db.SaveChangesAsync();

        (await svc.StateAsync(Client(), t.Id)).Should().BeEquivalentTo(new { CanRate = false, Reason = "You can rate this ticket once it is resolved." });
        await Assert.ThrowsAsync<ValidationFailedException>(() => svc.RateAsync(Client(), t.Id, 4, null));
    }

    [Fact]
    public async Task Ratings_close_thirty_days_after_the_ticket_was_resolved()
    {
        var (h, svc, _) = await BuildAsync();
        var t = Psa("Old one", "CLOSED", resolved: h.Clock.GetUtcNow());
        h.Db.Tickets.Add(t);
        await h.Db.SaveChangesAsync();

        h.Clock.Advance(TimeSpan.FromDays(31));
        (await svc.StateAsync(Client(), t.Id)).CanRate.Should().BeFalse();
        await Assert.ThrowsAsync<ValidationFailedException>(() => svc.RateAsync(Client(), t.Id, 5, null));
    }

    [Fact]
    public async Task A_client_cannot_rate_another_companys_ticket_or_an_internal_one()
    {
        var (h, svc, _) = await BuildAsync();
        var theirs = Psa("Globex's", "RESOLVED", company: Other, resolved: h.Clock.GetUtcNow());
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT" };
        var internalOne = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "INT-000001",
            ClientCompanyId = Company, RequesterName = "d", RequesterEmail = "d@t", Title = "About Acme", PortalStatus = "CLOSED",
            ResolvedAt = h.Clock.GetUtcNow(),
        };
        h.Db.Boards.Add(board);
        h.Db.Tickets.AddRange(theirs, internalOne);
        await h.Db.SaveChangesAsync();

        // Not found either way: a rating endpoint must not confirm the ticket exists.
        await Assert.ThrowsAsync<NotFoundException>(() => svc.RateAsync(Client(), theirs.Id, 5, null));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.RateAsync(Client(), internalOne.Id, 5, null));
    }

    [Fact]
    public async Task The_rating_keeps_who_worked_the_ticket_even_after_it_is_reassigned()
    {
        var (h, svc, tech) = await BuildAsync();
        var t = Psa("Laptop", "RESOLVED", holder: tech, resolved: h.Clock.GetUtcNow());
        h.Db.Tickets.Add(t);
        await h.Db.SaveChangesAsync();
        await svc.RateAsync(Client(), t.Id, 5, "Great");

        t.AssignedAppUserId = null;
        await h.Db.SaveChangesAsync();

        var summary = await svc.SummaryAsync(h.Clock.GetUtcNow().AddDays(-1), h.Clock.GetUtcNow().AddDays(1));
        summary.ByTechnician.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "Basit", Ratings = 1, CsatPct = 100.0 });
    }

    [Fact]
    public async Task The_summary_counts_4_and_5_as_satisfied_and_lists_poor_ratings_even_without_a_comment()
    {
        var (h, svc, tech) = await BuildAsync();
        var now = h.Clock.GetUtcNow();
        var a = Psa("A", "RESOLVED", holder: tech, resolved: now);
        var b = Psa("B", "RESOLVED", holder: tech, resolved: now);
        var c = Psa("C", "RESOLVED", resolved: now);
        var d = Psa("D", "RESOLVED", company: Other, resolved: now);
        h.Db.Tickets.AddRange(a, b, c, d);
        await h.Db.SaveChangesAsync();
        await svc.RateAsync(Client(), a.Id, 5, null);
        await svc.RateAsync(Client(), b.Id, 4, "Fine");
        await svc.RateAsync(Client(), c.Id, 1, null);
        await svc.RateAsync(Client(Other), d.Id, 3, null);

        var s = await svc.SummaryAsync(now.AddDays(-1), now.AddDays(1));

        s.Should().BeEquivalentTo(new { Ratings = 4, Satisfied = 2, CsatPct = 50.0, Average = 3.25 });
        s.Distribution.Should().Equal(1, 0, 1, 1, 1);
        s.ByClient.Should().Contain(g => g.Name == "Acme" && g.Ratings == 3 && g.CsatPct == 66.7);
        s.ByTechnician.Should().Contain(g => g.Name == "Nobody held it" && g.Ratings == 2);
        // The comment, and the poor rating that came with none; the unremarkable 5 and 3 are not listed.
        s.Recent.Select(r => r.Title).Should().BeEquivalentTo("B", "C");
    }

    [Fact]
    public async Task A_period_with_no_ratings_has_no_percentage_rather_than_zero()
    {
        var (h, svc, _) = await BuildAsync();
        var s = await svc.SummaryAsync(h.Clock.GetUtcNow().AddDays(-30), h.Clock.GetUtcNow());
        s.CsatPct.Should().BeNull();
        s.Average.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task A_rating_outside_one_to_five_is_refused(int rating)
    {
        var (h, svc, _) = await BuildAsync();
        var t = Psa("X", "RESOLVED", resolved: h.Clock.GetUtcNow());
        h.Db.Tickets.Add(t);
        await h.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ValidationFailedException>(() => svc.RateAsync(Client(), t.Id, rating, null));
    }

    [Fact]
    public async Task Staff_see_the_rating_on_the_ticket_and_the_client_detail_does_not_carry_it()
    {
        var (h, svc, tech) = await BuildAsync();
        var t = Psa("Mail", "RESOLVED", holder: tech, resolved: h.Clock.GetUtcNow());
        h.Db.Tickets.Add(t);
        h.Db.ClientUsers.Add(new Desk.Domain.Tenancy.ClientUser
        {
            Id = ClientUser, MspOrganizationId = Org, ClientCompanyId = Company, DisplayName = "Priya", Email = "p@acme.test",
        });
        await h.Db.SaveChangesAsync();
        await svc.RateAsync(Client(), t.Id, 4, "Good");

        var staff = new TicketReadService(h.Db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: tech));
        (await staff.GetDetailForStaffAsync(t.Id))!.Rating.Should().BeEquivalentTo(new { Rating = 4, RatedBy = "Priya", TechnicianName = "Basit" });
        (await staff.GetDetailAsync(Client(), t.Id))!.Rating.Should().BeNull();
    }
}
