using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Old tickets hold placeholder contacts; the backfill asks the PSA and keeps the contact it names,
/// and leaves alone what it cannot improve. It runs by itself at every start, so it also leaves a
/// PSA alone that has refused the login, is paused or is put away, and stops at the first refusal.
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

    /// <summary>A harness with the connection the tickets belong to, as the ticket sync would call it.</summary>
    private static AdminHarness Harness(Action<PsaConnection>? set = null, Guid? id = null)
    {
        var h = AdminHarness.Create(Org);
        var connection = new PsaConnection
        {
            Id = id ?? Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://at.test", CredentialSecretRef = "ref", Status = ConnectionStatus.Healthy, IsEnabled = true,
        };
        set?.Invoke(connection);
        h.Db.PsaConnections.Add(connection);
        h.Db.SaveChanges();
        return h;
    }

    private static UnifiedTicket Live(string id, string? name, string? email) => new()
    {
        ExternalId = id, Title = "Printer", RequesterName = name, RequesterEmail = email,
    };

    [Fact]
    public async Task Only_tickets_nobody_can_be_reached_on_are_pending()
    {
        var h = Harness();
        h.Db.Tickets.AddRange(Held("1"), Held("2", "asha@acme.test"), Held("3", requesterUser: Guid.NewGuid()),
            new Ticket { MspOrganizationId = Org, PsaConnectionId = Conn, ClientCompanyId = Guid.NewGuid(), Title = "Portal only", RequesterName = "Unknown", RequesterEmail = "unknown@unknown" });
        await h.Db.SaveChangesAsync();

        var pending = await ContactBackfill.PendingAsync(h.Db);

        pending.Should().ContainKey(Conn).WhoseValue.Select(p => p.ExternalId).Should().Equal("1");
    }

    [Fact]
    public async Task The_contact_the_PSA_names_is_stored_and_the_rest_are_counted()
    {
        var h = Harness();
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

    [Theory]
    [InlineData("login refused")]
    [InlineData("paused")]
    [InlineData("put away")]
    [InlineData("switched off")]
    public async Task A_connection_that_is_not_to_be_called_has_nothing_pending(string why)
    {
        var h = Harness(c =>
        {
            switch (why)
            {
                case "login refused": c.LastErrorKind = ConnectionStates.Authentication; break;
                case "paused": c.SyncPausedAt = DateTimeOffset.UtcNow; break;
                case "put away": c.ArchivedAt = DateTimeOffset.UtcNow; break;
                case "switched off": c.IsEnabled = false; break;
            }
        });
        h.Db.Tickets.AddRange(Held("1"), Held("2"), Held("3"));
        await h.Db.SaveChangesAsync();

        (await ContactBackfill.PendingAsync(h.Db)).Should().BeEmpty("it runs by itself, and nobody asked it to call this PSA");
    }

    [Fact]
    public async Task One_connection_that_is_not_to_be_called_does_not_hold_back_another()
    {
        var other = Guid.NewGuid();
        var h = Harness(c => c.LastErrorKind = ConnectionStates.Authentication);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = other, MspOrganizationId = Org, Name = "ConnectWise", Provider = ProviderType.ConnectWisePsa,
            ApiEndpoint = "https://cw.test", CredentialSecretRef = "ref2", Status = ConnectionStatus.Healthy, IsEnabled = true,
        });
        var theirs = Held("77");
        theirs.PsaConnectionId = other;
        h.Db.Tickets.AddRange(Held("1"), theirs);
        await h.Db.SaveChangesAsync();

        var pending = await ContactBackfill.PendingAsync(h.Db);

        pending.Keys.Should().Equal(other);
        pending[other].Select(p => p.ExternalId).Should().Equal("77");
    }

    [Fact]
    public async Task A_login_refused_part_way_stops_the_asking_at_once()
    {
        var h = Harness();
        h.Db.Tickets.AddRange(Enumerable.Range(1, 25).Select(i => Held(i.ToString("00"))));
        await h.Db.SaveChangesAsync();
        var psa = new StubConnector { TicketReadFailure = new ConnectorException(ConnectorFailureKind.Authentication, "Autotask rejected the credentials.") };

        var pending = (await ContactBackfill.PendingAsync(h.Db))[Conn];
        var act = () => ContactBackfill.ApplyAsync(h.Db, psa, pending);

        (await act.Should().ThrowAsync<ConnectorException>()).Which.Kind.Should().Be(ConnectorFailureKind.Authentication);
        psa.TicketReads.Should().Be(1, "one refusal is enough to know; twenty-four more would count against the account");
    }
}
