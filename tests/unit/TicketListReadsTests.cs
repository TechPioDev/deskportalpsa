using System.Data.Common;
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
    private readonly Statements _sent = new();
    private Guid _me, _asha, _alpha, _beta;

    /// <summary>Every statement sent to the database, so a test can say how an answer was found as well as what it was.</summary>
    private sealed class Statements : DbCommandInterceptor
    {
        public List<string> All { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            All.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            All.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    /// <summary>The real rule for what the caller may see, which will not say that it is every PSA ticket: each ticket is then read.</summary>
    private sealed class WillNotSay(ITicketScopeQuery real) : ITicketScopeQuery
    {
        public Task<IQueryable<Ticket>> VisibleAsync(IQueryable<Ticket> source, Guid appUserId, string permissionKey, CancellationToken ct = default)
            => real.VisibleAsync(source, appUserId, permissionKey, ct);
        public Task<Ticket?> FindAsync(IQueryable<Ticket> source, Guid ticketId, Guid appUserId, string permissionKey, CancellationToken ct = default)
            => real.FindAsync(source, ticketId, appUserId, permissionKey, ct);
    }

    public TicketListReadsTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection)
            .AddInterceptors(_sent)
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

    private TicketReadService Reads(bool byIndex = true, Guid? as_ = null)
    {
        var scope = new TicketScopeQuery(_db, new EffectivePermissionService(_db));
        return new TicketReadService(_db, byIndex ? scope : new WillNotSay(scope), new TestCurrentUser(Org, userId: as_ ?? _me));
    }

    private bool Walked => _sent.All.Any(sql => sql.Contains("WITH RECURSIVE"));

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

    [Theory]
    [InlineData(true)]   // found from the indexes, one person to the next
    [InlineData(false)]  // found by reading the tickets and their time entries
    public async Task The_filter_lists_offer_what_the_tickets_the_caller_can_see_hold_and_nothing_else(bool byIndex)
    {
        var f = await Reads(byIndex).FacetsAsync();
        Walked.Should().Be(byIndex, "the two ways of finding people are both held to the same answer");

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

    [Fact]
    public async Task People_found_from_the_indexes_are_never_another_organizations()
    {
        // The walk is written by hand, and a statement written by hand is not narrowed to the tenant
        // for it. Another organization in the same database, with a login and a person of its own.
        var theirs = Guid.NewGuid();
        var platform = new TenantContext();
        platform.SetPlatformScope();
        await using (var all = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, platform, TimeProvider.System))
        {
            var them = new AppUser { MspOrganizationId = theirs, DisplayName = "Their Technician", Email = "t@other.test", IsActive = true };
            var psa = new PsaConnection { MspOrganizationId = theirs, Name = "Their PSA", Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://o.example/", CredentialSecretRef = "mem://o", IsEnabled = true };
            Ticket Of(Action<Ticket> with)
            {
                var t = new Ticket
                {
                    MspOrganizationId = theirs, Origin = TicketOrigin.Psa, Provider = psa.Provider, PsaConnectionId = psa.Id, ExternalTicketId = Guid.NewGuid().ToString("N")[..8],
                    RequesterName = "x", RequesterEmail = "x@other.test", Title = "Theirs", PortalStatus = "Their Status", PortalPriority = "Their Priority", QueueOrBoard = "Their Queue",
                };
                with(t);
                return t;
            }
            var held = Of(t => (t.AssignedTechnicianExternalId, t.AssignedTechnicianName) = ("o-1", "Their Login"));
            var theirOwn = Of(t => t.AssignedAppUserId = them.Id);
            all.AddRange(new MspOrganization { Id = theirs, Name = "Other", Slug = "other" }, them, psa, held, theirOwn,
                new TicketTimeEntry { MspOrganizationId = theirs, TicketId = held.Id, TechnicianExternalId = "o-2", TechnicianName = "Their Logger", Hours = 1, EntryDate = _now, Source = TimeEntrySource.Provider, SyncStatus = TimeEntrySyncStatus.Synced },
                new TicketTimeEntry { MspOrganizationId = theirs, TicketId = theirOwn.Id, AppUserId = them.Id, Hours = 1, EntryDate = _now, Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced });
            await all.SaveChangesAsync();
        }

        var f = await Reads().FacetsAsync();

        Walked.Should().BeTrue();
        f.People.Select(p => p.Name).Should().Equal("Asha Rao", "Bilal Khan", "Dalbir", "Zed Zafar");
        f.Statuses.Should().NotContain("Their Status");
        f.Queues.Should().NotContain("Their Queue");
        f.Sources.Should().NotContain("Their PSA");
    }

    [Fact]
    public async Task People_found_from_the_indexes_keep_the_two_odd_cases_the_reading_keeps()
    {
        // A login whose only ticket is also held by someone here is that person's ticket, not the
        // login's: the login is not offered. And a ticket still naming a user who has since been
        // removed offers them, as someone unknown.
        var gone = Guid.NewGuid();
        _db.Tickets.AddRange(
            new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = ProviderType.ConnectWisePsa, PsaConnectionId = _alpha, ExternalTicketId = "9100",
                RequesterName = "r", RequesterEmail = "r@a.test", Title = "Held here and there", PortalStatus = "NEW", PortalPriority = "LOW",
                AssignedAppUserId = _asha, AssignedTechnicianExternalId = "m-5", AssignedTechnicianName = "Only Ever With Asha",
            },
            new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = ProviderType.ConnectWisePsa, PsaConnectionId = _alpha, ExternalTicketId = "9101",
                RequesterName = "r", RequesterEmail = "r@a.test", Title = "Held by someone removed", PortalStatus = "NEW", PortalPriority = "LOW", AssignedAppUserId = gone,
            });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var walked = (await Reads(byIndex: true).FacetsAsync()).People;
        var read = (await Reads(byIndex: false).FacetsAsync()).People;

        walked.Should().BeEquivalentTo(read, o => o.WithStrictOrdering());
        walked.Select(p => p.Name).Should().Equal("Asha Rao", "Bilal Khan", "Dalbir", "Unknown user", "Zed Zafar");
    }

    [Fact]
    public async Task Someone_who_sees_only_their_own_tickets_has_the_tickets_read_and_is_offered_only_what_is_on_them()
    {
        // Asha's sight is narrowed to what she holds. Whether a login is on a ticket she can see is
        // then a question about each ticket, so the indexes are not walked.
        var technician = new Role { MspOrganizationId = Org, Name = "Technician", BuiltInType = RoleType.Technician };
        technician.Permissions.Add(new RolePermission { PermissionKey = Permissions.TicketsViewAssigned, Scope = PermissionScope.Assigned });
        _db.Add(technician);
        _db.Add(new UserRole { AppUserId = _asha, RoleId = technician.Id });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var f = await Reads(as_: _asha).FacetsAsync();

        Walked.Should().BeFalse();
        f.People.Select(p => p.Name).Should().Contain("Asha Rao").And.NotContain(["Bilal Khan", "Zed Zafar"], "those are on Beta's ticket, which is not hers");
        f.Sources.Should().Equal("Alpha PSA");
    }

    [Fact]
    public async Task A_time_entry_is_given_the_account_of_its_ticket_whoever_writes_it_and_a_thousand_at_once_cost_one_look()
    {
        // "Who has logged time" is read off the entry, with its account. An entry written without
        // one would not be in the answer, so the save gives it: from the ticket in hand, from a
        // ticket saved beside it, or by asking - once for all of them, however many there are.
        var onAlpha = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _alpha).Select(t => t.Id).FirstAsync();
        var onBeta = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _beta).Select(t => t.Id).FirstAsync();
        var onNoPsa = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == null).Select(t => t.Id).FirstAsync();
        var fresh = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = ProviderType.AutotaskPsa, PsaConnectionId = _beta, ExternalTicketId = "9200",
            RequesterName = "r", RequesterEmail = "r@a.test", Title = "Raised in the same breath", PortalStatus = "NEW", PortalPriority = "LOW",
        };
        TicketTimeEntry On(Guid ticket) => new()
        {
            MspOrganizationId = Org, TicketId = ticket, Hours = 0.25m, EntryDate = _now, Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced,
        };
        var entries = Enumerable.Range(0, 1_000).Select(i => On(i % 2 == 0 ? onAlpha : onBeta)).ToList();
        var withItsTicket = On(fresh.Id);
        var onTheTeamsOwn = On(onNoPsa);
        _db.ChangeTracker.Clear();
        _db.Add(fresh);
        _db.AddRange(entries);
        _db.AddRange(withItsTicket, onTheTeamsOwn);
        _sent.All.Clear();

        await _db.SaveChangesAsync();

        entries.Where((_, i) => i % 2 == 0).Should().OnlyContain(e => e.PsaConnectionId == _alpha);
        entries.Where((_, i) => i % 2 == 1).Should().OnlyContain(e => e.PsaConnectionId == _beta);
        withItsTicket.PsaConnectionId.Should().Be(_beta, "its ticket was being saved beside it, and had not been asked for");
        onTheTeamsOwn.PsaConnectionId.Should().BeNull("a ticket with no PSA is an answer, not something left undone");
        _sent.All.Count(sql => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("\"tickets\"")).Should().Be(1, "the tickets not in hand are asked for together");
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
