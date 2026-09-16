using Microsoft.EntityFrameworkCore;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Connectors.Mock;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Who a public reply is emailed to. The portal sends no mail itself — the PSA does — so what is
/// under test is the instruction handed to the provider, and above all the boundary around it: a
/// reply on one customer's ticket must never be copied to another customer's contact.
/// </summary>
public class ReplyRecipientTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid RegularUser = Guid.NewGuid();

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static async Task<DeskDbContext> SeedAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "CW", Provider = ProviderType.ConnectWisePsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        db.ClientCompanies.Add(new ClientCompany
        { Id = CompanyA, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" });
        db.ClientUsers.Add(new ClientUser
        {
            Id = RegularUser, MspOrganizationId = Org, ClientCompanyId = CompanyA,
            Email = "user@acme.test", DisplayName = "User A", IdpSubject = "sub-user",
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static (TicketCommandService Svc, MockConnector Psa) Build(DeskDbContext db, TestClock clock)
    {
        var psa = new MockConnector(new MockConnectorOptions(), clock);
        return (new TicketCommandService(db, new FakeResolver(psa), new MappingEngine(),
            new SyncEventStore(db, clock), new NoopTicketScopeQuery(), clock, new RecordingActivity()), psa);
    }

    private static ClientAccess Access() => new(Org, CompanyA, RegularUser, false);

    [Fact]
    public async Task Recipients_chosen_by_the_technician_reach_the_provider()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var (svc, psa) = Build(db, clock);
        psa.AddContact("c1", "komal@acme.test", "Komal Mehta");
        var created = await svc.CreateAsync(Access(), new CreateTicketInput("Outlook down", null, null, null, null));

        await svc.AddStaffCommentAsync(Guid.NewGuid(), "Jane Tech", created.Id, "Fixed, please confirm.",
            isPublic: true, emailContact: true, emailCc: ["komal@acme.test"]);

        psa.LastNoteRequest!.EmailContact.Should().BeTrue();
        psa.LastNoteRequest.EmailCc.Should().Equal("komal@acme.test");
    }

    [Fact]
    public async Task An_address_outside_this_customer_is_refused_not_quietly_dropped()
    {
        // The isolation guarantee. Silently filtering would send the reply believing it had copied
        // someone it had not; refusing says so before anything leaves the building.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var (svc, psa) = Build(db, clock);
        psa.AddContact("c1", "komal@acme.test", "Komal Mehta");
        var created = await svc.CreateAsync(Access(), new CreateTicketInput("Outlook down", null, null, null, null));

        var act = async () => await svc.AddStaffCommentAsync(Guid.NewGuid(), "Jane Tech", created.Id, "Hello.",
            isPublic: true, emailContact: false, emailCc: ["rival@othercompany.test"]);

        (await act.Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("rival@othercompany.test");
        psa.LastNoteRequest.Should().BeNull("nothing may reach the provider once a recipient is rejected");
    }

    [Fact]
    public async Task An_internal_note_is_emailed_to_nobody_however_the_request_was_made()
    {
        // The composer hides recipients on an internal note, but a request can be made without the
        // composer. This is the gate that actually protects a private remark.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var (svc, psa) = Build(db, clock);
        psa.AddContact("c1", "komal@acme.test", "Komal Mehta");
        var created = await svc.CreateAsync(Access(), new CreateTicketInput("Outlook down", null, null, null, null));

        await svc.AddStaffCommentAsync(Guid.NewGuid(), "Jane Tech", created.Id, "Legacy tenant — do not tell them yet.",
            isPublic: false, emailContact: true, emailCc: ["komal@acme.test"]);

        psa.LastNoteRequest!.IsPublic.Should().BeFalse();
        psa.LastNoteRequest.EmailContact.Should().BeFalse("an internal note must never be mailed to a customer");
        psa.LastNoteRequest.EmailCc.Should().BeEmpty();
    }

    [Fact]
    public async Task The_picker_offers_only_this_customers_active_contacts()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var (svc, psa) = Build(db, clock);
        psa.AddContact("c1", "komal@acme.test", "Komal Mehta");
        psa.AddContact("c2", "retired@acme.test", "Retired Person", isActive: false);
        var created = await svc.CreateAsync(Access(), new CreateTicketInput("Outlook down", null, null, null, null));

        var list = await svc.ListReplyRecipientsAsync(Guid.NewGuid(), created.Id);

        list.CompanyName.Should().Be("Acme");
        list.CanChooseRecipients.Should().BeTrue();
        list.Contacts.Select(c => c.Email).Should().Contain("komal@acme.test");
        list.Contacts.Should().NotContain(c => c.Email == "retired@acme.test",
            "a deactivated contact is not someone to copy");
        list.Contacts.Should().OnlyContain(c => c.Email.EndsWith("@acme.test"),
            "the list is this ticket's customer, and nobody else");
    }

    [Fact]
    public async Task Opening_a_ticket_with_no_known_contact_asks_the_PSA_and_keeps_the_answer()
    {
        // The sync only re-reads recently active tickets, so an older ticket learns its contact
        // when a technician opens it - otherwise Public reply stays hidden on a ticket that has one.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var (svc, psa) = Build(db, clock);
        var created = await svc.CreateAsync(Access(), new CreateTicketInput("Outlook down", null, null, null, null));
        var row = await db.Tickets.SingleAsync(t => t.Id == created.Id);
        row.RequesterUserId = null; row.RequesterEmail = "unknown@unknown"; row.RequesterName = "Unknown";
        await db.SaveChangesAsync();

        psa.SetTicketContact(row.ExternalTicketId!, null, null);
        (await svc.RefreshContactAsync(Guid.NewGuid(), created.Id)).Should().BeFalse("the PSA names nobody yet");

        psa.SetTicketContact(row.ExternalTicketId!, "Priya Nair", "priya@acme.test");
        (await svc.RefreshContactAsync(Guid.NewGuid(), created.Id)).Should().BeTrue();

        var stored = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == created.Id);
        (stored.RequesterName, stored.RequesterEmail).Should().Be(("Priya Nair", "priya@acme.test"));
    }
}
