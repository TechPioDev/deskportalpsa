using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// What the dashboard summary, the ticket page's filter lists and the text search answer, held to
/// the figure.
///
/// These three were a pass over every ticket for each number they show, and took seconds at half a
/// million tickets. Nothing held what they return: the only tests of the summary asserted that it
/// was not null. They are pinned here first, on a real SQL translator and through the real
/// visibility rule, so that making them faster can be shown to have changed nothing - including
/// what is left out: a ticket on a board the caller is not a member of is in none of the counts,
/// none of the lists and no search result.
/// </summary>
public sealed class TicketListReadsTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private Guid _me, _asha, _alpha, _beta;

    public TicketListReadsTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection)
            .ConfigureWarnings(w => w.Throw(
                RelationalEventId.MultipleCollectionIncludeWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning,
                CoreEventId.FirstWithoutOrderByAndFilterWarning)).Options, _tenant, TimeProvider.System);
        _db.Database.EnsureCreated();
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TicketReadService Reads()
    {
        var permissions = new EffectivePermissionService(_db);
        return new TicketReadService(_db, new TicketScopeQuery(_db, permissions), new TestCurrentUser(Org, userId: _me));
    }

    private void Seed()
    {
        var role = new Role { MspOrganizationId = Org, Name = "Administrator", BuiltInType = RoleType.MspAdministrator };
        role.Permissions.Add(new RolePermission { PermissionKey = Permissions.TicketsViewAll, Scope = PermissionScope.All });
        var me = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "dalbir@techpio.test", IsActive = true };
        me.Roles.Add(new UserRole { RoleId = role.Id });
        var asha = new AppUser { MspOrganizationId = Org, DisplayName = "Asha Rao", Email = "asha@techpio.test", IsActive = true };
        var outsider = new AppUser { MspOrganizationId = Org, DisplayName = "Someone Else", Email = "else@techpio.test", IsActive = true };
        (_me, _asha) = (me.Id, asha.Id);

        // Two PSA accounts. The first writes as "api-a": that login is the integration, not a person.
        var alpha = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Alpha PSA", Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://a.example/",
            CredentialSecretRef = "mem://a", IsEnabled = true, DefaultTimeEntryResourceId = "api-a",
        };
        var beta = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Beta PSA", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://b.example/",
            CredentialSecretRef = "mem://b", IsEnabled = true,
        };
        (_alpha, _beta) = (alpha.Id, beta.Id);
        var acme = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = alpha.Id, Name = "Acme", ExternalCompanyId = "1" };
        var globex = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = beta.Id, Name = "Globex", ExternalCompanyId = "1" };
        var open = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal, NextNumber = 3 };
        var closed = new Board { MspOrganizationId = Org, Name = "Directors", Key = "DIR", Kind = BoardKind.Internal, NextNumber = 2 };

        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" },
            role, me, asha, outsider, alpha, beta, acme, globex, open, closed,
            // The Directors board names its members, and the caller is not one of them.
            new BoardMember { MspOrganizationId = Org, BoardId = closed.Id, AppUserId = outsider.Id },
            // On Alpha, the login m-1 is Asha. On Beta the same id is somebody else, not linked to anyone.
            new UserPsaIdentity { MspOrganizationId = Org, AppUserId = asha.Id, PsaConnectionId = alpha.Id, ExternalTechnicianId = "m-1", ExternalTechnicianName = "Asha R" });

        var n = 0;
        Ticket Psa(PsaConnection c, ClientCompany company, string title, string status, string priority, Action<Ticket>? with = null)
        {
            var t = new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = c.Provider, PsaConnectionId = c.Id, ClientCompanyId = company.Id,
                ExternalTicketId = (9000 + ++n).ToString(), RequesterName = "Rita Requester", RequesterEmail = "rita@acme.test", Title = title,
                PortalStatus = status, PortalPriority = priority, QueueOrBoard = "Service Desk", SyncStatus = TicketSyncStatus.Synced,
            };
            with?.Invoke(t);
            return t;
        }
        Ticket Own(Board? board, TicketOrigin origin, string title, string priority, string? number = null) => new()
        {
            MspOrganizationId = Org, Origin = origin, BoardId = board?.Id, Number = number, RequesterName = "Dalbir", RequesterEmail = "dalbir@techpio.test",
            Title = title, PortalStatus = "NEW", PortalPriority = priority, QueueOrBoard = board?.Name, SyncStatus = TicketSyncStatus.Synced,
        };

        var dueSoon = Psa(alpha, acme, "Printer offline", "NEW", "HIGH", t => (t.AssignedTechnicianExternalId, t.AssignedTechnicianName, t.SlaDueAt) = ("m-1", "Asha R", _now.AddHours(2)));
        var overdue = Psa(alpha, acme, "Mailbox full", "IN_PROGRESS", "Critical", t => (t.AssignedAppUserId, t.SlaDueAt) = (asha.Id, _now.AddHours(-3)));
        // Paused: waiting on the customer, so not late whatever its date says, and held by nobody.
        var paused = Psa(alpha, acme, "Awaiting quote approval", "Waiting Customer", "LOW", t => (t.SlaPausedAt, t.SlaDueAt) = (_now.AddDays(-1), _now.AddDays(-1)));
        // Held by the integration's own login, which is nobody.
        var onHold = Psa(alpha, acme, "Parts on order", "On Hold", "NORMAL", t => (t.AssignedTechnicianExternalId, t.AssignedTechnicianName, t.QueueOrBoard) = ("api-a", "API User", "Projects"));
        var resolved = Psa(alpha, acme, "Password reset", "RESOLVED", "HIGH", t => t.ResolvedAt = _now.AddDays(-2));
        var longClosed = Psa(alpha, acme, "Old laptop", "Closed", "LOW", t => t.ResolvedAt = _now.AddDays(-10));
        var onBeta = Psa(beta, globex, "VPN drops", "NEW", "urgent", t => (t.AssignedTechnicianExternalId, t.AssignedTechnicianName, t.QueueOrBoard) = ("m-1", "Bilal Khan", "Triage"));
        var mine = Psa(alpha, acme, "Quarterly review prep", "NEW", "LOW", t => t.AssignedAppUserId = me.Id);
        var internalWork = Own(open, TicketOrigin.Internal, "Replace the UPS battery", "NORMAL", "INT-000001");
        var alert = Own(null, TicketOrigin.Rmm, "Disk 91% on SRV-01", "HIGH");
        // On the board the caller is not a member of. It is in nothing below.
        var hidden = Own(closed, TicketOrigin.Internal, "Payroll printer offline", "HIGH", "DIR-000001");
        hidden.PortalStatus = "Board Review";
        _db.AddRange(dueSoon, overdue, paused, onHold, resolved, longClosed, onBeta, mine, internalWork, alert, hidden);

        TicketTimeEntry Time(Ticket t, decimal hours, Guid? user = null, string? ext = null, string? name = null) => new()
        {
            MspOrganizationId = Org, TicketId = t.Id, AppUserId = user, TechnicianExternalId = ext, TechnicianName = name, Hours = hours,
            EntryDate = _now, Source = user is null ? TimeEntrySource.Provider : TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced,
        };
        _db.AddRange(
            Time(mine, 1.5m, user: me.Id),
            Time(dueSoon, 0.5m, user: asha.Id),
            Time(onBeta, 2m, ext: "m-9", name: "Zed Zafar"),
            // The integration's own login, and a login seen only on the hidden ticket: neither is offered.
            Time(onHold, 1m, ext: "api-a", name: "API User"),
            Time(hidden, 3m, ext: "m-77", name: "Hidden Person"));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task The_summary_counts_the_open_work_the_caller_can_see_each_ticket_where_it_belongs()
    {
        var s = await Reads().SummaryAsync(mineOnly: false);

        // Open: eight. The two finished ones are not, and neither is the one on the board the caller is not on.
        s.Open.Should().Be(8);
        s.Overdue.Should().Be(1, "past its date and not paused: the mailbox");
        s.DueSoon.Should().Be(1, "due inside eight hours: the printer");
        // Due today is by the organization's own day. Two hours from now is today unless it is nearly midnight there.
        s.DueToday.Should().Be(_now.AddHours(2) < new DateTimeOffset(_now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero) ? 1 : 0);
        s.Waiting.Should().Be(2, "paused, or a status that says waiting or hold");
        s.HighPriority.Should().Be(4, "high, urgent or critical, however the PSA capitalises it");
        s.Unassigned.Should().Be(4, "nobody here and nobody there; the integration's own login is nobody");
        s.ResolvedLast7Days.Should().Be(1);
        s.OpenByPriority.Should().BeEquivalentTo(
            [new LabelCount("HIGH", 2), new LabelCount("CRITICAL", 1), new LabelCount("LOW", 2), new LabelCount("NORMAL", 2), new LabelCount("URGENT", 1)]);
        s.OpenBySource.Should().BeEquivalentTo(
            [new LabelCount("Alpha PSA", 5), new LabelCount("Beta PSA", 1), new LabelCount("Team boards", 1), new LabelCount("Monitoring", 1)]);
        s.OpenBySource[0].Should().Be(new LabelCount("Alpha PSA", 5), "the largest comes first");
        s.HoursLoggedThisWeek.Should().BeNull("that is a figure about one person");
    }

    [Fact]
    public async Task My_own_summary_is_of_my_tickets_and_my_hours()
    {
        var s = await Reads().SummaryAsync(mineOnly: true);

        (s.Open, s.Overdue, s.DueSoon, s.Waiting, s.HighPriority, s.Unassigned, s.ResolvedLast7Days).Should().Be((1, 0, 0, 0, 0, 0, 0));
        s.OpenByPriority.Should().BeEquivalentTo([new LabelCount("LOW", 1)]);
        s.OpenBySource.Should().BeEquivalentTo([new LabelCount("Alpha PSA", 1)]);
        s.HoursLoggedThisWeek.Should().Be(1.5m);
    }

    [Fact]
    public async Task The_filter_lists_offer_what_the_tickets_the_caller_can_see_hold_and_nothing_else()
    {
        var f = await Reads().FacetsAsync();

        f.Statuses.Should().Equal("Closed", "IN_PROGRESS", "NEW", "On Hold", "RESOLVED", "Waiting Customer");
        f.Statuses.Should().NotContain("Board Review", "a status seen only on a ticket the caller cannot open");
        f.Priorities.Should().Equal("Critical", "HIGH", "LOW", "NORMAL", "urgent");
        f.Companies.Should().Equal("Acme", "Globex");
        f.Queues.Should().Equal("Internal", "Projects", "Service Desk", "Triage");
        f.Queues.Should().NotContain("Directors");
        f.Sources.Should().Equal("Alpha PSA", "Beta PSA");

        // Everyone who holds or logged time on a ticket the caller can see, once each. The same
        // login id on the two PSA accounts is two people: Asha on Alpha (linked), someone else on Beta.
        f.People.Select(p => p.Name).Should().Equal("Asha Rao", "Bilal Khan", "Dalbir", "Zed Zafar");
        f.People.Select(p => p.Key).Should().Equal(
            PersonKey.For(_asha, null, null), PersonKey.For(null, _beta, "m-1"), PersonKey.For(_me, null, null), PersonKey.For(null, _beta, "m-9"));
        f.People.Select(p => p.Name).Should().NotContain(["API User", "Hidden Person"]);
    }

    [Theory]
    [InlineData("printer", new[] { "Printer offline" })]            // a subject; the payroll printer is on the hidden board
    [InlineData("PRINTER OFF", new[] { "Printer offline" })]        // whatever the case
    [InlineData("9007", new[] { "VPN drops" })]                     // the PSA's own number
    [InlineData("INT-0000", new[] { "Replace the UPS battery" })]   // a board's number
    [InlineData("globex", new[] { "VPN drops" })]                   // the client's name
    [InlineData("rita", new[] { "Awaiting quote approval", "Mailbox full", "Old laptop", "Parts on order", "Password reset", "Printer offline", "Quarterly review prep", "VPN drops" })] // who raised it
    [InlineData("no such thing", new string[0])]
    public async Task Search_finds_a_ticket_by_its_number_subject_requester_or_client_and_only_one_the_caller_can_see(string asked, string[] found)
    {
        var result = await Reads().SearchAsync(new TicketQuery(Q: asked));

        result.Items.Select(t => t.Title).Should().BeEquivalentTo(found);
        (result.Total, result.Truncated).Should().Be((found.Length, false));
        (await Reads().PageAsync(new TicketQuery(Q: asked))).Total.Should().Be(found.Length, "the paged list narrows by the same words");
    }
}
