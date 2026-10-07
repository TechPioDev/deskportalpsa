using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.ControlPanel;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A client user sees into a second company only where somebody at the desk has given it to them.
///
/// What these hold, each as an attempt and what comes of it: a company that was not given is
/// refused, in the same words whether it does not exist, belongs to another organization or is
/// simply not theirs; a ticket is never served under a company it does not belong to, whichever
/// way round the two are asked for; a grant gives the least it can until more is said; a company
/// given to be looked at cannot be changed; a grant makes nobody an administrator of anything; a
/// grant taken away is gone for the very next request; and every grant, change and removal is in
/// the audit log. On a SQL translator, through the real rule for what a client may see.
///
/// Three companies of one organization (Acme is Priya's own; Bolt and Cord are not), and Xeno of
/// another organization.
/// </summary>
public sealed class ClientCompanyAccessTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Elsewhere = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new();
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly DeskDbContext _platform;
    private readonly StubConnector _psa = new(ProviderType.ConnectWisePsa);
    private readonly Acting _acting = new();
    private Guid _connectionId, _acme, _bolt, _cord, _xeno, _priya, _theirUser;
    private readonly Dictionary<string, Guid> _tickets = [];

    /// <summary>What a request says: the company it names, and whether it would change anything.</summary>
    private sealed class Acting : IActingCompany
    {
        public Guid? RequestedCompanyId { get; set; }
        public bool Malformed { get; set; }
        public bool IsWrite { get; set; }
    }

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    public ClientCompanyAccessTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        var platform = new TenantContext();
        platform.SetPlatformScope();
        _platform = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, platform, _clock);
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        PsaConnection Psa(Guid org, string name) => new()
        {
            MspOrganizationId = org, Name = name, Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://cw.example/",
            CredentialSecretRef = "mem://" + name, IsEnabled = true,
        };
        var (ours, theirs) = (Psa(Org, "Ours"), Psa(Elsewhere, "Theirs"));
        _connectionId = ours.Id;
        ClientCompany Company(Guid org, PsaConnection psa, string name, string external) => new() { MspOrganizationId = org, PsaConnectionId = psa.Id, Name = name, ExternalCompanyId = external };
        var (acme, bolt, cord, xeno) = (Company(Org, ours, "Acme", "100"), Company(Org, ours, "Bolt", "200"), Company(Org, ours, "Cord", "300"), Company(Elsewhere, theirs, "Xeno", "900"));
        (_acme, _bolt, _cord, _xeno) = (acme.Id, bolt.Id, cord.Id, xeno.Id);
        ClientUser User(Guid org, ClientCompany company, string name, string subject, bool admin = false, string? contact = null) => new()
        {
            MspOrganizationId = org, ClientCompanyId = company.Id, DisplayName = name, Email = $"{subject}@{company.Name.ToLower()}.test",
            IdpSubject = subject, IsCompanyAdministrator = admin, IsActive = true, ExternalContactId = contact,
        };
        // Priya administers Acme, her own company; her contact in the PSA is a contact OF Acme.
        var priya = User(Org, acme, "Priya Nair", "priya", admin: true, contact: "acme-contact-7");
        var boltsOwn = User(Org, bolt, "Bo Larsen", "bo", admin: true);
        var theirUser = User(Elsewhere, xeno, "Xavier Moss", "xavier", admin: true);
        (_priya, _theirUser) = (priya.Id, theirUser.Id);
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal, NextNumber = 2 };

        Ticket T(string key, Guid org, ClientCompany company, PsaConnection psa, Guid? requester, bool onBoard = false)
        {
            var t = new Ticket
            {
                MspOrganizationId = org, ClientCompanyId = company.Id, CorrelationId = Guid.NewGuid(), Title = key,
                RequesterName = "R", RequesterEmail = "r@test", RequesterUserId = requester, PortalStatus = "NEW", PortalPriority = "NORMAL",
                Origin = onBoard ? TicketOrigin.Internal : TicketOrigin.Psa, BoardId = onBoard ? board.Id : null, Number = onBoard ? "INT-000001" : null,
                PsaConnectionId = onBoard ? null : psa.Id, Provider = onBoard ? null : ProviderType.ConnectWisePsa, ExternalTicketId = onBoard ? null : "x-" + key,
                SyncStatus = TicketSyncStatus.Synced,
            };
            _tickets[key] = t.Id;
            return t;
        }

        _platform.AddRange(
            new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" },
            new MspOrganization { Id = Elsewhere, Name = "Another MSP", Slug = "another", TimeZone = "UTC" },
            ours, theirs, acme, bolt, cord, xeno, priya, boltsOwn, theirUser, board,
            T("acme-hers", Org, acme, ours, priya.Id), T("acme-other", Org, acme, ours, null),
            // In Bolt: one ticket Priya herself raised, two she did not, and the desk's own note against Bolt on its internal board.
            T("bolt-hers", Org, bolt, ours, priya.Id), T("bolt-a", Org, bolt, ours, boltsOwn.Id), T("bolt-b", Org, bolt, ours, null),
            T("bolt-internal", Org, bolt, ours, null, onBoard: true),
            T("cord-a", Org, cord, ours, null),
            T("xeno-a", Elsewhere, xeno, theirs, theirUser.Id));
        _platform.SaveChanges();
        _platform.ChangeTracker.Clear();
    }

    private ClientCompanyAccessService Grants()
        => new(_db, new AuditWriter(_db, new TestCurrentUser(Org, name: "Dana Desk"), _tenant, _clock));

    /// <summary>A request from Priya naming a company (or none), as the API would resolve it.</summary>
    private Task<ClientAccess?> AsPriyaAsync(Guid? company = null, bool write = false, string subject = "priya")
    {
        (_acting.RequestedCompanyId, _acting.Malformed, _acting.IsWrite) = (company, false, write);
        _db.ChangeTracker.Clear();
        return new ClientAccessResolver(_db, _acting).ResolveAsync(subject);
    }

    private TicketReadService Reads() => new(_db, new NoopTicketScopeQuery(), new TestCurrentUser(Org));

    private async Task<List<string>> SeenAsync(ClientAccess access)
        => (await Reads().ListAsync(access)).Select(t => t.Title).Order(StringComparer.Ordinal).ToList();

    private async Task<List<string>> AuditedAsync()
        => await _db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("client.company_access.")).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => a.Action + " " + a.DetailJson).ToListAsync();

    // ------------------------------------------------------------------ nothing without a grant

    [Fact]
    public async Task With_no_company_named_a_client_is_in_their_own_company_exactly_as_before()
    {
        var access = await AsPriyaAsync();

        access.Should().BeEquivalentTo(new { ClientCompanyId = _acme, ClientUserId = _priya, IsCompanyAdministrator = true, IsGranted = false, SeesAllTickets = true, CanWrite = true });
        (await SeenAsync(access!)).Should().Equal("acme-hers", "acme-other");
        // Naming her own company is the same thing.
        (await AsPriyaAsync(_acme)).Should().BeEquivalentTo(access);
    }

    [Fact]
    public async Task A_company_that_was_not_given_is_refused_in_the_same_words_whatever_the_reason()
    {
        // Bolt is in her organization and she was not given it. Xeno is another organization's. The third does not exist.
        foreach (var company in new[] { _bolt, _xeno, Guid.NewGuid() })
        foreach (var write in new[] { false, true })
            (await FluentActions.Awaiting(() => AsPriyaAsync(company, write)).Should().ThrowAsync<ForbiddenException>())
                .Which.Message.Should().Be(ClientAccessResolver.NotYours, "the answer does not say whether the company exists");

        // A company named and unreadable is refused too. It is never read as "none named", which would answer with her own company.
        (_acting.RequestedCompanyId, _acting.Malformed) = (null, true);
        (await FluentActions.Awaiting(() => new ClientAccessResolver(_db, _acting).ResolveAsync("priya")).Should().ThrowAsync<ForbiddenException>())
            .Which.Message.Should().Be(ClientAccessResolver.NotYours);

        // Having Bolt gives her nothing in Cord.
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true, CanCreate: true));
        await FluentActions.Awaiting(() => AsPriyaAsync(_cord)).Should().ThrowAsync<ForbiddenException>();
        // And somebody who is not a client user at all is nobody here, whatever company the request names.
        (await AsPriyaAsync(_bolt, subject: "a-technician")).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_but_a_grant_gives_a_company_not_an_address_and_not_a_name()
    {
        // Priya's address is changed to one at Bolt's own domain, and a second Acme is created with Bolt's name.
        var priya = await _platform.ClientUsers.SingleAsync(u => u.Id == _priya);
        priya.Email = "priya@bolt.test";
        await _platform.SaveChangesAsync();
        _platform.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => AsPriyaAsync(_bolt)).Should().ThrowAsync<ForbiddenException>("an address at the company's domain is not a grant");
        (await Grants().MineAsync("priya")).Select(c => c.Name).Should().Equal("Acme");
        (await SeenAsync((await AsPriyaAsync())!)).Should().Equal("acme-hers", "acme-other");
    }

    // ------------------------------------------------------------------ what a grant gives

    [Fact]
    public async Task A_grant_gives_the_least_it_can_until_more_is_said()
    {
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput());

        var access = await AsPriyaAsync(_bolt);
        access.Should().BeEquivalentTo(new { ClientCompanyId = _bolt, ClientUserId = _priya, IsGranted = true, IsCompanyAdministrator = false, SeesAllTickets = false, CanWrite = false },
            "she administers Acme; in Bolt she is nobody's administrator, sees what she raised, and changes nothing");
        (await SeenAsync(access!)).Should().Equal("bolt-hers");
        (await Reads().GetDetailAsync(access!, _tickets["bolt-a"])).Should().BeNull("a ticket of Bolt's she did not raise is not hers to open by its id either");

        // Every ticket of the company is said separately.
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true));
        access = await AsPriyaAsync(_bolt);
        (await SeenAsync(access!)).Should().Equal("bolt-a", "bolt-b", "bolt-hers");
        (await SeenAsync(access!)).Should().NotContain("bolt-internal", "the desk's own record against a client is the desk's, in this company as in her own");
        (await Reads().GetDetailAsync(access!, _tickets["bolt-a"]))!.Title.Should().Be("bolt-a");
        access!.CanWrite.Should().BeFalse("seeing more is not being allowed to change anything");
    }

    [Fact]
    public async Task A_ticket_is_never_served_under_a_company_it_does_not_belong_to()
    {
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true, CanCreate: true));
        var (home, inBolt) = ((await AsPriyaAsync())!, (await AsPriyaAsync(_bolt))!);
        var reads = Reads();

        // Each company's list is that company's and nothing else's: nothing is gathered across the two.
        (await SeenAsync(home)).Should().Equal("acme-hers", "acme-other");
        (await SeenAsync(inBolt)).Should().Equal("bolt-a", "bolt-b", "bolt-hers");
        // By id, either way round, and for companies she has nothing in.
        foreach (var (access, key) in new[] { (home, "bolt-a"), (home, "bolt-hers"), (inBolt, "acme-hers"), (inBolt, "acme-other"), (home, "cord-a"), (inBolt, "cord-a"), (home, "xeno-a"), (inBolt, "xeno-a"), (inBolt, "bolt-internal") })
            (await reads.GetDetailAsync(access, _tickets[key])).Should().BeNull($"{key} is not a ticket of the company the request is about");
        (await reads.SearchAsync(new TicketQuery(Q: "acme"), inBolt)).Items.Should().BeEmpty("a search in Bolt finds nothing of Acme's");
        (await reads.PageAsync(new TicketQuery(Take: 50), inBolt)).Total.Should().Be(3);
        (await reads.FacetsAsync(inBolt)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_company_given_to_be_looked_at_refuses_every_change_before_anything_is_done()
    {
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true));

        (await AsPriyaAsync(_bolt)).Should().NotBeNull("she may read");
        (await FluentActions.Awaiting(() => AsPriyaAsync(_bolt, write: true)).Should().ThrowAsync<ForbiddenException>())
            .Which.Message.Should().Be(ClientAccessResolver.ViewOnly);
        // In her own company she writes as she always did, grant or no grant elsewhere.
        (await AsPriyaAsync(write: true))!.CanWrite.Should().BeTrue();
        // The devices of a company are listed for choosing one when raising a ticket. She raises none there.
        _platform.Devices.Add(new Device { MspOrganizationId = Org, ClientCompanyId = _bolt, PsaConnectionId = _connectionId, Name = "BOLT-LAPTOP-01", Type = "Laptop", ExternalId = "d-1", IsActive = true });
        await _platform.SaveChangesAsync();
        var devices = new TicketDeviceService(_db, new NoopTicketScopeQuery(), new Resolver(_psa));
        (await devices.ClientChoicesAsync((await AsPriyaAsync(_bolt))!)).Should().BeEmpty();

        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true, CanCreate: true));
        (await AsPriyaAsync(_bolt, write: true))!.CanWrite.Should().BeTrue();
        (await devices.ClientChoicesAsync((await AsPriyaAsync(_bolt))!)).Select(d => d.Name).Should().Equal("BOLT-LAPTOP-01");
        (await devices.ClientChoicesAsync((await AsPriyaAsync())!)).Should().BeEmpty("Bolt's devices are not offered in Acme");
    }

    [Fact]
    public async Task A_ticket_raised_in_a_company_she_was_given_is_that_companys_and_carries_no_contact_of_her_own()
    {
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(CanCreate: true));
        var commands = new TicketCommandService(_db, new Resolver(_psa), new MappingEngine(), new SyncEventStore(_db, _clock), new NoopTicketScopeQuery(), _clock, new RecordingActivity());

        var raised = await commands.CreateAsync((await AsPriyaAsync(_bolt, write: true))!, new CreateTicketInput("Bolt's printer is down", "It smokes", "HIGH", null, null));

        var sent = _psa.CreateRequests.Should().ContainSingle().Subject;
        (sent.ExternalCompanyId, sent.RequesterExternalId, sent.RequesterEmail).Should().Be(("200", null, "priya@acme.test"),
            "the PSA is told Bolt's company, and not a contact id that is a contact of Acme");
        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == raised.Id);
        (ticket.ClientCompanyId, ticket.RequesterUserId).Should().Be((_bolt, _priya));
        // She sees it in Bolt, as the one who raised it, and not in Acme.
        (await SeenAsync((await AsPriyaAsync(_bolt))!)).Should().Equal("Bolt's printer is down", "bolt-hers");
        (await SeenAsync((await AsPriyaAsync())!)).Should().NotContain("Bolt's printer is down");

        // In her own company the contact is hers, as before.
        _psa.NextCreateResult = new(true, "9002", null);
        await commands.CreateAsync((await AsPriyaAsync(write: true))!, new CreateTicketInput("Acme's own", null, "LOW", null, null));
        (_psa.CreateRequests[1].ExternalCompanyId, _psa.CreateRequests[1].RequesterExternalId).Should().Be(("100", "acme-contact-7"));
    }

    [Fact]
    public async Task A_grant_makes_nobody_an_administrator_and_opens_no_control_panel()
    {
        await Grants().GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true, CanCreate: true));
        // Besides administering Acme she holds a section of the control panel outright. Sections are not kept company by company.
        _platform.ClientAccessGrants.Add(new ClientAccessGrant { MspOrganizationId = Org, ClientUserId = _priya, Section = ControlPanelSection.Announcements });
        await _platform.SaveChangesAsync();
        var audit = new AuditWriter(_db, new TestCurrentUser(Org), _tenant, _clock);
        var (panel, content) = (new ControlPanelService(_db, audit), new ClientContentService(_db, audit, _clock, null!));
        var inBolt = (await AsPriyaAsync(_bolt, write: true))!;

        (await panel.GetCapabilitiesAsync(inBolt)).Should().BeEquivalentTo(new { IsCompanyAdministrator = false, ClientCompanyId = _bolt, Sections = Array.Empty<string>() });
        await FluentActions.Awaiting(() => panel.ListUsersAsync(inBolt)).Should().ThrowAsync<ForbiddenException>("Bolt's users are Bolt's administrators' to see");
        await FluentActions.Awaiting(() => panel.InviteUserAsync(inBolt, new InviteClientUserInput("mole@bolt.test", "A Mole", true))).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => content.SaveAnnouncementAsync(inBolt, new AnnouncementInput(null, "Hello Bolt", "From Acme", false, true)))
            .Should().ThrowAsync<ForbiddenException>().WithMessage("*for your own company*");
        await FluentActions.Awaiting(() => content.ListAnnouncementsAsync(inBolt)).Should().ThrowAsync<ForbiddenException>();
        (await _platform.ClientUsers.AsNoTracking().CountAsync(u => u.ClientCompanyId == _bolt)).Should().Be(1, "nobody was added to Bolt");
        (await _platform.Announcements.AsNoTracking().CountAsync()).Should().Be(0);

        // At home nothing has changed: she is Acme's administrator and its panel is hers.
        var home = (await AsPriyaAsync(write: true))!;
        (await panel.GetCapabilitiesAsync(home)).IsCompanyAdministrator.Should().BeTrue();
        (await panel.ListUsersAsync(home)).Should().ContainSingle(u => u.Email == "priya@acme.test");
        (await content.SaveAnnouncementAsync(home, new AnnouncementInput(null, "Hello Acme", null, false, true))).Title.Should().Be("Hello Acme");
    }

    // ------------------------------------------------------------------ taken away, and stale rows

    [Fact]
    public async Task A_grant_taken_away_or_narrowed_is_so_for_the_very_next_request()
    {
        var grants = Grants();
        await grants.GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true, CanCreate: true));
        (await SeenAsync((await AsPriyaAsync(_bolt))!)).Should().HaveCount(3);

        await grants.GrantAsync(_priya, _bolt, new SetCompanyGrantInput());
        (await SeenAsync((await AsPriyaAsync(_bolt))!)).Should().Equal("bolt-hers");
        await FluentActions.Awaiting(() => AsPriyaAsync(_bolt, write: true)).Should().ThrowAsync<ForbiddenException>();

        await grants.RevokeAsync(_priya, _bolt);
        (await FluentActions.Awaiting(() => AsPriyaAsync(_bolt)).Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Be(ClientAccessResolver.NotYours);
        (await grants.MineAsync("priya")).Select(c => c.Name).Should().Equal("Acme");
        // Her own company is untouched by any of it, and she can be told what she has left whatever company the request named.
        (await SeenAsync((await AsPriyaAsync())!)).Should().Equal("acme-hers", "acme-other");

        // Someone switched off is nobody, with or without grants.
        await grants.GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true));
        (await _platform.ClientUsers.SingleAsync(u => u.Id == _priya)).IsActive = false;
        await _platform.SaveChangesAsync();
        (await AsPriyaAsync(_bolt)).Should().BeNull();
        (await grants.MineAsync("priya")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_that_points_at_another_organizations_company_gives_nothing()
    {
        // Not something the product can write (see below). Put there by hand, as a restore or a script might.
        _platform.ClientCompanyAccess.Add(new ClientCompanyAccess { MspOrganizationId = Org, ClientUserId = _priya, ClientCompanyId = _xeno, SeesAllTickets = true, CanCreate = true });
        await _platform.SaveChangesAsync();
        _platform.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => AsPriyaAsync(_xeno)).Should().ThrowAsync<ForbiddenException>();
        (await Grants().MineAsync("priya")).Select(c => c.Name).Should().Equal("Acme");
    }

    // ------------------------------------------------------------------ the desk's side

    [Fact]
    public async Task Only_a_company_of_the_users_own_organization_can_be_given_and_never_their_own()
    {
        var grants = Grants();

        await FluentActions.Awaiting(() => grants.GrantAsync(_priya, _xeno, new SetCompanyGrantInput(true, true))).Should().ThrowAsync<NotFoundException>("another organization's company is not found");
        await FluentActions.Awaiting(() => grants.GrantAsync(_theirUser, _bolt, new SetCompanyGrantInput(true, true))).Should().ThrowAsync<NotFoundException>("nor is another organization's user");
        await FluentActions.Awaiting(() => grants.GrantAsync(_priya, Guid.NewGuid(), new SetCompanyGrantInput())).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => grants.RevokeAsync(_theirUser, _xeno)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => grants.GetUserAsync(_theirUser)).Should().ThrowAsync<NotFoundException>();
        (await FluentActions.Awaiting(() => grants.GrantAsync(_priya, _acme, new SetCompanyGrantInput(true, true))).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("own company").And.Contain("needs no grant");

        (await _platform.ClientCompanyAccess.AsNoTracking().CountAsync()).Should().Be(0);
        (await AuditedAsync()).Should().BeEmpty();
        (await grants.CompaniesAsync()).Select(c => c.Name).Should().Equal("Acme", "Bolt", "Cord");
        (await grants.ListUsersAsync(null)).Select(u => u.DisplayName).Should().Equal("Bo Larsen", "Priya Nair");
        (await grants.ListUsersAsync("xavier")).Should().BeEmpty("another organization's people are not listed");
    }

    [Fact]
    public async Task Every_grant_change_and_removal_is_audited_with_who_what_and_for_whom()
    {
        var grants = new ClientCompanyAccessService(_db, new Stepping(new AuditWriter(_db, new TestCurrentUser(Org, name: "Dana Desk"), _tenant, _clock), _clock));

        var given = await grants.GrantAsync(_priya, _bolt, new SetCompanyGrantInput());
        given.Grants.Should().ContainSingle().Which.Should().BeEquivalentTo(new { CompanyId = _bolt, CompanyName = "Bolt", SeesAllTickets = false, CanCreate = false });
        (given.HomeCompanyName, given.Email).Should().Be(("Acme", "priya@acme.test"));
        await grants.GrantAsync(_priya, _bolt, new SetCompanyGrantInput());          // said again as it is: not a change
        await grants.GrantAsync(_priya, _bolt, new SetCompanyGrantInput(SeesAllTickets: true, CanCreate: true));
        await grants.GrantAsync(_priya, _cord, new SetCompanyGrantInput(SeesAllTickets: true));
        (await grants.MineAsync("priya")).Should().BeEquivalentTo(new[]
        {
            new MyCompanyDto(_acme, "Acme", true, true, true), new MyCompanyDto(_bolt, "Bolt", false, true, true), new MyCompanyDto(_cord, "Cord", false, true, false),
        }, o => o.WithStrictOrdering(), "her own first, then what she was given, by name");
        (await grants.RevokeAsync(_priya, _bolt)).Grants.Select(g => g.CompanyName).Should().Equal("Cord");
        await grants.RevokeAsync(_priya, _bolt);                                       // already gone: nothing to record

        var audited = await AuditedAsync();
        audited.Select(a => a.Split(' ')[0]).Should().Equal(
            "client.company_access.granted", "client.company_access.changed", "client.company_access.granted", "client.company_access.revoked");
        audited[0].Should().Contain("priya@acme.test").And.Contain("\"company\":\"Bolt\"").And.Contain("\"SeesAllTickets\":false").And.Contain("\"CanCreate\":false");
        audited[1].Should().Contain("\"before\":{\"SeesAllTickets\":false,\"CanCreate\":false}").And.Contain("\"after\":{\"SeesAllTickets\":true,\"CanCreate\":true}");
        audited[3].Should().Contain("\"company\":\"Bolt\"");
        (await _db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("client.company_access.")).Select(a => a.ActorDisplayName).Distinct().ToListAsync())
            .Should().Equal("Dana Desk");
    }

    // ------------------------------------------------------------------ what a request is read as

    [Theory]
    [InlineData(null, "GET", false, false, false)]
    [InlineData("", "GET", false, false, false)]
    [InlineData("  ", "POST", false, false, true)]
    [InlineData("7b1f6c1e-52a5-4c1b-9d0a-3f1f0c9a2e11", "GET", true, false, false)]
    [InlineData("7B1F6C1E-52A5-4C1B-9D0A-3F1F0C9A2E11", "HEAD", true, false, false)]
    [InlineData("7b1f6c1e-52a5-4c1b-9d0a-3f1f0c9a2e11", "PUT", true, false, true)]
    [InlineData("7b1f6c1e-52a5-4c1b-9d0a-3f1f0c9a2e11", "DELETE", true, false, true)]
    [InlineData("bolt", "GET", false, true, false)]
    [InlineData("' OR 1=1 --", "GET", false, true, false)]
    [InlineData("00000000-0000-0000-0000-000000000000", "GET", false, true, false)]
    [InlineData("7b1f6c1e-52a5-4c1b-9d0a-3f1f0c9a2e11,7b1f6c1e-52a5-4c1b-9d0a-3f1f0c9a2e11", "GET", false, true, false)]
    public void A_company_is_read_from_the_request_only_when_it_is_exactly_one_id(string? header, string method, bool named, bool malformed, bool write)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = method;
        if (header is not null) http.Request.Headers[Desk.Api.Auth.HttpActingCompany.Header] = header;
        var acting = new Desk.Api.Auth.HttpActingCompany(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http });

        (acting.RequestedCompanyId is not null, acting.Malformed, acting.IsWrite).Should().Be((named, malformed, write),
            "anything that is not one id is refused as unreadable, and is never taken to mean the person's own company");
    }

    [Fact]
    public void The_same_company_sent_as_two_headers_is_not_one_id()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Headers[Desk.Api.Auth.HttpActingCompany.Header] = new Microsoft.Extensions.Primitives.StringValues([_bolt.ToString(), _acme.ToString()]);
        var acting = new Desk.Api.Auth.HttpActingCompany(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http });

        (acting.RequestedCompanyId, acting.Malformed).Should().Be(((Guid?)null, true));
    }

    private sealed class Stepping(IAuditWriter inner, TestClock clock) : IAuditWriter
    {
        public async Task WriteAsync(string action, string entityType, string? entityId, object? detail = null, CancellationToken ct = default)
        {
            await inner.WriteAsync(action, entityType, entityId, detail, ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }
}
