using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Time a technician entered in the PSA itself is kept in the portal as a worklog.
///
/// What these hold: that everything about the entry is kept as the PSA has it; that one entry is
/// one worklog however often it is read, with the database refusing a second; that an entry logged
/// from the portal and read back is not copied; that a worklog follows a change or a deletion in
/// the PSA and survives a read that failed; that nothing read from the PSA is ever sent to it; and
/// that whose time it is follows the login's link, on its own connection, and never the account the
/// portal writes as. On a SQL translator, because the refusal of a second row is the database's.
/// </summary>
public sealed class ProviderWorklogTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly DeskDbContext _db;
    private readonly StubConnector _psa = new(ProviderType.AutotaskPsa) { Paged = true, SupportsTimeEntries = true };
    private Guid _main, _other, _asha, _ben;

    public ProviderWorklogTests()
    {
        _connection.Open();
        // The sync's own view: every organization's rows, as the worker runs.
        var platform = new TenantContext();
        platform.SetPlatformScope();
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, platform, _clock);
        _db.Database.EnsureCreated();
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        PsaConnection Of(string name, string? account = null) => new()
        {
            MspOrganizationId = Org, Name = name, Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.example/",
            CredentialSecretRef = "mem://" + name, Status = ConnectionStatus.Healthy, IsEnabled = true, SyncAttachments = false,
            DefaultTimeEntryResourceId = account,
        };
        // The portal writes to the main account as login 900: that login is the integration, not a person.
        var (main, other) = (Of("Main", account: "900"), Of("Second account"));
        (_main, _other) = (main.Id, other.Id);
        var asha = new AppUser { MspOrganizationId = Org, DisplayName = "Asha Rao", Email = "asha@techpio.test", IsActive = true };
        var ben = new AppUser { MspOrganizationId = Org, DisplayName = "Ben Okafor", Email = "ben@techpio.test", IsActive = true };
        (_asha, _ben) = (asha.Id, ben.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" }, main, other, asha, ben,
            // On the main account login 41 is Asha. On the second account 41 is somebody else, linked to nobody.
            new UserPsaIdentity { MspOrganizationId = Org, AppUserId = asha.Id, PsaConnectionId = main.Id, ExternalTechnicianId = "41", ExternalTechnicianName = "Asha R" });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private async Task<Ticket> TicketAsync(string externalId = "500", Guid? connection = null)
    {
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection ?? _main, Name = "Acme " + externalId, ExternalCompanyId = "co-" + externalId + (connection ?? _main) };
        var ticket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = connection ?? _main, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
            ExternalTicketId = externalId, ClientCompanyId = company.Id, CorrelationId = Guid.NewGuid(), Title = "Ticket " + externalId,
            RequesterName = "Acme", RequesterEmail = "a@acme.test", SyncStatus = TicketSyncStatus.Synced,
        };
        _db.AddRange(company, ticket);
        await _db.SaveChangesAsync();
        return ticket;
    }

    private UnifiedTimeEntry Entered(string id, string login, decimal hours, string? notes = "Replaced the toner", bool billable = true, DateTimeOffset? at = null)
        => new(id, login, hours, billable, at ?? _clock.GetUtcNow().AddHours(-3), notes) { TechnicianName = "Login " + login, WorkType = "Remote support" };

    private async Task<ProviderWorklogs.Outcome> ReadAsync(Ticket ticket, params UnifiedTimeEntry[] entries)
    {
        var outcome = await ProviderWorklogs.ReconcileAsync(_db, await _db.Tickets.SingleAsync(t => t.Id == ticket.Id), entries);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return outcome;
    }

    private Task<List<TicketTimeEntry>> WorklogsAsync(Ticket ticket)
        => _db.TicketTimeEntries.AsNoTracking().Where(e => e.TicketId == ticket.Id).OrderBy(e => e.ExternalEntryId).ToListAsync();

    // ------------------------------------------------------------------ what is kept

    [Fact]
    public async Task An_entry_made_in_the_PSA_becomes_a_worklog_with_everything_as_the_PSA_has_it()
    {
        var ticket = await TicketAsync();
        var at = new DateTimeOffset(2026, 10, 6, 14, 30, 0, TimeSpan.FromHours(5.5));

        var outcome = await ReadAsync(ticket,
            new UnifiedTimeEntry("te-9001", " 41 ", 1.75m, false, at, "Swapped the drum") { TechnicianName = "Asha R", WorkType = "On-site", InternalNotes = "Charge the callout next time" });

        outcome.Should().Be(new ProviderWorklogs.Outcome(1, 0, 0));
        var worklog = (await WorklogsAsync(ticket)).Should().ContainSingle().Subject;
        worklog.Should().BeEquivalentTo(new
        {
            Source = TimeEntrySource.Provider, PsaConnectionId = (Guid?)_main, ExternalEntryId = "te-9001", MspOrganizationId = Org,
            TechnicianExternalId = "41", TechnicianName = "Asha R", Hours = 1.75m, Billable = false, WorkTypeLabel = "On-site",
            SyncStatus = TimeEntrySyncStatus.Synced, AppUserId = (Guid?)null, NoteId = (Guid?)null, WorkSessionId = (Guid?)null,
        }, "it is the PSA's entry: its source, its connection, its id, its login, its hours and its kind of work, and nobody here logged it");
        worklog.EntryDate.Should().Be(at, "the moment it was worked is the PSA's, not the moment it was read");
        worklog.Notes.Should().Contain("Swapped the drum").And.Contain("Charge the callout next time");
    }

    [Fact]
    public async Task One_entry_is_one_worklog_however_often_it_is_read()
    {
        var ticket = await TicketAsync();
        var entries = new[] { Entered("1", "41", 1m), Entered("2", "77", 0.5m) };

        (await ReadAsync(ticket, entries)).Should().Be(new ProviderWorklogs.Outcome(2, 0, 0));
        var first = await WorklogsAsync(ticket);
        // Polling again, a webhook for the same ticket, a run retried after a failure: the same read.
        for (var i = 0; i < 3; i++)
            (await ReadAsync(ticket, entries)).Should().Be(new ProviderWorklogs.Outcome(0, 0, 0));
        // The PSA lists an entry twice on one page: still one.
        (await ReadAsync(ticket, [.. entries, entries[0]])).Changed.Should().BeFalse();

        var after = await WorklogsAsync(ticket);
        after.Select(w => (w.Id, w.ExternalEntryId, w.Hours, w.UpdatedAt)).Should().Equal(first.Select(w => (w.Id, w.ExternalEntryId, w.Hours, w.UpdatedAt)),
            "nothing was added and nothing was rewritten");
    }

    [Fact]
    public async Task The_database_refuses_a_second_row_for_one_entry_of_one_account_and_allows_the_same_id_on_another()
    {
        var (here, alsoHere, elsewhere) = (await TicketAsync("500"), await TicketAsync("501"), await TicketAsync("500", _other));
        await ReadAsync(here, Entered("7", "41", 1m));

        // Two reads racing, each believing the entry new: the second save is refused.
        _db.TicketTimeEntries.Add(new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = alsoHere.Id, PsaConnectionId = _main, ExternalEntryId = "7", Hours = 1m,
            Source = TimeEntrySource.Provider, SyncStatus = TimeEntrySyncStatus.Synced, EntryDate = _clock.GetUtcNow(),
        });
        await FluentActions.Awaiting(() => _db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();

        // The second account numbers its own entries: its entry 7 is another entry.
        (await ReadAsync(elsewhere, Entered("7", "41", 2m))).Added.Should().Be(1);
        (await _db.TicketTimeEntries.AsNoTracking().CountAsync(e => e.ExternalEntryId == "7")).Should().Be(2);
        // Time on a ticket with no PSA has no PSA id, and any number of those may be held.
        var board = new Ticket { MspOrganizationId = Org, Origin = TicketOrigin.Internal, Title = "Board work", RequesterName = "x", RequesterEmail = "x@x.test", CorrelationId = Guid.NewGuid() };
        _db.Add(board);
        _db.TicketTimeEntries.AddRange(Enumerable.Range(0, 3).Select(_ => new TicketTimeEntry { MspOrganizationId = Org, TicketId = board.Id, Hours = 1m, EntryDate = _clock.GetUtcNow() }));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_entry_logged_from_the_portal_and_read_back_is_the_portals_own_and_is_not_copied()
    {
        var ticket = await TicketAsync();
        // Logged here by Asha an hour ago, on a reply, and pushed out: the PSA filed it under the account the portal writes as.
        var note = Guid.NewGuid();
        var logged = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, PsaConnectionId = _main, ExternalEntryId = "8001", Hours = 0.6167m, Billable = true,
            Notes = "As I typed it", TechnicianExternalId = "900", AppUserId = _asha, NoteId = note, Source = TimeEntrySource.Portal,
            SyncStatus = TimeEntrySyncStatus.Synced, EntryDate = _clock.GetUtcNow().AddHours(-1),
        };
        _db.TicketTimeEntries.Add(logged);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // The PSA sends it back, rounded its own way and under its own wording, beside one made in the PSA.
        var outcome = await ReadAsync(ticket, Entered("8001", "900", 0.75m, notes: "As the PSA holds it"), Entered("8002", "41", 2m));

        outcome.Should().Be(new ProviderWorklogs.Outcome(1, 0, 0), "one new worklog: the one made in the PSA");
        var held = await WorklogsAsync(ticket);
        held.Should().HaveCount(2);
        held.Single(w => w.ExternalEntryId == "8001").Should().BeEquivalentTo(new
        {
            logged.Id, Source = TimeEntrySource.Portal, AppUserId = (Guid?)_asha, NoteId = (Guid?)note, Hours = 0.6167m, Notes = "As I typed it", TechnicianExternalId = "900",
        }, "who logged it, the reply it came with and the time on the clock are the portal's record of its own entry");
        held.Single(w => w.ExternalEntryId == "8002").Source.Should().Be(TimeEntrySource.Provider);
        held.Sum(w => w.Hours).Should().Be(2.6167m, "an hour is counted once");
    }

    [Fact]
    public async Task A_worklog_follows_what_the_PSA_does_to_its_entry_and_a_deleted_one_goes()
    {
        var ticket = await TicketAsync();
        var portals = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, PsaConnectionId = _main, ExternalEntryId = "8001", Hours = 1m, AppUserId = _asha,
            Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced, EntryDate = _clock.GetUtcNow(),
        };
        _db.TicketTimeEntries.Add(portals);
        await _db.SaveChangesAsync();
        await ReadAsync(ticket, Entered("1", "41", 1m), Entered("2", "41", 2m), Entered("8001", "900", 1m));
        var id = (await WorklogsAsync(ticket)).Single(w => w.ExternalEntryId == "1").Id;

        // Entry 1 corrected in the PSA and moved to another login; entry 2 deleted there; and the PSA no longer lists the portal's.
        var moved = _clock.GetUtcNow().AddDays(-1);
        var outcome = await ReadAsync(ticket, Entered("1", "77", 1.5m, notes: "Corrected", billable: false, at: moved));

        outcome.Should().Be(new ProviderWorklogs.Outcome(0, 1, 1));
        var held = await WorklogsAsync(ticket);
        held.Select(w => w.ExternalEntryId).Should().Equal("1", "8001");
        held[0].Should().BeEquivalentTo(new { Id = id, Hours = 1.5m, Billable = false, TechnicianExternalId = "77", EntryDate = moved, Notes = "Corrected" },
            "the same worklog, as the PSA holds the entry now");
        held[1].Id.Should().Be(portals.Id, "an entry logged here is the portal's to account for; it is not removed because a read did not list it");
    }

    [Fact]
    public async Task Nothing_read_from_the_PSA_is_ever_sent_to_it()
    {
        var ticket = await TicketAsync();
        await ReadAsync(ticket, Entered("1", "41", 1m));
        var worklog = await _db.TicketTimeEntries.SingleAsync(e => e.TicketId == ticket.Id);
        var writer = new TicketTimeWriter(_db, null!, null);

        await FluentActions.Awaiting(() => writer.PushAsync(worklog, ticket, _psa, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*never sent to it*");

        _psa.PushedTime.Should().BeEmpty();
        _psa.TimeEntryLookups.Should().BeEmpty("it was not even asked whether it has it");
        (await WorklogsAsync(ticket)).Single().SyncStatus.Should().Be(TimeEntrySyncStatus.Synced, "and nothing waits to be sent: the pusher looks only for what is not yet in the PSA");
    }

    // ------------------------------------------------------------------ through a sync run

    private ConnectionSyncRunner Runner(DeskDbContext db)
        => new(db, new Resolver(_psa), new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, _clock), _clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), _clock), new HeuristicMalwareScanner(), _clock);

    private sealed class Resolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private async Task<SyncRunResult> RunAsync()
    {
        _db.ChangeTracker.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        return await Runner(_db).RunAsync(_main, new SyncRunRequest(Full: true));
    }

    [Fact]
    public async Task A_sync_keeps_the_PSAs_time_as_worklogs_beside_the_totals_and_a_second_run_adds_none()
    {
        _psa.Tickets.Add(new UnifiedTicket
        {
            ExternalId = "500", Title = "Printer offline", Status = "New", Priority = "Medium", RequesterExternalId = "co-1", CompanyName = "Acme",
            ModifiedAt = _clock.GetUtcNow().AddDays(-1),
        });
        _psa.TimeEntries["500"] = [Entered("1", "41", 1.5m), Entered("2", "77", 0.5m, billable: false)];

        (await RunAsync()).Failed.Should().Be(0);

        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.ExternalTicketId == "500");
        (ticket.TimeWorkedHours, ticket.BillableHours, ticket.NonBillableHours).Should().Be((2m, 1.5m, 0.5m));
        var worklogs = await WorklogsAsync(ticket);
        worklogs.Select(w => (w.ExternalEntryId, w.Hours, w.Source, w.PsaConnectionId)).Should().Equal(
            ("1", 1.5m, TimeEntrySource.Provider, (Guid?)_main), ("2", 0.5m, TimeEntrySource.Provider, (Guid?)_main));
        worklogs.Sum(w => w.Hours).Should().Be(ticket.TimeWorkedHours, "the worklogs and the totals are one account of the same hours");
        var reads = _psa.TimeReads;

        (await RunAsync()).Failed.Should().Be(0);
        (await WorklogsAsync(ticket)).Select(w => w.Id).Should().Equal(worklogs.Select(w => w.Id), "read again, they are the same worklogs");
        (_psa.TimeReads - reads).Should().Be(1, "and the PSA was asked for the ticket's time once, as before there were worklogs");
        _psa.PushedTime.Should().BeEmpty("no run sends a worklog back");
    }

    [Fact]
    public async Task A_read_of_time_that_failed_removes_nothing()
    {
        _psa.Tickets.Add(new UnifiedTicket
        {
            ExternalId = "500", Title = "Printer offline", Status = "New", Priority = "Medium", RequesterExternalId = "co-1", CompanyName = "Acme",
            ModifiedAt = _clock.GetUtcNow().AddDays(-1),
        });
        _psa.TimeEntries["500"] = [Entered("1", "41", 1.5m)];
        await RunAsync();

        // The PSA stops answering for time. That is not the same as the ticket having none.
        _psa.TimeReadFailureFor["500"] = new ConnectorException(ConnectorFailureKind.Timeout, "The PSA did not answer.");
        await RunAsync();

        var ticket = await _db.Tickets.AsNoTracking().SingleAsync(t => t.ExternalTicketId == "500");
        (await WorklogsAsync(ticket)).Should().ContainSingle().Which.Hours.Should().Be(1.5m);
        ticket.TimeWorkedHours.Should().Be(1.5m);

        // It answers again, and the entry really has been deleted there.
        _psa.TimeReadFailureFor.Clear();
        _psa.TimeEntries["500"] = [];
        await RunAsync();
        (await WorklogsAsync(ticket)).Should().BeEmpty();
        (await _db.Tickets.AsNoTracking().SingleAsync(t => t.ExternalTicketId == "500")).TimeWorkedHours.Should().Be(0m);
    }

    // ------------------------------------------------------------------ whose time it is

    [Fact]
    public async Task Time_entered_in_the_PSA_is_the_persons_whose_login_it_is_on_that_account_and_nobody_elses()
    {
        var (ticket, elsewhere) = (await TicketAsync("500"), await TicketAsync("500", _other));
        var portals = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, PsaConnectionId = _main, ExternalEntryId = "8001", Hours = 1m, AppUserId = _asha, TechnicianExternalId = "900",
            Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced, EntryDate = _clock.GetUtcNow().AddHours(-2),
        };
        _db.TicketTimeEntries.Add(portals);
        await _db.SaveChangesAsync();
        await ReadAsync(ticket,
            Entered("1", "41", 2m),                 // Asha's login on this account
            Entered("2", "77", 4m),                 // a login linked to nobody
            Entered("3", "900", 8m),                // the account the portal writes as, with no portal entry behind it
            Entered("8001", "900", 1m),             // the portal's own, read back
            Entered("4", "41", 16m, at: _clock.GetUtcNow().AddDays(-30)));   // Asha's, a month ago
        await ReadAsync(elsewhere, Entered("1", "41", 32m));   // login 41 on the other account is somebody else

        var (from, before) = (_clock.GetUtcNow().AddDays(-7), _clock.GetUtcNow().AddDays(1));
        var hers = await PsaLoggedTime.ForAsync(_db, [_asha, _ben], from, before);

        hers.Select(x => (x.AppUserId, x.Entry.ExternalEntryId, x.Entry.Hours)).Should().Equal((_asha, "1", 2m));
        // With what she logged in the portal, an hour is counted once: 1 logged here and 2 entered in the PSA.
        var logged = await _db.TicketTimeEntries.AsNoTracking().Where(e => e.AppUserId == _asha).SumAsync(e => (double)e.Hours);
        ((decimal)logged + hers.Sum(x => x.Entry.Hours)).Should().Be(3m);
        (await PsaLoggedTime.ForAsync(_db, [_ben], from, before)).Should().BeEmpty("Ben has no login on any account");
        (await PsaLoggedTime.ForAsync(_db, [], from, before)).Should().BeEmpty();

        // Login 77 is linked to Ben afterwards: the time already entered under it is his from then on, without a row being touched.
        _db.UserPsaIdentities.Add(new UserPsaIdentity { MspOrganizationId = Org, AppUserId = _ben, PsaConnectionId = _main, ExternalTechnicianId = "77" });
        await _db.SaveChangesAsync();
        (await PsaLoggedTime.ForAsync(_db, [_ben], from, before)).Select(x => (x.AppUserId, x.Entry.Hours)).Should().Equal((_ben, 4m));
        (await _db.TicketTimeEntries.AsNoTracking().CountAsync(e => e.Source == TimeEntrySource.Provider && e.AppUserId != null)).Should().Be(0, "whose it is, is not stored on the worklog");

        // Linking somebody to the account's own login gives them nothing: it is the whole portal's writing.
        var cara = new AppUser { MspOrganizationId = Org, DisplayName = "Cara Lind", Email = "cara@techpio.test", IsActive = true };
        _db.AddRange(cara, new UserPsaIdentity { MspOrganizationId = Org, AppUserId = cara.Id, PsaConnectionId = _main, ExternalTechnicianId = "900" });
        await _db.SaveChangesAsync();
        (await PsaLoggedTime.ForAsync(_db, [cara.Id], from, before)).Should().BeEmpty("the eight hours under the account's login are nobody's");
        (await PsaLoggedTime.ForAsync(_db, [_asha, _ben, cara.Id], from, before)).Sum(x => x.Entry.Hours).Should().Be(6m);
    }
}
