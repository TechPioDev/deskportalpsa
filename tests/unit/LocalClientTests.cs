using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.ControlPanel;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Clients that are in no PSA, added by hand so that the team's own tickets can say who the work
/// was for.
///
/// What these hold: a client is added by name and can be chosen on a ticket straight away; one
/// name is one client, against the PSA's clients as well and whatever the case; a name that is no
/// name is refused; renaming, retiring and bringing back, with an audit entry each; a retired
/// client is not offered and cannot take a new ticket, and the tickets it has keep its name; a
/// PSA's client cannot be renamed here; a client added by hand has no client sign-ins, so none
/// can be granted access to it, and nothing is ever asked of a PSA about it; and another
/// organization's are not seen. On a SQL translator.
/// </summary>
public sealed class LocalClientTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly TestCurrentUser _lead;
    private readonly Guid _harpal, _board, _psaCompany, _clientUser;

    public LocalClientTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        var connection = new PsaConnection
        {
            MspOrganizationId = Org, Name = "ConnectWise", Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://cw.example/",
            CredentialSecretRef = "mem://cw", IsEnabled = true,
        };
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "Magnolia Dental Services", ExternalCompanyId = "250" };
        var other = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "ABC-IT", ExternalCompanyId = "251" };
        var harpal = new AppUser { MspOrganizationId = Org, DisplayName = "Harpal Singh", Email = "harpal@techpio.test", IsActive = true };
        var board = new Board { MspOrganizationId = Org, Name = "TechPio", Key = "TECPIO", Kind = BoardKind.Internal };
        var clientUser = new ClientUser { MspOrganizationId = Org, ClientCompanyId = other.Id, DisplayName = "Ben Okafor", Email = "ben@abc-it.test" };
        (_harpal, _board, _psaCompany, _clientUser) = (harpal.Id, board.Id, company.Id, clientUser.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "Asia/Kolkata" }, connection, company, other, harpal, board, clientUser);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _lead = new TestCurrentUser(Org, name: "Harpal Singh", userId: _harpal);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private AuditWriter Audit => new(_db, _lead, _tenant, _clock);
    private LocalClientService Clients => new(_db, _tenant, Audit);
    private InternalTicketService Tickets => new(_db, _tenant, _clock, new RecordingActivity());

    private List<string> Audited() => _db.AuditLog.AsNoTracking().Select(a => a.Action).ToList();

    private sealed class OneConnector(Desk.PsaCore.Contracts.IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<Desk.PsaCore.Contracts.IServiceManagementConnector> ResolveAsync(Guid psaConnectionId, CancellationToken ct = default) => Task.FromResult(c);
    }

    [Fact]
    public async Task A_client_is_added_by_name_and_can_be_chosen_straight_away_beside_the_PSAs_own()
    {
        var added = await Clients.AddAsync(new LocalClientInput("  NeedTech   Solutions "));

        added.Should().BeEquivalentTo(new { Name = "NeedTech Solutions", IsActive = true, Tickets = 0 });
        (await Clients.ChoicesAsync()).Should().BeEquivalentTo(
        [
            new { Name = "ABC-IT", Local = false },
            new { Name = "Magnolia Dental Services", Local = false },
            new { Name = "NeedTech Solutions", Local = true },
        ], o => o.WithStrictOrdering());

        var row = await _db.ClientCompanies.AsNoTracking().SingleAsync(c => c.Id == added.Id);
        row.PsaConnectionId.Should().BeNull();
        row.IsLocal.Should().BeTrue();
        row.ExternalCompanyId.Should().StartWith("local:").And.HaveLength(38);
        Audited().Should().BeEquivalentTo(["client.local.added"]);
    }

    [Theory]
    [InlineData("magnolia dental services", "comes from your PSA (ConnectWise)")]
    [InlineData("ABC-it", "comes from your PSA (ConnectWise)")]
    [InlineData("needtech", "is already in the list.")]
    public async Task One_name_is_one_client_whatever_the_case_and_wherever_it_came_from(string name, string says)
    {
        await Clients.AddAsync(new LocalClientInput("NeedTech"));
        var act = () => Clients.AddAsync(new LocalClientInput(name));
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain(says);
        (await _db.ClientCompanies.CountAsync()).Should().Be(3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("X")]
    public async Task A_name_that_is_no_name_is_refused(string name)
    {
        var act = () => Clients.AddAsync(new LocalClientInput(name));
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("at least two characters");
        var tooLong = () => Clients.AddAsync(new LocalClientInput(new string('n', LocalClientService.MaxNameLength + 1)));
        await tooLong.Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task A_ticket_on_a_board_can_be_raised_for_a_client_added_by_hand_and_the_list_counts_it()
    {
        var needTech = await Clients.AddAsync(new LocalClientInput("NeedTech"));
        var created = await Tickets.CreateAsync(_harpal, new InternalTicketInput(_board, "NinjaRMM implementation", null, ClientCompanyId: needTech.Id));

        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == created.TicketId);
        (ticket.ClientCompanyId, ticket.Origin, ticket.PsaConnectionId).Should().Be((needTech.Id, TicketOrigin.Internal, (Guid?)null));
        (await Clients.ListAsync()).Single().Tickets.Should().Be(1);
    }

    [Fact]
    public async Task Renaming_retiring_and_bringing_back_are_each_recorded_and_a_retired_client_takes_no_new_ticket()
    {
        var client = await Clients.AddAsync(new LocalClientInput("NeedTech"));
        var first = await Tickets.CreateAsync(_harpal, new InternalTicketInput(_board, "First job", null, ClientCompanyId: client.Id));

        (await Clients.UpdateAsync(client.Id, new LocalClientInput("NeedTech Pvt Ltd"))).Name.Should().Be("NeedTech Pvt Ltd");
        var retired = await Clients.UpdateAsync(client.Id, new LocalClientInput("NeedTech Pvt Ltd", IsActive: false));
        retired.Should().BeEquivalentTo(new { IsActive = false, Tickets = 1 });

        (await Clients.ChoicesAsync()).Select(c => c.Name).Should().NotContain("NeedTech Pvt Ltd");
        (await Clients.ListAsync()).Single().Should().BeEquivalentTo(new { Name = "NeedTech Pvt Ltd", IsActive = false });
        var raise = () => Tickets.CreateAsync(_harpal, new InternalTicketInput(_board, "Second job", null, ClientCompanyId: client.Id));
        (await raise.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("has been retired");
        // What was raised for it keeps its name.
        (await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == first.TicketId)).ClientCompanyId.Should().Be(client.Id);

        // A second one under the same name is not the way back.
        var again = () => Clients.AddAsync(new LocalClientInput("needtech pvt ltd"));
        (await again.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("has been retired. Bring it back");

        (await Clients.UpdateAsync(client.Id, new LocalClientInput("NeedTech Pvt Ltd", IsActive: true))).IsActive.Should().BeTrue();
        (await Clients.ChoicesAsync()).Select(c => c.Name).Should().Contain("NeedTech Pvt Ltd");
        // Written within the same instant of the test clock, so compared as a set.
        Audited().Should().BeEquivalentTo(["client.local.added", "client.local.renamed", "client.local.retired", "client.local.restored"]);
    }

    [Fact]
    public async Task Renaming_onto_another_clients_name_is_refused_and_saving_the_same_name_is_not_a_clash()
    {
        var client = await Clients.AddAsync(new LocalClientInput("NeedTech"));
        var clash = () => Clients.UpdateAsync(client.Id, new LocalClientInput("abc-it"));
        (await clash.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("comes from your PSA");
        (await Clients.UpdateAsync(client.Id, new LocalClientInput("NeedTech"))).Name.Should().Be("NeedTech");
        Audited().Should().BeEquivalentTo(["client.local.added"]);
    }

    [Fact]
    public async Task A_PSAs_client_cannot_be_renamed_or_retired_here()
    {
        var act = () => Clients.UpdateAsync(_psaCompany, new LocalClientInput("Renamed", IsActive: false));
        await act.Should().ThrowAsync<NotFoundException>();
        var row = await _db.ClientCompanies.AsNoTracking().SingleAsync(c => c.Id == _psaCompany);
        (row.Name, row.IsActive).Should().Be(("Magnolia Dental Services", true));
        (await Clients.ListAsync()).Should().BeEmpty("the list is of the ones added by hand");
    }

    [Fact]
    public async Task No_client_sign_in_can_be_granted_access_to_a_client_added_by_hand_and_it_is_not_offered()
    {
        var client = await Clients.AddAsync(new LocalClientInput("NeedTech"));
        var access = new ClientCompanyAccessService(_db, Audit);

        (await access.CompaniesAsync()).Select(c => c.Name).Should().BeEquivalentTo(["ABC-IT", "Magnolia Dental Services"]);
        var grant = () => access.GrantAsync(_clientUser, client.Id, new Desk.Application.Admin.SetCompanyGrantInput(SeesAllTickets: true));
        (await grant.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("is in no PSA");
        (await _db.ClientCompanyAccess.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Nothing_is_asked_of_a_PSA_about_a_client_added_by_hand()
    {
        var client = await Clients.AddAsync(new LocalClientInput("NeedTech"));
        var psa = new StubConnector(ProviderType.ConnectWisePsa);
        var devices = new DeviceSyncService(_db, new OneConnector(psa), _clock, NullLogger<DeviceSyncService>.Instance);

        var act = () => devices.SyncCompanyAsync(client.Id);
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("not in a PSA");
    }

    [Fact]
    public async Task Another_organizations_clients_are_not_seen_and_their_names_are_free_to_use()
    {
        var other = Guid.NewGuid();
        var elsewhere = new TenantContext();
        elsewhere.SetTenant(other);
        using var theirs = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, elsewhere, _clock);
        theirs.Add(new MspOrganization { Id = other, Name = "Another MSP", Slug = "another", TimeZone = "UTC" });
        await theirs.SaveChangesAsync();
        var theirClients = new LocalClientService(theirs, elsewhere, new AuditWriter(theirs, new TestCurrentUser(other, name: "Someone Else"), elsewhere, _clock));
        var theirOwn = await theirClients.AddAsync(new LocalClientInput("NeedTech"));

        // The same name here is this organization's own, and theirs is not in this list.
        var mine = await Clients.AddAsync(new LocalClientInput("NeedTech"));
        mine.Id.Should().NotBe(theirOwn.Id);
        (await Clients.ListAsync()).Select(c => c.Id).Should().Equal(mine.Id);
        (await theirClients.ChoicesAsync()).Select(c => c.Id).Should().Equal(theirOwn.Id);
        var reach = () => Clients.UpdateAsync(theirOwn.Id, new LocalClientInput("Taken over"));
        await reach.Should().ThrowAsync<NotFoundException>();
    }
}
