using Desk.Domain.Tickets;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Old tickets hold placeholder contacts; the backfill asks the PSA and keeps the contact it names,
/// and leaves alone what it cannot improve.
/// </summary>
public class ContactBackfillTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private static Ticket Held(string externalId, string email = TicketContact.PlaceholderEmail, Guid? requesterUser = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = Conn, ClientCompanyId = Guid.NewGuid(), ExternalTicketId = externalId,
        Title = "Printer", RequesterName = email == TicketContact.PlaceholderEmail ? TicketContact.PlaceholderName : "Asha",
        RequesterEmail = email, RequesterUserId = requesterUser,
    };

    private static UnifiedTicket Live(string id, string? name, string? email) => new()
    {
        ExternalId = id, Title = "Printer", RequesterName = name, RequesterEmail = email,
    };

    [Fact]
    public async Task Only_tickets_nobody_can_be_reached_on_are_pending()
    {
        var h = AdminHarness.Create(Org);
        h.Db.Tickets.AddRange(Held("1"), Held("2", "asha@acme.test"), Held("3", requesterUser: Guid.NewGuid()),
            new Ticket { MspOrganizationId = Org, PsaConnectionId = Conn, ClientCompanyId = Guid.NewGuid(), Title = "Portal only", RequesterName = "Unknown", RequesterEmail = "unknown@unknown" });
        await h.Db.SaveChangesAsync();

        var pending = await ContactBackfill.PendingAsync(h.Db);

        pending.Should().ContainKey(Conn).WhoseValue.Select(p => p.ExternalId).Should().Equal("1");
    }

    [Fact]
    public async Task The_contact_the_PSA_names_is_stored_and_the_rest_are_counted()
    {
        var h = AdminHarness.Create(Org);
        h.Db.Tickets.AddRange(Held("10"), Held("11"), Held("12"), Held("13"));
        await h.Db.SaveChangesAsync();
        var psa = new StubConnector();
        psa.Tickets.Add(Live("10", "Asha Rao", " asha@acme.test "));
        psa.Tickets.Add(Live("11", null, null));
        psa.Tickets.Add(Live("12", "Front desk", null));
        psa.FailingTicketIds.Add("13");

        var pending = (await ContactBackfill.PendingAsync(h.Db))[Conn];
        var outcome = await ContactBackfill.ApplyAsync(h.Db, psa, pending);

        outcome.Should().Be(new ContactBackfill.Outcome(Found: 1, NoContact: 1, Failed: 1, NameOnly: 1));
        var rows = await h.Db.Tickets.AsNoTracking().ToDictionaryAsync(t => t.ExternalTicketId!);
        rows["10"].Should().BeEquivalentTo(new { RequesterName = "Asha Rao", RequesterEmail = "asha@acme.test" });
        rows["11"].RequesterEmail.Should().Be(TicketContact.PlaceholderEmail);
        rows["12"].Should().BeEquivalentTo(new { RequesterName = "Front desk", RequesterEmail = TicketContact.PlaceholderEmail });
        rows["13"].RequesterEmail.Should().Be(TicketContact.PlaceholderEmail);

        (await ContactBackfill.PendingAsync(h.Db))[Conn].Select(p => p.ExternalId).Should().Equal("11", "12", "13");
    }
}
