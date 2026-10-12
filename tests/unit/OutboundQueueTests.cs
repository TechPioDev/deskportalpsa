using Desk.Api.Controllers;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A change made while the PSA cannot be reached is kept and sent later.
///
/// What these hold: that the change is kept and not handed back as an error; that it is only ever
/// Pending Sync, Synced or Sync Failed, and Synced only when the PSA has said so; that the waits
/// grow and stop, and what has failed is kept; that a PSA which answers "no" is not retried; that
/// nothing is sent twice because an answer was lost; that a status, which the PSA holds, is not
/// changed on the ticket until the PSA accepts it, and is not set over what the ticket has since
/// become; that one ticket's changes go in the order they were made; and that retrying and
/// letting go are a person's decision, and audited. On a SQL translator.
/// </summary>
public sealed class OutboundQueueTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly StubConnector _psa;
    private readonly TestCurrentUser _asha;
    private Guid _connectionId, _companyId, _ticketId, _ashaId, _priyaId;
    private double _jitter;

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    public OutboundQueueTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        _psa = new StubConnector(ProviderType.AutotaskPsa) { AcceptsWrites = true, SupportsTimeEntries = true, NoteClock = () => _clock.GetUtcNow() };

        var connection = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Main", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.example/",
            CredentialSecretRef = "mem://main", IsEnabled = true,
        };
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "Acme", ExternalCompanyId = "1" };
        var asha = new AppUser { MspOrganizationId = Org, DisplayName = "Asha Rao", Email = "asha@techpio.test", IsActive = true };
        var priya = new ClientUser { MspOrganizationId = Org, ClientCompanyId = company.Id, DisplayName = "Priya Nair", Email = "priya@acme.test", IdpSubject = "priya", IsCompanyAdministrator = true };
        var ticket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = connection.Id, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
            ExternalTicketId = "500", ClientCompanyId = company.Id, CorrelationId = Guid.NewGuid(), Title = "Printer offline",
            RequesterName = "Priya Nair", RequesterEmail = "priya@acme.test", RequesterUserId = priya.Id,
            PortalStatus = "NEW", PsaStatus = "NEW", PortalPriority = "NORMAL", SyncStatus = TicketSyncStatus.Synced,
        };
        (_connectionId, _companyId, _ticketId, _ashaId, _priyaId) = (connection.Id, company.Id, ticket.Id, asha.Id, priya.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" }, connection, company, asha, priya, ticket);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _asha = new TestCurrentUser(Org, userId: asha.Id, name: "Asha Rao");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- the pieces, built as the API and the worker build them --------------------------------

    private AuditWriter Audit => new(_db, _asha, _tenant, _clock);
    private OutboundQueue Queue => new(_db, _clock, Audit, () => _jitter);
    private TicketCommandService Commands => new(_db, new Resolver(_psa), new MappingEngine(), new SyncEventStore(_db, _clock), new NoopTicketScopeQuery(), _clock, new RecordingActivity(), Queue);
    private TicketStatusWriter StatusWriter => new(_db, new Resolver(_psa), new MappingEngine(), Audit, _asha);
    private TicketTimeWriter TimeWriter => new(_db, null!, Audit, Queue);
    private OutboundProcessor Processor => new(_db, Queue, new Resolver(_psa), TimeWriter, StatusWriter,
        new TicketResyncService(_db, new Resolver(_psa), new MappingEngine(), Audit, _clock), _clock);
    private TicketStatusController StatusController => new(_db, StatusWriter, new NoopTicketScopeQuery(), _asha, Queue);
    private TicketReadService Reads => new(_db, new NoopTicketScopeQuery(), _asha);
    private ClientAccess AsPriya => new(Org, _companyId, _priyaId, true);

    private static ConnectorException Away() => new(ConnectorFailureKind.Timeout, "Autotask request timed out.");
    /// <summary>The PSA could not be connected to at all: nothing was sent, so nothing was done there.</summary>
    private static ConnectorException Unreachable()
        => new(ConnectorFailureKind.Timeout, "Autotask request failed.", new HttpRequestException(HttpRequestError.ConnectionError));

    private Task<TicketNoteDto> ReplyAsync(string body, bool isPublic = true)
    {
        _db.ChangeTracker.Clear();
        return Commands.AddStaffCommentAsync(_ashaId, "Asha Rao", _ticketId, body, isPublic);
    }

    private async Task<List<OutboundOperation>> OperationsAsync()
    {
        _db.ChangeTracker.Clear();
        return await _db.OutboundOperations.AsNoTracking().OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync();
    }

    private async Task<Ticket> TicketAsync()
    {
        _db.ChangeTracker.Clear();
        return await _db.Tickets.AsNoTracking().Include(t => t.Notes).SingleAsync(t => t.Id == _ticketId);
    }

    /// <summary>One turn of the worker's loop: whatever is due is tried.</summary>
    private async Task<int> WorkerTurnAsync()
    {
        _db.ChangeTracker.Clear();
        return await Processor.RunDueAsync();
    }

    private async Task<List<string>> AuditedAsync(string prefix = "outbound.")
        => await _db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith(prefix)).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => a.Action + " " + a.DetailJson).ToListAsync();

    // ------------------------------------------------------------------ kept, then sent

    [Fact]
    public async Task A_reply_written_while_the_PSA_is_away_is_kept_and_is_not_called_synced_until_the_PSA_has_it()
    {
        _psa.WriteFailure = Unreachable();

        var written = await ReplyAsync("We are sending an engineer at 2pm.");

        written.SyncState.Should().Be("Pending Sync", "it is the author's work: kept, not handed back as an error to type again");
        var op = (await OperationsAsync()).Should().ContainSingle().Subject;
        op.Should().BeEquivalentTo(new
        {
            Kind = OutboundKind.Note, State = OutboundState.Pending, Summary = "Reply", TicketId = _ticketId, PsaConnectionId = _connectionId,
            Attempts = 1, SyncedAt = (DateTimeOffset?)null, ExternalId = (string?)null, RequestedByAppUserId = (Guid?)_ashaId, RequestedByName = "Asha Rao",
            LastError = "The PSA did not answer.", Uncertain = false, NextAttemptAt = (DateTimeOffset?)_clock.GetUtcNow().AddSeconds(30),
        });
        var ticket = await TicketAsync();
        ticket.Notes.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Body = "We are sending an engineer at 2pm.", ExternalNoteId = (string?)null, SyncState = (OutboundState?)OutboundState.Pending });
        ticket.FirstRespondedAt.Should().BeNull("nobody has been answered yet: the customer cannot read it");
        _psa.PostedNotes.Should().BeEmpty();

        // Staff see it on the ticket as waiting, with the change listed. The client is not shown a reply the PSA has not sent them.
        var staff = (await Reads.GetDetailForStaffAsync(_ticketId))!;
        staff.Conversation.Should().ContainSingle().Which.SyncState.Should().Be("Pending Sync");
        staff.Outbound.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = "Note", Summary = "Reply", State = "Pending Sync", Attempts = 1, MaxAttempts = 8, RequestedBy = "Asha Rao" });
        var client = (await Reads.GetDetailAsync(AsPriya, _ticketId))!;
        (client.Conversation.Count, client.Outbound).Should().Be((0, null));

        // The PSA is back. Not before its time: thirty seconds were set.
        _psa.WriteFailure = null;
        (await WorkerTurnAsync()).Should().Be(0);
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await WorkerTurnAsync()).Should().Be(1);

        op = (await OperationsAsync()).Single();
        op.Should().BeEquivalentTo(new { State = OutboundState.Synced, Attempts = 2, SyncedAt = (DateTimeOffset?)_clock.GetUtcNow(), LastError = (string?)null, NextAttemptAt = (DateTimeOffset?)null });
        op.ExternalId.Should().NotBeNullOrEmpty();
        ticket = await TicketAsync();
        ticket.Notes.Single().Should().BeEquivalentTo(new { ExternalNoteId = op.ExternalId, SyncState = (OutboundState?)OutboundState.Synced });
        ticket.FirstRespondedAt.Should().Be(_clock.GetUtcNow(), "answered when the customer could read it");
        _psa.PostedNotes.Should().ContainSingle().Which.Note.Should().BeEquivalentTo(new { Body = "We are sending an engineer at 2pm.", IsPublic = true });
        (await Reads.GetDetailAsync(AsPriya, _ticketId))!.Conversation.Should().ContainSingle().Which.SyncState.Should().BeNull();
        (await Reads.GetDetailForStaffAsync(_ticketId))!.Outbound.Should().BeNull("a change that is synced is simply the ticket as it is");
        (await WorkerTurnAsync()).Should().Be(0, "and it is not sent again");
        (await AuditedAsync()).Should().ContainSingle().Which.Should().StartWith("outbound.queued").And.Contain("Asha Rao").And.Contain("The PSA did not answer.");
    }

    [Fact]
    public async Task A_PSA_that_answers_no_is_an_answer_and_nothing_is_queued()
    {
        // Refused in so many words.
        _psa.WriteRefusal = "Notes cannot be added to a completed ticket.";
        (await FluentActions.Awaiting(() => ReplyAsync("Hello")).Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Notes cannot be added to a completed ticket.");
        // Refused for its rights: not something waiting will change.
        (_psa.WriteRefusal, _psa.WriteFailure) = (null, new ConnectorException(ConnectorFailureKind.PermissionDenied, "The API user may not add notes."));
        await FluentActions.Awaiting(() => ReplyAsync("Hello")).Should().ThrowAsync<ConnectorException>();

        (await OperationsAsync()).Should().BeEmpty();
        (await TicketAsync()).Notes.Should().BeEmpty("a note the PSA refused is not kept as though it might yet be sent");
    }

    // ------------------------------------------------------------------ the waits, and the end of them

    [Fact]
    public async Task The_waits_double_from_thirty_seconds_and_after_eight_tries_it_is_failed_and_kept()
    {
        _psa.WriteFailure = Away();
        await ReplyAsync("Still waiting on the part.");

        var waits = new List<double>();
        while ((await OperationsAsync()).Single() is { State: OutboundState.Pending, NextAttemptAt: { } next })
        {
            waits.Add((next - _clock.GetUtcNow()).TotalSeconds);
            _clock.Advance(next - _clock.GetUtcNow());
            (await WorkerTurnAsync()).Should().Be(1);
        }

        waits.Should().Equal([30, 60, 120, 240, 480, 960, 1920], "thirty seconds, then double each time");
        var op = (await OperationsAsync()).Single();
        op.Should().BeEquivalentTo(new { State = OutboundState.Failed, Attempts = 8, NextAttemptAt = (DateTimeOffset?)null, SyncedAt = (DateTimeOffset?)null });
        op.LastError.Should().StartWith("The PSA could not be reached in 8 tries.");
        (await TicketAsync()).Notes.Single().SyncState.Should().Be(OutboundState.Failed, "the reply is still there, and says it was not sent");
        (await Reads.GetDetailForStaffAsync(_ticketId))!.Outbound.Should().ContainSingle().Which.State.Should().Be("Sync Failed");

        // Failed is kept, and left alone: nothing tries it again by itself, however long it waits.
        _clock.Advance(TimeSpan.FromDays(3));
        _psa.WriteFailure = null;
        (await WorkerTurnAsync()).Should().Be(0);
        _psa.PostedNotes.Should().BeEmpty();
        (await AuditedAsync()).Select(a => a.Split(' ')[0]).Should().Equal("outbound.queued", "outbound.failed");
    }

    [Fact]
    public async Task The_wait_is_spread_by_up_to_a_fifth_and_a_PSA_that_says_when_to_come_back_is_taken_at_its_word()
    {
        _jitter = 1;
        _psa.WriteFailure = Unreachable();
        await ReplyAsync("One");
        ((await OperationsAsync()).Single().NextAttemptAt - _clock.GetUtcNow()).Should().Be(TimeSpan.FromSeconds(36), "thirty seconds and a fifth");

        _jitter = -1;
        _clock.Advance(TimeSpan.FromSeconds(36));
        _psa.WriteFailure = new ConnectorException(ConnectorFailureKind.RateLimited, "Too many requests.") { RetryAfter = TimeSpan.FromSeconds(90) };
        await WorkerTurnAsync();
        var op = (await OperationsAsync()).Single();
        (op.NextAttemptAt - _clock.GetUtcNow()).Should().Be(TimeSpan.FromSeconds(90), "the PSA said ninety seconds; its rate limit is respected, not second-guessed");
        op.LastError.Should().Be("The PSA is taking no more requests for the moment.");

        _clock.Advance(TimeSpan.FromSeconds(90));
        _psa.WriteFailure = Away();
        await WorkerTurnAsync();
        ((await OperationsAsync()).Single().NextAttemptAt - _clock.GetUtcNow()).Should().Be(TimeSpan.FromSeconds(96), "the third try's two minutes, less a fifth");
    }

    // ------------------------------------------------------------------ never twice

    [Fact]
    public async Task A_reply_whose_answer_was_lost_is_looked_for_in_the_PSA_and_is_not_sent_a_second_time()
    {
        // The PSA takes the note and the answer never arrives.
        (_psa.WriteFailure, _psa.WritesLandButAnswerIsLost) = (Away(), true);
        await ReplyAsync("Your   replacement laptop\nships tomorrow.");
        _psa.PostedNotes.Should().ContainSingle();
        var op = (await OperationsAsync()).Single();
        (op.State, op.Uncertain).Should().Be((OutboundState.Pending, true), "it is not known whether the PSA has it");

        // Meanwhile a sync reads the PSA's thread and keeps the PSA's copy as one of the PSA's own.
        var theirs = _psa.Notes["500"].Single();
        _db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = Org, TicketId = _ticketId, ExternalNoteId = theirs.ExternalId, AuthorName = "Integration", Body = theirs.Body,
            IsPublic = true, NoteCreatedAt = theirs.CreatedAt, ImportedFromProvider = true,
        });
        await _db.SaveChangesAsync();

        (_psa.WriteFailure, _psa.WritesLandButAnswerIsLost) = (null, false);
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await WorkerTurnAsync()).Should().Be(1);

        _psa.PostedNotes.Should().ContainSingle("it was found there, and was not said to the customer twice");
        op = (await OperationsAsync()).Single();
        (op.State, op.ExternalId).Should().Be((OutboundState.Synced, theirs.ExternalId));
        var notes = (await TicketAsync()).Notes;
        notes.Should().ContainSingle("the copy the sync made is this note, and the thread says it once")
            .Which.Should().BeEquivalentTo(new { AuthorName = "Asha Rao", ExternalNoteId = theirs.ExternalId, ImportedFromProvider = false, SyncState = (OutboundState?)OutboundState.Synced });
    }

    [Fact]
    public async Task A_reply_that_certainly_never_left_is_sent_without_asking_and_two_the_same_are_two()
    {
        _psa.WriteFailure = Unreachable();
        await ReplyAsync("Thanks, closing this.");
        _clock.Advance(TimeSpan.FromSeconds(1));
        await ReplyAsync("Thanks, closing this.");
        (await OperationsAsync()).Should().OnlyContain(o => !o.Uncertain);
        var reads = _psa.NoteReads;

        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        // Of one ticket's changes the oldest goes first, and the next not until it has gone.
        (await WorkerTurnAsync()).Should().Be(1);
        (await OperationsAsync()).Select(o => o.State).Should().Equal(OutboundState.Synced, OutboundState.Pending);
        (await WorkerTurnAsync()).Should().Be(1);

        _psa.NoteReads.Should().Be(reads, "nothing was sent before, so there is nothing to look for");
        _psa.PostedNotes.Should().HaveCount(2, "the same words written twice are two replies");
        (await TicketAsync()).Notes.Select(n => n.ExternalNoteId).Should().OnlyHaveUniqueItems().And.NotContainNulls();
    }

    // ------------------------------------------------------------------ a status is the PSA's

    private async Task<(int Code, string PortalStatus, bool Queued)> AskForStatusAsync(string status, string? resolution = null)
    {
        _db.ChangeTracker.Clear();
        var result = await StatusController.SetStatus(_ticketId, new TicketStatusController.SetStatusRequest(status, resolution), default);
        var (code, value) = result switch
        {
            AcceptedResult a => (202, a.Value!),
            OkObjectResult ok => (200, ok.Value!),
            _ => throw new InvalidOperationException(result.GetType().Name),
        };
        var json = System.Text.Json.JsonSerializer.SerializeToElement(value);
        return (code, json.GetProperty("portalStatus").GetString()!, json.TryGetProperty("queued", out var q) && q.GetBoolean());
    }

    [Fact]
    public async Task A_status_asked_for_while_the_PSA_is_away_does_not_change_the_ticket_until_the_PSA_accepts_it()
    {
        _psa.WriteFailure = Unreachable();

        var asked = await AskForStatusAsync("RESOLVED", "Replaced the fuser.");

        asked.Should().Be((202, "NEW", true), "accepted to be sent; the ticket is still what the PSA last said it was");
        var ticket = await TicketAsync();
        (ticket.PortalStatus, ticket.PsaStatus, ticket.Resolution, ticket.ResolvedByAppUserId).Should().Be(("NEW", "NEW", null, null));
        (await OperationsAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = OutboundKind.StatusChange, State = OutboundState.Pending, Summary = "Status to RESOLVED", RequestedByName = "Asha Rao" });
        // One at a time: a second change is not stacked on one that has not been answered.
        (await FluentActions.Awaiting(() => AskForStatusAsync("CLOSED")).Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("already waiting").And.Contain("Status to RESOLVED");

        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await WorkerTurnAsync()).Should().Be(1);

        _psa.Updates.Should().ContainSingle().Which.Update.Status.Should().Be("RESOLVED");
        ticket = await TicketAsync();
        (ticket.PortalStatus, ticket.Resolution, ticket.ResolvedByAppUserId).Should().Be(("RESOLVED", "Replaced the fuser.", _ashaId), "and the credit is whose change it was, though the worker sent it");
        (await OperationsAsync()).Single().State.Should().Be(OutboundState.Synced);
        // With the PSA there, a change is made at once and nothing waits.
        (await AskForStatusAsync("CLOSED")).Should().Be((200, "CLOSED", false));
        (await OperationsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_waiting_status_is_not_set_over_what_the_ticket_has_since_become()
    {
        _psa.WriteFailure = Unreachable();
        await AskForStatusAsync("WAITING_CUSTOMER");

        // While it waited the PSA moved the ticket, and a sync brought that in.
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        (ticket.PortalStatus, ticket.PsaStatus) = ("IN_PROGRESS", "In Progress");
        await _db.SaveChangesAsync();
        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromSeconds(31));
        await WorkerTurnAsync();

        _psa.Updates.Should().BeEmpty("the PSA holds a ticket's status; what somebody wanted earlier is not sent over what it says now");
        var op = (await OperationsAsync()).Single();
        op.State.Should().Be(OutboundState.Failed);
        op.LastError.Should().Contain("was NEW when the change to WAITING_CUSTOMER was asked for").And.Contain("has since become IN_PROGRESS");
        (await TicketAsync()).PortalStatus.Should().Be("IN_PROGRESS");
    }

    [Fact]
    public async Task A_waiting_status_the_ticket_already_has_is_done_and_one_the_PSA_refuses_is_failed_with_its_words()
    {
        _psa.WriteFailure = Unreachable();
        await AskForStatusAsync("IN_PROGRESS");
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        ticket.PortalStatus = "IN_PROGRESS";      // it got there by another road
        await _db.SaveChangesAsync();
        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromSeconds(31));
        await WorkerTurnAsync();
        (await OperationsAsync()).Single().State.Should().Be(OutboundState.Synced);
        _psa.Updates.Should().BeEmpty("there was nothing left to send");

        _psa.WriteFailure = Unreachable();
        await AskForStatusAsync("RESOLVED");
        (_psa.WriteFailure, _psa.WriteRefusal) = (null, "This board does not allow Resolved from In Progress.");
        _clock.Advance(TimeSpan.FromSeconds(31));
        await WorkerTurnAsync();

        var refused = (await OperationsAsync()).Last();
        (refused.State, refused.LastError, refused.Attempts).Should().Be((OutboundState.Failed, "This board does not allow Resolved from In Progress.", 2), "refused is refused: it is not tried six more times");
        (await TicketAsync()).PortalStatus.Should().Be("IN_PROGRESS", "the ticket never showed a status the PSA would not take");
    }

    // ------------------------------------------------------------------ a person's decision

    [Fact]
    public async Task A_failed_change_is_sent_round_again_or_let_go_of_by_a_person_and_both_are_audited()
    {
        _psa.WriteRefusal = null;
        _psa.WriteFailure = Away();
        await ReplyAsync("First");
        _clock.Advance(TimeSpan.FromSeconds(1));
        await ReplyAsync("Second", isPublic: false);
        var (first, second) = ((await OperationsAsync())[0], (await OperationsAsync())[1]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Queue.FailAsync(await _db.OutboundOperations.SingleAsync(o => o.Id == first.Id), "The PSA could not be reached in 8 tries.");
        _db.ChangeTracker.Clear();

        // Retried by Asha: from the first try again, and due now.
        _clock.Advance(TimeSpan.FromSeconds(1));
        var again = await Queue.RetryAsync(first.Id, "Asha Rao");
        (again.State, again.Attempts, again.NextAttemptAt).Should().Be((OutboundState.Pending, 0, null));
        (await TicketAsync()).Notes.Single(n => n.Body == "First").SyncState.Should().Be(OutboundState.Pending);
        _psa.WriteFailure = null;
        (await WorkerTurnAsync()).Should().Be(1);
        (await OperationsAsync()).Single(o => o.Id == first.Id).State.Should().Be(OutboundState.Synced);
        await FluentActions.Awaiting(() => Queue.RetryAsync(first.Id, "Asha Rao")).Should().ThrowAsync<ValidationFailedException>().WithMessage("*already in the PSA*");
        await FluentActions.Awaiting(() => Queue.DiscardAsync(first.Id, "Asha Rao")).Should().ThrowAsync<ValidationFailedException>().WithMessage("*cannot be taken back*");

        // The second is let go of. It is not sent, and the note that was never sent goes with it.
        _db.ChangeTracker.Clear();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Queue.DiscardAsync(second.Id, "Asha Rao");
        (await OperationsAsync()).Should().ContainSingle().Which.Id.Should().Be(first.Id);
        (await TicketAsync()).Notes.Select(n => n.Body).Should().Equal("First");
        _clock.Advance(TimeSpan.FromHours(2));
        (await WorkerTurnAsync()).Should().Be(0);
        _psa.PostedNotes.Should().ContainSingle();

        var audited = await AuditedAsync();
        audited.Select(a => a.Split(' ')[0]).Should().Equal("outbound.queued", "outbound.queued", "outbound.failed", "outbound.retried", "outbound.discarded");
        audited[3].Should().Contain("\"by\":\"Asha Rao\"").And.Contain("Sync Failed");
        audited[4].Should().Contain("\"by\":\"Asha Rao\"").And.Contain("Internal note").And.Contain("Pending Sync");
        // By the ticket, and only by whoever may change that ticket: a change is not reached through another ticket's address.
        var controller = new TicketOutboundController(_db, new NoopTicketScopeQuery(), Queue, _asha);
        await FluentActions.Awaiting(() => controller.Retry(Guid.NewGuid(), first.Id, default)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => controller.Discard(_ticketId, Guid.NewGuid(), default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task One_worker_takes_a_change_and_a_worker_that_stops_does_not_strand_it()
    {
        _psa.WriteFailure = Unreachable();
        await ReplyAsync("Hello");
        var id = (await OperationsAsync()).Single().Id;
        _clock.Advance(TimeSpan.FromSeconds(31));

        // Two workers ask for it at the same moment. One has it.
        _db.ChangeTracker.Clear();
        (await Queue.ClaimAsync(id)).Should().NotBeNull();
        _db.ChangeTracker.Clear();
        (await Queue.ClaimAsync(id)).Should().BeNull("it is held");
        (await Queue.DueAsync(10)).Should().BeEmpty();
        // The one that has it stops. Its hold runs out, and the change is there to be taken again.
        _clock.Advance(OutboundQueue.Lease + TimeSpan.FromSeconds(1));
        (await Queue.DueAsync(10)).Should().Equal(id);
        _psa.WriteFailure = null;
        (await WorkerTurnAsync()).Should().Be(1);
        (await OperationsAsync()).Single().State.Should().Be(OutboundState.Synced);
    }

    [Fact]
    public async Task A_change_a_worker_is_sending_at_this_moment_is_not_freed_to_be_sent_by_a_second()
    {
        _psa.WriteFailure = Unreachable();
        await ReplyAsync("Hello");
        var id = (await OperationsAsync()).Single().Id;
        _clock.Advance(TimeSpan.FromSeconds(31));
        _db.ChangeTracker.Clear();
        (await Queue.ClaimAsync(id)).Should().NotBeNull();

        // "Try now", pressed while the worker has it, would clear the hold; a second worker could then send it as well.
        _db.ChangeTracker.Clear();
        await FluentActions.Awaiting(() => Queue.RetryAsync(id, "Asha Rao")).Should().ThrowAsync<ValidationFailedException>().WithMessage("*being sent at this moment*");
        _db.ChangeTracker.Clear();
        await FluentActions.Awaiting(() => Queue.DiscardAsync(id, "Asha Rao")).Should().ThrowAsync<ValidationFailedException>().WithMessage("*being sent at this moment*");
        _db.ChangeTracker.Clear();
        (await Queue.DueAsync(10)).Should().BeEmpty("it is still held");

        _clock.Advance(OutboundQueue.Lease + TimeSpan.FromSeconds(1));
        _db.ChangeTracker.Clear();
        (await Queue.RetryAsync(id, "Asha Rao")).State.Should().Be(OutboundState.Pending);
    }

    // ------------------------------------------------------------------ as the worker runs it

    private sealed class Breaking(Func<IServiceManagementConnector> get) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(get());
    }

    /// <summary>
    /// The services as the worker has them: a unit of work for each scope, nobody signed in, and
    /// the runner over them. Each scope's tenant is whatever the runner sets it to.
    /// </summary>
    private (OutboundRunner Runner, ServiceProvider Services) Worker(Func<IServiceManagementConnector>? connector = null)
    {
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options;
        var system = new TestCurrentUser(null, subject: "system", name: "System");
        var resolver = new Breaking(connector ?? (() => _psa));
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<Desk.Application.Abstractions.ISettableTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped(sp => new DeskDbContext(options, sp.GetRequiredService<TenantContext>(), _clock));
        services.AddScoped(sp => new AuditWriter(sp.GetRequiredService<DeskDbContext>(), system, sp.GetRequiredService<TenantContext>(), _clock));
        services.AddScoped(sp => new OutboundQueue(sp.GetRequiredService<DeskDbContext>(), _clock, sp.GetRequiredService<AuditWriter>(), () => _jitter));
        services.AddScoped(sp =>
        {
            var (db, audit, queue) = (sp.GetRequiredService<DeskDbContext>(), sp.GetRequiredService<AuditWriter>(), sp.GetRequiredService<OutboundQueue>());
            return new OutboundProcessor(db, queue, resolver, new TicketTimeWriter(db, null!, audit, queue),
                new TicketStatusWriter(db, resolver, new MappingEngine(), audit, system),
                new TicketResyncService(db, resolver, new MappingEngine(), audit, _clock), _clock);
        });
        var provider = services.BuildServiceProvider();
        return (new OutboundRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboundRunner>.Instance), provider);
    }

    /// <summary>A second organization, with a ticket of its own and a reply of its own waiting.</summary>
    private async Task<(Guid Org, Guid Ticket, Guid Operation)> AnotherOrganizationWithAReplyWaitingAsync()
    {
        var other = Guid.NewGuid();
        var tenant = new TenantContext();
        tenant.SetTenant(other);
        await using var db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, tenant, _clock);
        var connection = new PsaConnection
        {
            MspOrganizationId = other, Name = "Theirs", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://other.example/",
            CredentialSecretRef = "mem://other", IsEnabled = true,
        };
        var company = new ClientCompany { MspOrganizationId = other, PsaConnectionId = connection.Id, Name = "Globex", ExternalCompanyId = "1" };
        var ticket = new Ticket
        {
            MspOrganizationId = other, PsaConnectionId = connection.Id, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
            ExternalTicketId = "700", ClientCompanyId = company.Id, CorrelationId = Guid.NewGuid(), Title = "No dial tone",
            RequesterName = "Lena Wu", RequesterEmail = "lena@globex.test",
            PortalStatus = "NEW", PsaStatus = "NEW", PortalPriority = "NORMAL", SyncStatus = TicketSyncStatus.Synced,
        };
        var note = new TicketNote
        {
            MspOrganizationId = other, TicketId = ticket.Id, AuthorName = "Ben Okafor", Body = "On our way.", IsPublic = true,
            NoteCreatedAt = _clock.GetUtcNow(), SyncState = OutboundState.Pending,
        };
        db.AddRange(new MspOrganization { Id = other, Name = "Another desk", Slug = "another", TimeZone = "UTC" }, connection, company, ticket, note);
        var op = new OutboundQueue(db, _clock, null, () => 0).Enqueue(ticket, OutboundKind.Note, "Reply", new OutboundNote(note.Id, true, false, []),
            note.Id, Guid.NewGuid().ToString("N"), null, null, "Ben Okafor", "The PSA did not answer.", uncertain: false);
        await db.SaveChangesAsync();
        return (other, ticket.Id, op.Id);
    }

    [Fact]
    public async Task The_worker_sends_each_change_inside_its_own_organization_and_what_it_records_is_that_organizations()
    {
        _psa.WriteFailure = Unreachable();
        await ReplyAsync("We are sending an engineer at 2pm.");
        var theirs = await AnotherOrganizationWithAReplyWaitingAsync();
        // The PSA is back, takes the first and refuses the second: one is synced and one is failed, each for its own organization.
        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromSeconds(40));
        var (runner, services) = Worker();
        await using var _ = services;

        _psa.RefuseNotesOn = "700";
        (await runner.RunDueAsync()).Should().Be(2, "the worker asks once for every organization's, and takes each in its own");

        _db.ChangeTracker.Clear();
        var ops = await _db.OutboundOperations.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        ops.Single(o => o.MspOrganizationId == Org).State.Should().Be(OutboundState.Synced);
        ops.Single(o => o.Id == theirs.Operation).Should().BeEquivalentTo(new { State = OutboundState.Failed, MspOrganizationId = theirs.Org, LastError = "Notes cannot be added to a completed ticket." });
        // What the worker recorded belongs to the organization whose change it was. Written with every
        // organization in view it would belong to none, and nobody would ever read it.
        var failed = await _db.AuditLog.IgnoreQueryFilters().AsNoTracking().Where(a => a.Action == "outbound.failed")
            .Select(a => new { a.MspOrganizationId, a.EntityId, a.ActorDisplayName }).ToListAsync();
        failed.Should().ContainSingle().Which.Should().BeEquivalentTo(new { MspOrganizationId = (Guid?)theirs.Org, EntityId = theirs.Ticket.ToString(), ActorDisplayName = "System" });
        (await AuditedAsync()).Should().ContainSingle("this organization reads its own log, and the other's failure is not in it").Which.Should().StartWith("outbound.queued");
        (await TicketAsync()).FirstRespondedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task A_fault_of_the_portals_own_is_a_counted_try_and_the_change_runs_out_of_tries_like_any_other()
    {
        _psa.WriteFailure = Unreachable();
        await ReplyAsync("Hello");
        _psa.WriteFailure = null;
        var broken = true;
        var (runner, services) = Worker(() => broken ? throw new InvalidOperationException("Sequence contains no elements") : _psa);
        await using var _ = services;

        _clock.Advance(TimeSpan.FromSeconds(31));
        (await runner.RunDueAsync()).Should().Be(1);

        var op = (await OperationsAsync()).Single();
        (op.State, op.Attempts, op.Uncertain, op.LeaseExpiresAt).Should().Be((OutboundState.Pending, 2, true, null), "a try, and it is not known what it did");
        op.LastError.Should().StartWith("The portal met a fault of its own").And.NotContain("Sequence", "what went wrong inside is for the log, not for the ticket");
        (op.NextAttemptAt - _clock.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(1), "it waits its turn; it is not taken again every few seconds");

        while ((await OperationsAsync()).Single() is { State: OutboundState.Pending, NextAttemptAt: { } next })
        {
            _clock.Advance(next - _clock.GetUtcNow());
            (await runner.RunDueAsync()).Should().Be(1);
        }
        op = (await OperationsAsync()).Single();
        (op.State, op.Attempts).Should().Be((OutboundState.Failed, 8), "failed and kept, where it would have been taken again for ever");
        (await TicketAsync()).Notes.Single().Should().BeEquivalentTo(new { SyncState = (OutboundState?)OutboundState.Failed, ExternalNoteId = (string?)null });
        (await AuditedAsync()).Select(a => a.Split(' ')[0]).Should().Equal("outbound.queued", "outbound.failed");

        // Mended, and sent again by a person: looked for in the PSA first, since an earlier try may have got there, then sent once.
        broken = false;
        var reads = _psa.NoteReads;
        _db.ChangeTracker.Clear();
        await Queue.RetryAsync(op.Id, "Asha Rao");
        (await runner.RunDueAsync()).Should().Be(1);
        (_psa.NoteReads - reads, _psa.PostedNotes.Count).Should().Be((1, 1));
        (await OperationsAsync()).Single().State.Should().Be(OutboundState.Synced);
    }

    [Fact]
    public async Task A_ticket_whose_sending_broke_part_way_is_not_created_until_a_person_has_looked()
    {
        _psa.WriteFailure = Unreachable();
        _db.ChangeTracker.Clear();
        var raised = await Commands.CreateAsync(AsPriya, new CreateTicketInput("The scanner will not feed", "Since this morning", "HIGH", null, null));
        _psa.WriteFailure = null;
        var broken = true;
        var (runner, services) = Worker(() => broken ? throw new InvalidOperationException("boom") : _psa);
        await using var _ = services;

        // The try breaks inside the portal. Whether the PSA was asked for a ticket is not known.
        _clock.Advance(TimeSpan.FromSeconds(31));
        await runner.RunDueAsync();
        (await OperationsAsync()).Single().Should().BeEquivalentTo(new { State = OutboundState.Pending, Uncertain = true });

        // Mended. It is still not sent: there is no asking a PSA whether it created a ticket.
        broken = false;
        _clock.Advance(TimeSpan.FromMinutes(2));
        await runner.RunDueAsync();
        _psa.CreateRequests.Should().BeEmpty();
        var op = (await OperationsAsync()).Single();
        op.State.Should().Be(OutboundState.Failed);
        op.LastError.Should().Contain("It is not known whether the PSA created this ticket").And.Contain("only if it is not there");
        (await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == raised.Id)).SyncStatus.Should().Be(TicketSyncStatus.Error);

        // Somebody looks, finds none, and sends it again. That is the decision it waited for.
        _db.ChangeTracker.Clear();
        await Queue.RetryAsync(op.Id, "Asha Rao");
        await runner.RunDueAsync();
        _psa.CreateRequests.Should().ContainSingle();
        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == raised.Id);
        (ticket.SyncStatus, ticket.ExternalTicketId).Should().Be((TicketSyncStatus.Synced, "9001"));
    }

    [Fact]
    public void The_registration_the_worker_runs_can_build_everything_a_turn_needs()
    {
        // The worker's own container: the shared registration, and nobody signed in. The tests above
        // build the processor by hand; this is that the real one can be built at all, in a scope
        // that belongs to one organization, which an empty queue would never have shown.
        var services = new ServiceCollection();
        services.AddLogging();
        Desk.Infrastructure.DependencyInjection.AddDeskInfrastructure(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=unused", ["Secrets:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            }).Build());
        services.AddScoped<Desk.Application.Abstractions.ICurrentUser>(_ => new TestCurrentUser(null, subject: "system", name: "System"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        provider.GetRequiredService<OutboundRunner>().Should().NotBeNull();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<Desk.Application.Abstractions.ISettableTenantContext>().SetTenant(Guid.NewGuid());
        scope.ServiceProvider.GetRequiredService<OutboundProcessor>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<Desk.Application.Abstractions.ITenantContext>().OrganizationId.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ the client's own words, an hour, a ticket

    [Fact]
    public async Task A_clients_comment_is_kept_too_and_they_see_it_marked_as_waiting()
    {
        _psa.WriteFailure = Away();
        _db.ChangeTracker.Clear();
        var written = await Commands.AddCommentAsync(AsPriya, _ticketId, "It is still jamming.");

        written.SyncState.Should().Be("Pending Sync");
        (await OperationsAsync()).Single().Should().BeEquivalentTo(new { Kind = OutboundKind.Note, Summary = "Comment from the client", RequestedByClientUserId = (Guid?)_priyaId, RequestedByAppUserId = (Guid?)null });
        var theirs = (await Reads.GetDetailAsync(AsPriya, _ticketId))!;
        theirs.Conversation.Should().ContainSingle("it is their own comment: they are shown it, and that it is on its way").Which.SyncState.Should().Be("Pending Sync");
        theirs.Outbound.Should().BeNull("how the desk's queue stands is the desk's");

        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromSeconds(31));
        await WorkerTurnAsync();
        (await Reads.GetDetailAsync(AsPriya, _ticketId))!.Conversation.Single().SyncState.Should().BeNull();
        _psa.PostedNotes.Should().ContainSingle().Which.Note.IsPublic.Should().BeTrue();
    }

    [Fact]
    public async Task An_hour_logged_while_the_PSA_is_away_waits_and_is_looked_for_before_it_is_sent_again()
    {
        _psa.TimeEntryFailure = Away();
        _db.ChangeTracker.Clear();
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        var logged = await TimeWriter.LogAsync(ticket, _psa, _ashaId, 1.5m, true, "Replaced the fuser", null, null, null, _clock.GetUtcNow(), null, default);

        (logged.Entry.SyncStatus, logged.Entry.ExternalEntryId).Should().Be((TimeEntrySyncStatus.Pending, null), "waiting, not failed: it will be tried again without anybody having to notice");
        (await OperationsAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = OutboundKind.TimeEntry, State = OutboundState.Pending, Summary = "1.5 h logged", TargetId = (Guid?)logged.Entry.Id, RequestedByName = "Asha Rao" });

        (_psa.TimeEntryFailure, _psa.NextTimeEntryResult) = (null, new CreateTimeEntryResult(true, "te-77", null));
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await WorkerTurnAsync()).Should().Be(1);

        _psa.TimeEntryLookups.Should().ContainSingle("a second send of an hour could bill it twice, so the PSA is asked first whether it has it");
        _psa.PushedTime.Should().ContainSingle("it was not there, so it was sent: once");
        var entry = await _db.TicketTimeEntries.AsNoTracking().SingleAsync();
        (entry.SyncStatus, entry.ExternalEntryId, entry.SyncError).Should().Be((TimeEntrySyncStatus.Synced, "te-77", null));
        (await OperationsAsync()).Single().Should().BeEquivalentTo(new { State = OutboundState.Synced, ExternalId = "te-77" });

        // One the PSA refuses outright is failed on the entry at once, as before, and nothing is queued for it.
        _psa.TimeEntryFailure = new ConnectorException(ConnectorFailureKind.InvalidRequest, "The role is not valid for this resource.");
        _db.ChangeTracker.Clear();
        ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        var refused = await TimeWriter.LogAsync(ticket, _psa, _ashaId, 1m, true, null, null, null, null, _clock.GetUtcNow(), null, default);
        (refused.Entry.SyncStatus, refused.Entry.SyncError).Should().Be((TimeEntrySyncStatus.Failed, "The role is not valid for this resource."));
        (await OperationsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_ticket_raised_while_the_PSA_cannot_be_connected_to_is_kept_and_created_once_when_it_can()
    {
        _psa.WriteFailure = Unreachable();
        _db.ChangeTracker.Clear();

        var raised = await Commands.CreateAsync(AsPriya, new CreateTicketInput("The scanner will not feed", "Since this morning", "HIGH", null, null));

        raised.ExternalTicketId.Should().BeNull("it is theirs and it is kept; it is not said to have reached the desk");
        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == raised.Id);
        (ticket.SyncStatus, ticket.ExternalTicketId, ticket.RequesterUserId).Should().Be((TicketSyncStatus.PendingCreate, null, _priyaId));
        (await OperationsAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = OutboundKind.TicketCreate, State = OutboundState.Pending, Summary = "New ticket", TicketId = raised.Id });
        _psa.CreateRequests.Should().BeEmpty();

        _psa.WriteFailure = null;
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await WorkerTurnAsync()).Should().Be(1);
        ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == raised.Id);
        (ticket.SyncStatus, ticket.ExternalTicketId).Should().Be((TicketSyncStatus.Synced, "9001"));
        _psa.CreateRequests.Should().ContainSingle();
        (await OperationsAsync()).Single().Should().BeEquivalentTo(new { State = OutboundState.Synced, ExternalId = "9001" });
    }

    [Fact]
    public async Task A_ticket_that_may_have_been_created_is_not_created_again_by_a_machine()
    {
        // The PSA takes the request and the answer is lost. Whether there is a ticket there is not known.
        (_psa.WriteFailure, _psa.WritesLandButAnswerIsLost) = (Away(), true);
        _db.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => Commands.CreateAsync(AsPriya, new CreateTicketInput("The scanner will not feed", null, "HIGH", null, null)))
            .Should().ThrowAsync<ValidationFailedException>("as before: the customer is not told it reached the desk");

        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.Title == "The scanner will not feed");
        ticket.SyncStatus.Should().Be(TicketSyncStatus.Error, "kept for a person to check and resend, as it always was");
        (await OperationsAsync()).Should().BeEmpty("a second ticket for one request is worse than one that waits to be looked at");
        _psa.CreateRequests.Should().ContainSingle();
        (_psa.WriteFailure, _psa.WritesLandButAnswerIsLost) = (null, false);
        _clock.Advance(TimeSpan.FromHours(1));
        (await WorkerTurnAsync()).Should().Be(0);
        _psa.CreateRequests.Should().ContainSingle();
    }

    [Fact]
    public void Only_a_failure_that_certainly_did_nothing_is_called_never_sent()
    {
        Unreachable().NeverSent.Should().BeTrue("it could not be connected to");
        new ConnectorException(ConnectorFailureKind.Timeout, "x", new HttpRequestException(HttpRequestError.NameResolutionError)).NeverSent.Should().BeTrue();
        new ConnectorException(ConnectorFailureKind.RateLimited, "Too many requests.").NeverSent.Should().BeTrue("turned away before it was looked at");
        Away().NeverSent.Should().BeFalse("sent, and no answer: it may have been carried out");
        new ConnectorException(ConnectorFailureKind.ProviderError, "500").NeverSent.Should().BeFalse();
        new ConnectorException(ConnectorFailureKind.Timeout, "x", new HttpRequestException(HttpRequestError.ResponseEnded)).NeverSent.Should().BeFalse("the answer broke off part way");
        OutboundQueue.Label(OutboundState.Pending).Should().Be("Pending Sync");
        OutboundQueue.Label(OutboundState.Synced).Should().Be("Synced");
        OutboundQueue.Label(OutboundState.Failed).Should().Be("Sync Failed");
        Enum.GetValues<OutboundState>().Should().HaveCount(3, "three states, and no fourth");
    }
}
