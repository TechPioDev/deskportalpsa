using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A ticket's due date is moved later by somebody who may change the ticket, with a reason.
///
/// What these hold: a PSA ticket's new date goes to the PSA first and is kept here only once the
/// PSA accepted it; a board ticket's is the portal's own; the date it was first due is kept from
/// the first extension on and never changed after; the reason and the author are kept with the
/// ticket and audited; a date that is not later, a missing reason and a finished ticket are
/// refused before anything is sent; and a client is sent none of it. On a SQL translator.
/// </summary>
public sealed class TicketDueDateTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly StubConnector _psa;
    private readonly TestCurrentUser _asha;
    private readonly Guid _ashaId = Guid.NewGuid();
    private Guid _psaTicketId, _boardTicketId, _companyId, _priyaId;

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    public TicketDueDateTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        _psa = new StubConnector(ProviderType.AutotaskPsa) { AcceptsWrites = true };
        var connection = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Main", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.example/",
            CredentialSecretRef = "mem://main", IsEnabled = true,
        };
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "Acme", ExternalCompanyId = "1" };
        var asha = new AppUser { Id = _ashaId, MspOrganizationId = Org, DisplayName = "Asha Rao", Email = "asha@techpio.test", IsActive = true };
        var priya = new ClientUser { MspOrganizationId = Org, ClientCompanyId = company.Id, DisplayName = "Priya Nair", Email = "priya@acme.test", IdpSubject = "priya", IsCompanyAdministrator = true };
        var psaTicket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = connection.Id, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
            ExternalTicketId = "500", ClientCompanyId = company.Id, CorrelationId = Guid.NewGuid(), Title = "Printer offline",
            RequesterName = "Priya Nair", RequesterEmail = "priya@acme.test", RequesterUserId = priya.Id,
            PortalStatus = "IN_PROGRESS", PsaStatus = "In Progress", PortalPriority = "NORMAL", SyncStatus = TicketSyncStatus.Synced,
            SlaDueAt = _clock.GetUtcNow().AddDays(1),
        };
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT" };
        var boardTicket = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, CorrelationId = Guid.NewGuid(), Title = "Replace the switch",
            RequesterName = "Asha Rao", RequesterEmail = "asha@techpio.test", PortalStatus = "NEW", PortalPriority = "NORMAL",
            SyncStatus = TicketSyncStatus.Synced, SlaDueAt = _clock.GetUtcNow().AddHours(8),
        };
        (_psaTicketId, _boardTicketId, _companyId, _priyaId) = (psaTicket.Id, boardTicket.Id, company.Id, priya.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" }, connection, company, asha, priya, board, psaTicket, boardTicket);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _asha = new TestCurrentUser(Org, subject: "kc-asha", name: "Asha Rao", userId: _ashaId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TicketDueDateService Service => new(_db, new Resolver(_psa), _asha, _clock, new AuditWriter(_db, _asha, _tenant, _clock));
    private TicketReadService Reads => new(_db, new NoopTicketScopeQuery(), _asha);
    private ClientAccess AsPriya => new(Org, _companyId, _priyaId, true);

    private async Task<Ticket> TrackedAsync(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.Tickets.SingleAsync(t => t.Id == id);
    }

    [Fact]
    public async Task A_PSA_tickets_new_due_date_goes_to_the_PSA_first_and_is_kept_with_its_reason_once_accepted()
    {
        var ticket = await TrackedAsync(_psaTicketId);
        var was = ticket.SlaDueAt;
        var later = _clock.GetUtcNow().AddDays(3);

        var result = await Service.ExtendAsync(ticket, new ExtendDueDateInput(later, "Parts are on back-order until Friday."));

        var (sentTo, sent) = _psa.Updates.Should().ContainSingle().Subject;
        sentTo.Should().Be("500");
        (sent.DueDate, sent.Status, sent.Priority, sent.AssignedTechnicianExternalId).Should().Be(((DateTimeOffset?)later, null, null, null), "only the due date travels");
        result.Should().BeEquivalentTo(new { DueAt = (DateTimeOffset?)later, OriginalDueAt = was, Extensions = 1, LastExtendedAt = (DateTimeOffset?)_clock.GetUtcNow(), LastExtendedByName = "Asha Rao", LastReason = "Parts are on back-order until Friday." });
        var saved = await TrackedAsync(_psaTicketId);
        (saved.SlaDueAt, saved.OriginalSlaDueAt, saved.DueDateExtensions, saved.DueDateExtendedByUserId).Should().Be((later, was, 1, (Guid?)_ashaId));

        var audit = await _db.AuditLog.AsNoTracking().SingleAsync(a => a.Action == "ticket.due_date.extended");
        audit.EntityId.Should().Be(_psaTicketId.ToString());
        audit.DetailJson.Should().Contain("Parts are on back-order").And.Contain("\"sentToPsa\":true");

        // Staff read the move; the client reads nothing of it.
        var staff = (await Reads.GetDetailForStaffAsync(_psaTicketId))!;
        staff.DueDate.Should().BeEquivalentTo(new { Extensions = 1, LastReason = "Parts are on back-order until Friday.", OriginalDueAt = was });
        var client = (await Reads.GetDetailAsync(AsPriya, _psaTicketId))!;
        (client.DueDate, client.SlaDueAt).Should().Be((null, null));
        System.Text.Json.JsonSerializer.Serialize(client).Should().NotContain("back-order");
    }

    [Fact]
    public async Task A_PSA_that_refuses_the_date_changes_nothing_here()
    {
        _psa.WriteRefusal = "Due date cannot be after the contract end.";
        var ticket = await TrackedAsync(_psaTicketId);
        var was = ticket.SlaDueAt;

        var act = () => Service.ExtendAsync(ticket, new ExtendDueDateInput(_clock.GetUtcNow().AddDays(3), "Trying anyway."));

        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Due date cannot be after the contract end.");
        var saved = await TrackedAsync(_psaTicketId);
        (saved.SlaDueAt, saved.DueDateExtensions, saved.OriginalSlaDueAt).Should().Be((was, 0, null));
        (await _db.AuditLog.AsNoTracking().CountAsync(a => a.Action == "ticket.due_date.extended")).Should().Be(0);
    }

    [Fact]
    public async Task A_PSA_that_cannot_be_reached_is_an_error_and_nothing_is_saved()
    {
        _psa.WriteFailure = new ConnectorException(ConnectorFailureKind.Timeout, "Autotask request timed out.");
        var ticket = await TrackedAsync(_psaTicketId);
        await FluentActions.Awaiting(() => Service.ExtendAsync(ticket, new ExtendDueDateInput(_clock.GetUtcNow().AddDays(3), "Trying anyway."))).Should().ThrowAsync<ConnectorException>();
        (await TrackedAsync(_psaTicketId)).DueDateExtensions.Should().Be(0);
    }

    [Fact]
    public async Task A_board_tickets_due_date_is_the_portals_own_and_the_first_date_is_kept_across_two_moves()
    {
        var ticket = await TrackedAsync(_boardTicketId);
        var first = ticket.SlaDueAt;

        await Service.ExtendAsync(ticket, new ExtendDueDateInput(_clock.GetUtcNow().AddDays(1), "Waiting on the supplier."));
        _psa.Updates.Should().BeEmpty("a board ticket has no PSA");
        _clock.Advance(TimeSpan.FromHours(5));
        var again = await TrackedAsync(_boardTicketId);
        var result = await Service.ExtendAsync(again, new ExtendDueDateInput(_clock.GetUtcNow().AddDays(4), "Supplier slipped a week."));

        result.Should().BeEquivalentTo(new { OriginalDueAt = first, Extensions = 2, LastReason = "Supplier slipped a week.", LastExtendedAt = (DateTimeOffset?)_clock.GetUtcNow() });
        (await _db.AuditLog.AsNoTracking().CountAsync(a => a.Action == "ticket.due_date.extended")).Should().Be(2);
        var audit = await _db.AuditLog.AsNoTracking().Where(a => a.Action == "ticket.due_date.extended").OrderByDescending(a => a.CreatedAt).FirstAsync();
        audit.DetailJson.Should().Contain("\"sentToPsa\":false").And.Contain("\"extensions\":2");
    }

    [Fact]
    public async Task A_date_that_is_not_later_a_missing_reason_and_a_finished_ticket_are_refused_before_anything_is_sent()
    {
        var ticket = await TrackedAsync(_psaTicketId);
        var now = _clock.GetUtcNow();
        (await FluentActions.Awaiting(() => Service.ExtendAsync(ticket, new ExtendDueDateInput(now.AddHours(12), "Sooner, please."))).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("later than the current one");
        (await FluentActions.Awaiting(() => Service.ExtendAsync(ticket, new ExtendDueDateInput(now.AddDays(-1), "x"))).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("in the future").And.Contain("Say why");
        (await FluentActions.Awaiting(() => Service.ExtendAsync(ticket, new ExtendDueDateInput(now.AddDays(3), new string('r', 501)))).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("500 characters");

        ticket.PortalStatus = "RESOLVED";
        (await FluentActions.Awaiting(() => Service.ExtendAsync(ticket, new ExtendDueDateInput(now.AddDays(3), "Too late now."))).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("finished ticket");
        _psa.Updates.Should().BeEmpty();

        // A ticket with no due date at all can be given one.
        var fresh = await TrackedAsync(_boardTicketId);
        fresh.SlaDueAt = null;
        await _db.SaveChangesAsync();
        (await Service.ExtendAsync(await TrackedAsync(_boardTicketId), new ExtendDueDateInput(now.AddDays(2), "It needs a date."))).OriginalDueAt.Should().BeNull();
    }

    [Fact]
    public void A_ticket_that_was_never_moved_says_nothing_about_it()
        => TicketDueDateService.Describe(new Ticket
        {
            MspOrganizationId = Org, CorrelationId = Guid.NewGuid(), Title = "t", RequesterName = "r", RequesterEmail = "r@x.test",
            PortalStatus = "NEW", PortalPriority = "NORMAL", SlaDueAt = _clock.GetUtcNow(),
        }).Should().BeNull();
}
