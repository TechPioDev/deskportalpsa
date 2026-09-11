using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Application.Attachments;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Inbound conversation sync: provider notes must reach the portal thread exactly once, keep their
/// real author, and preserve the provider's visibility flag — internal notes ARE stored (staff see
/// the whole thread) but marked IsPublic=false, and the client read path filters them at read time.
/// </summary>
public class NoteImportTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static async Task<DeskDbContext> SeedAsync(string dbName, bool importNotes = true, bool importSystemNotes = false)
    {
        var db = TestDbContextFactory.ForPlatform(dbName);
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
            ImportNotes = importNotes, ImportSystemNotes = importSystemNotes,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static ConnectionSyncRunner Runner(DeskDbContext db, StubConnector connector, TestClock clock)
        => new(db, new FakeResolver(connector),
            new TicketSyncService(db, new MappingEngine(), new SyncEventStore(db, clock), clock, new RecordingActivity()),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), clock), new HeuristicMalwareScanner(), clock);

    private static UnifiedTicket Incoming(string extId) => new()
    {
        ExternalId = extId, Title = "Network issue", Status = "1", Priority = "1",
        RequesterExternalId = "176", RequesterName = "Acme", RequesterEmail = "a@acme.test",
    };

    private static UnifiedTicketNote Note(string id, string author, string body, DateTimeOffset at) =>
        new(id, author, body, IsPublic: true, at);

    [Fact]
    public async Task An_internal_provider_note_is_stored_marked_internal_and_hidden_from_clients_but_not_staff()
    {
        // The live CWM finding this pins: a technician's rich internal note in ConnectWise never
        // appeared in the portal, because the connector used to drop internal notes before sync.
        // Now it must arrive with IsPublic=false, show for STAFF, and stay invisible to the CLIENT.
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedAsync(dbName);
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] =
        [
            Note("101", "Jane Tech", "Public reply.", clock.GetUtcNow()),
            new UnifiedTicketNote("102", "Jane Tech", "Internal analysis: password was expired.", IsPublic: false, clock.GetUtcNow().AddMinutes(1)),
        ];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var stored = await db.TicketNotes.OrderBy(n => n.NoteCreatedAt).ToListAsync();
        stored.Should().HaveCount(2);
        stored[1].IsPublic.Should().BeFalse("the provider's internal flag must survive the sync");

        // Read-time enforcement, both directions — not just the direction that happens to pass.
        var ticket = await db.Tickets.SingleAsync();
        var company = await db.ClientCompanies.SingleAsync();
        // Staff resolution needs a linked AppUser id — a caller without one sees nothing by design.
        var reads = new Desk.Infrastructure.Tickets.TicketReadService(db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));

        var staff = await reads.GetDetailForStaffAsync(ticket.Id);
        staff!.Conversation.Should().HaveCount(2, "staff see the whole thread");
        staff.Conversation.Should().ContainSingle(n => !n.IsPublic && n.Body.StartsWith("Internal analysis"));

        var client = await reads.GetDetailAsync(
            new Desk.Application.Tickets.ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true), ticket.Id);
        client!.Conversation.Should().ContainSingle("clients receive only the public note")
            .Which.Body.Should().Be("Public reply.");
    }

    [Fact]
    public async Task A_time_entrys_notes_join_the_thread_as_internal_unless_the_portal_logged_them()
    {
        // The second half of the live CWM finding: the technician's rich note was written through a
        // TIME ENTRY, and the ticket-notes API never returns those — ConnectWise's "All notes" view
        // is ticket notes PLUS time-entry notes. The provider-authored one must join the thread as
        // internal; the portal-logged one must NOT, because its text already sits in the thread as
        // the reply that logged it.
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedAsync(dbName);
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] = [Note("101", "Jane Tech", "Public reply.", clock.GetUtcNow())];
        connector.TimeEntries["7809"] =
        [
            new UnifiedTimeEntry("500", "tech-1", 0.17m, false, clock.GetUtcNow(), "1. Like\n2. Dislike\nWindows Admin Center is a free tool.") { TechnicianName = "Sarabjit Singh" },
            new UnifiedTimeEntry("501", "tech-1", 0.25m, true, clock.GetUtcNow(), "Reply text the portal already holds.") { TechnicianName = "Sarabjit Singh" },
        ];

        // First run creates the ticket; then stamp entry 501 as portal-origin, as logging from the
        // reply composer does, and re-run.
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var ticket = await db.Tickets.SingleAsync();
        db.TicketTimeEntries.Add(new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, Hours = 0.25m, Billable = true,
            Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced,
            ExternalEntryId = "501", EntryDate = clock.GetUtcNow(),
        });
        // Remove the echo imported before the portal stamp existed, then prove it STAYS gone.
        db.TicketNotes.RemoveRange(db.TicketNotes.Where(n => n.ExternalNoteId == "te-501"));
        await db.SaveChangesAsync();

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var teNote = await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "te-500");
        teNote.IsPublic.Should().BeFalse("a time entry's notes are internal by default");
        teNote.AuthorName.Should().Be("Sarabjit Singh");
        teNote.Body.Should().StartWith("1. Like", "the FULL text must arrive, not a truncation");
        (await db.TicketNotes.AnyAsync(n => n.ExternalNoteId == "te-501"))
            .Should().BeFalse("the portal-logged entry's text is already in the thread as the reply");

        // And the staff detail names the entry the note came from, so the UI can pair them.
        var reads = new Desk.Infrastructure.Tickets.TicketReadService(
            db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));
        var staff = await reads.GetDetailForStaffAsync(ticket.Id);
        staff!.Conversation.Single(n => n.Body.StartsWith("1. Like")).TimeEntryExternalId.Should().Be("500");
    }

    [Fact]
    public async Task A_customer_contacts_note_imports_on_the_client_side_and_stays_reconcilable()
    {
        // The live finding this pins: EVERY imported note rendered as the MSP's own words, because
        // the unified model discarded the provider's member-vs-contact distinction. A contact's
        // note must land AuthoredByClient — and, since AuthoredByClient used to double as the
        // "provider-origin" marker for deletion reconciliation, it must STILL be removable when
        // deleted in the PSA. Both halves, or the thread either loses its sides or grows ghosts.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] =
        [
            Note("1", "Jane Tech", "Working on it.", clock.GetUtcNow()),
            new UnifiedTicketNote("2", "Ravi Customer", "Still broken on my side.", IsPublic: true,
                clock.GetUtcNow().AddMinutes(1), FromClient: true),
        ];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var contactNote = await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "2");
        contactNote.AuthoredByClient.Should().BeTrue("the provider says a customer contact wrote it");
        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "1")).AuthoredByClient.Should().BeFalse();

        // Healing: notes imported BEFORE FromClient existed were all stored staff-side. A re-sync
        // must put them back on their real side — provider-imported rows only.
        contactNote.AuthoredByClient = false;
        await db.SaveChangesAsync();
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "2")).AuthoredByClient
            .Should().BeTrue("a re-sync heals the side recorded before the fix");

        // Deleted in the PSA → gone here too, client-authored or not.
        connector.Notes["7809"] = [Note("1", "Jane Tech", "Working on it.", clock.GetUtcNow())];
        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);
        run.NotesRemoved.Should().Be(1);
        (await db.TicketNotes.AnyAsync(n => n.ExternalNoteId == "2")).Should().BeFalse();
    }

    [Fact]
    public async Task A_reply_that_logged_time_carries_its_hours_for_staff_but_never_for_the_client()
    {
        // "Reply + 0.5h" in one send: the PSA keeps the note and the entry as unrelated records,
        // so the link lives here (TicketTimeEntry.NoteId) and the STAFF thread states the hours on
        // the reply itself. The CLIENT path must stay silent about time — hours are billing data,
        // and clients don't reach the time panel either.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var ticket = await db.Tickets.SingleAsync();

        var note = new TicketNote
        {
            MspOrganizationId = Org, TicketId = ticket.Id, ExternalNoteId = "801",
            AuthorName = "Jane Tech", AuthoredByClient = false, Body = "Patched the server.",
            IsPublic = true, NoteCreatedAt = clock.GetUtcNow(),
        };
        db.TicketNotes.Add(note);
        db.TicketTimeEntries.Add(new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, NoteId = note.Id,
            Hours = 0.5m, Billable = true, ExternalEntryId = "600",
            Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced,
            EntryDate = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var reads = new Desk.Infrastructure.Tickets.TicketReadService(
            db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));

        var staffNote = (await reads.GetDetailForStaffAsync(ticket.Id))!
            .Conversation.Single(n => n.Body == "Patched the server.");
        staffNote.TimeEntryExternalId.Should().Be("600");
        staffNote.TimeEntryHours.Should().Be(0.5m);
        staffNote.TimeEntryBillable.Should().Be(true);

        var company = await db.ClientCompanies.SingleAsync();
        var clientNote = (await reads.GetDetailAsync(
                new Desk.Application.Tickets.ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true), ticket.Id))!
            .Conversation.Single(n => n.Body == "Patched the server.");
        clientNote.TimeEntryExternalId.Should().BeNull("hours are billing data — the client thread stays silent about time");
        clientNote.TimeEntryHours.Should().BeNull();
    }

    [Fact]
    public async Task Notification_history_merges_real_events_and_never_leaks_internal_notes()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] =
        [
            Note("1", "Jane Tech", "Public reply.", clock.GetUtcNow().AddMinutes(5)),
            new UnifiedTicketNote("2", "Jane Tech", "Internal analysis.", IsPublic: false, clock.GetUtcNow().AddMinutes(6)),
            new UnifiedTicketNote("3", "Ravi Customer", "Thanks!", IsPublic: true, clock.GetUtcNow().AddMinutes(7), FromClient: true),
        ];
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var company = await db.ClientCompanies.SingleAsync();
        var reads = new Desk.Infrastructure.Tickets.TicketReadService(
            db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));
        var access = new Desk.Application.Tickets.ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true);

        var feed = await reads.ActivityHistoryAsync(access);

        feed.Select(e => e.Kind).Should().Contain(["ticket-created", "staff-reply", "client-reply"]);
        feed.Should().HaveCount(3, "created + two PUBLIC replies — the internal note must NOT appear as an event");
        feed.Should().BeInDescendingOrder(e => e.At);
        feed.First(e => e.Kind == "client-reply").Actor.Should().Be("Ravi Customer");
    }

    [Fact]
    public async Task A_time_entrys_internal_notes_reach_the_thread_with_its_summary()
    {
        // The live finding this pins: a technician wrote "See Internal Notes" as the summary and
        // the actual detail in Autotask's separate internal-notes field. The portal imported the
        // summary alone, so the thread showed a pointer to something it never displayed.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7814"));
        connector.TimeEntries["7814"] =
        [
            new UnifiedTimeEntry("900", "tech-1", 0.25m, true, clock.GetUtcNow(), "See Internal Notes")
                { TechnicianName = "Sudanshu Aggarwal", InternalNotes = "thank basit for help" },
            // Summary blank, detail internal-only — this one used to be skipped altogether.
            new UnifiedTimeEntry("901", "tech-1", 0.5m, true, clock.GetUtcNow().AddMinutes(1), null)
                { TechnicianName = "Sudanshu Aggarwal", InternalNotes = "internal-only detail" },
        ];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var both = await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "te-900");
        both.Body.Should().Be("thank basit for help",
            "the summary was only a signpost, and the destination is now right there");
        both.IsPublic.Should().BeFalse("time-entry notes are internal — a client must never see them");

        var internalOnly = await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "te-901");
        internalOnly.Body.Should().Contain("internal-only detail",
            "an entry whose only text is internal still has something to show");
    }

    [Fact]
    public async Task A_note_already_imported_with_a_narrower_body_is_healed_not_left_stale()
    {
        // Ticket 7814 in production: the entry was imported as "See Internal Notes" BEFORE the
        // import learned to read the internal half. Re-syncing alone cannot fix it — the insert
        // loop skips IDs it already holds — so without a heal the pointer-to-nothing is permanent.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7814"));
        connector.TimeEntries["7814"] =
            [new UnifiedTimeEntry("900", "tech-1", 0.25m, true, clock.GetUtcNow(), "See Internal Notes")
                { TechnicianName = "Sudanshu Aggarwal" }];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "te-900"))
            .Body.Should().NotContain("thank basit for help", "the old import could not see it");

        // Now the reader knows about internal notes, and the same entry comes back whole.
        connector.TimeEntries["7814"] =
            [new UnifiedTimeEntry("900", "tech-1", 0.25m, true, clock.GetUtcNow(), "See Internal Notes")
                { TechnicianName = "Sudanshu Aggarwal", InternalNotes = "thank basit for help" }];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var notes = await db.TicketNotes.Where(n => n.ExternalNoteId == "te-900").ToListAsync();
        notes.Should().ContainSingle("healing rewrites the row — it must not add a second copy");
        notes[0].Body.Should().Contain("thank basit for help");
    }

    [Fact]
    public async Task A_portal_reply_is_never_rewritten_by_the_heal()
    {
        // The heal touches provider-owned rows only. A reply written in the portal is the portal's
        // own record; letting a provider echo overwrite its text would rewrite what a human sent.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var ticket = await db.Tickets.SingleAsync();
        db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticket.Id,
            ExternalNoteId = "29683530",
            AuthorName = "Demo Admin",
            ImportedFromProvider = false, // written here, pushed out
            Body = "What we actually sent.",
            IsPublic = true,
            NoteCreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        connector.Notes["7809"] =
            [Note("29683530", "Demo Admin", "Provider's mangled echo.", clock.GetUtcNow())];
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "29683530"))
            .Body.Should().Be("What we actually sent.");
    }

    [Fact]
    public async Task A_notes_author_is_stored_as_an_id_and_backfilled_on_the_next_read()
    {
        // The name is only what the author is called. Techpio's integration account is called after
        // a real person, so without the id its notes and that person's are indistinguishable.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7814"));
        connector.Notes["7814"] =
        [
            new UnifiedTicketNote("1", "Sudanshu Aggarwal", "Written through the integration.", IsPublic: true,
                clock.GetUtcNow(), AuthorExternalId: "29682885"),
            new UnifiedTicketNote("2", "Ravi Customer", "From the customer.", IsPublic: true,
                clock.GetUtcNow(), FromClient: true),
        ];
        connector.TimeEntries["7814"] =
            [new UnifiedTimeEntry("900", "29682889", 0.5m, true, clock.GetUtcNow(), "Fixed it.") { TechnicianName = "Kamal Arora" }];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "1")).AuthorExternalId.Should().Be("29682885");
        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "2")).AuthorExternalId
            .Should().BeNull("a customer contact is not a resource");
        (await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "te-900")).AuthorExternalId
            .Should().Be("29682889", "a time entry's author is the resource it is filed under");

        // A row imported before the id was kept: the next read fills it in from the provider.
        var old = await db.TicketNotes.SingleAsync(n => n.ExternalNoteId == "1");
        old.AuthorExternalId = null;
        await db.SaveChangesAsync();

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        (await db.TicketNotes.AsNoTracking().SingleAsync(n => n.ExternalNoteId == "1")).AuthorExternalId
            .Should().Be("29682885", "the heal backfills it from the provider's own record");
    }

    [Fact]
    public async Task A_quiet_ticket_the_import_window_no_longer_returns_is_healed_by_a_note_refresh()
    {
        // Production, 11 Sep: a full resync filled in nothing. Both connections import only tickets
        // active in the last 7 days, and a full run still applies that window, so the tickets behind
        // the 53 integration notes - quiet since early September - were never read again.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7814"));
        connector.Notes["7814"] =
            [new UnifiedTicketNote("1", "Sudanshu Aggarwal", "Old note.", IsPublic: false, clock.GetUtcNow(), AuthorExternalId: "29682885")];
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        // As it stood before the id was kept - and the ticket has since gone quiet.
        var note = await db.TicketNotes.SingleAsync();
        note.AuthorExternalId = null;
        await db.SaveChangesAsync();
        connector.Tickets.Clear();

        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        (await db.TicketNotes.AsNoTracking().SingleAsync()).AuthorExternalId
            .Should().BeNull("a sync only reads what its window returns - the reason the refresh exists");

        var read = await Runner(db, connector, clock).RefreshNotesAsync(Conn, ["7814"]);

        read.Should().Be(1);
        (await db.TicketNotes.AsNoTracking().SingleAsync()).AuthorExternalId.Should().Be("29682885");
    }

    [Fact]
    public async Task The_backfill_picks_only_tickets_with_staff_notes_that_should_have_an_author_id()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        foreach (var id in new[] { "A", "B", "C", "D", "E" }) connector.Tickets.Add(Incoming(id));
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var byExt = await db.Tickets.ToDictionaryAsync(t => t.ExternalTicketId!, t => t.Id);

        TicketNote N(string ticket, string author, bool imported = true, bool client = false, string? authorId = null) => new()
        {
            MspOrganizationId = Org, TicketId = byExt[ticket], ExternalNoteId = Guid.NewGuid().ToString("N"),
            AuthorName = author, ImportedFromProvider = imported, AuthoredByClient = client,
            AuthorExternalId = authorId, Body = "b", IsPublic = true, NoteCreatedAt = clock.GetUtcNow(),
        };
        db.TicketNotes.AddRange(
            N("A", "Sudanshu Aggarwal"),                          // staff, imported, no id: needs it
            N("B", "Ravi Customer", client: true),                // a contact: no resource to find
            N("C", "AutotaskPsa automation"),                     // system note: no author at all
            N("D", "Kamal Arora", authorId: "29682889"),          // already has one
            N("E", "Demo Admin", imported: false));               // a portal reply: our own record
        await db.SaveChangesAsync();

        var pending = await NoteAuthorBackfill.PendingAsync(db);

        pending.Should().ContainSingle().Which.Value.Should().Equal("A");
        (await NoteAuthorBackfill.CountAsync(db)).Should().Be(1);
    }

    [Fact]
    public async Task The_integrations_notes_say_so_and_a_real_person_of_the_same_name_keeps_theirs()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        (await db.PsaConnections.SingleAsync()).DefaultTimeEntryResourceId = "29682885";
        await db.SaveChangesAsync();
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7814"));
        connector.Notes["7814"] =
        [
            new UnifiedTicketNote("1", "Sudanshu Aggarwal", "Posted by the integration.", IsPublic: true,
                clock.GetUtcNow(), AuthorExternalId: "29682885"),
            // Same NAME, different resource: a real person - the reason this is decided on ids.
            new UnifiedTicketNote("2", "Sudanshu Aggarwal", "Written by the person.", IsPublic: true,
                clock.GetUtcNow().AddMinutes(1), AuthorExternalId: "29682999"),
        ];
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var ticket = await db.Tickets.SingleAsync();
        var company = await db.ClientCompanies.SingleAsync();
        var reads = new Desk.Infrastructure.Tickets.TicketReadService(
            db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));
        var access = new Desk.Application.Tickets.ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true);

        var staff = (await reads.GetDetailForStaffAsync(ticket.Id))!.Conversation;
        var client = (await reads.GetDetailAsync(access, ticket.Id))!.Conversation;
        foreach (var thread in new[] { staff, client })
        {
            thread.Single(n => n.Body == "Posted by the integration.").AuthorName.Should().Be("AT integration");
            thread.Single(n => n.Body == "Written by the person.").AuthorName.Should().Be("Sudanshu Aggarwal");
        }

        // The client's notification history carries the same byline as the thread.
        (await reads.ActivityHistoryAsync(access))
            .Where(e => e.Kind == "staff-reply").Select(e => e.Actor)
            .Should().BeEquivalentTo("AT integration", "Sudanshu Aggarwal");
    }

    [Fact]
    public async Task Provider_notes_are_imported_once_and_keep_their_author()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedAsync(dbName);
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] =
        [
            Note("29683530", "Jane Tech", "Applied a fix on the switch port.", clock.GetUtcNow()),
            Note("29683533", "Demo Admin", "Confirmed, thank you.", clock.GetUtcNow().AddMinutes(1)),
        ];

        var first = await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var second = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        first.Notes.Should().Be(2);
        second.Notes.Should().Be(0); // re-running must not duplicate the thread
        var notes = await db.TicketNotes.OrderBy(n => n.NoteCreatedAt).ToListAsync();
        notes.Select(n => n.AuthorName).Should().Equal("Jane Tech", "Demo Admin");
        notes.Should().OnlyContain(n => n.IsPublic && !n.AuthoredByClient);
    }

    [Fact]
    public async Task A_reply_written_in_the_portal_is_not_re_imported_as_an_echo()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock();
        await using var db = await SeedAsync(dbName);
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        // Seed the projection the way the portal does after the provider accepts an outbound reply:
        // the note is stored locally already carrying the provider's own note id.
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var ticket = await db.Tickets.SingleAsync();
        db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = Org, TicketId = ticket.Id, ExternalNoteId = "29683533",
            AuthorName = "Demo Admin", AuthoredByClient = true, Body = "Portal reply.",
            IsPublic = true, NoteCreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        connector.Notes["7809"] = [Note("29683533", "Desk Portal", "Portal reply.", clock.GetUtcNow())];

        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.Notes.Should().Be(0);
        var note = await db.TicketNotes.SingleAsync();
        note.AuthoredByClient.Should().BeTrue();   // the client's own byline survives the round trip
        note.AuthorName.Should().Be("Demo Admin");
    }

    [Fact]
    public async Task System_notes_are_skipped_unless_the_connection_asks_for_them()
    {
        var clock = new TestClock();
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] =
        [
            Note("1", "Jane Tech", "Human reply.", clock.GetUtcNow()),
            Note("2", "", "SLA warning raised.", clock.GetUtcNow()), // no author = provider automation
        ];

        await using var off = await SeedAsync(Guid.NewGuid().ToString());
        (await Runner(off, connector, clock).RunAsync(Conn, full: true)).Notes.Should().Be(1);
        (await off.TicketNotes.Select(n => n.Body).ToListAsync()).Should().Equal("Human reply.");

        await using var on = await SeedAsync(Guid.NewGuid().ToString(), importSystemNotes: true);
        (await Runner(on, connector, clock).RunAsync(Conn, full: true)).Notes.Should().Be(2);
        (await on.TicketNotes.Where(n => n.Body == "SLA warning raised.").Select(n => n.AuthorName).SingleAsync())
            .Should().Be("AutotaskPsa automation");
    }

    [Fact]
    public async Task Note_import_is_skipped_entirely_when_the_connection_disables_it()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString(), importNotes: false);
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7809"));
        connector.Notes["7809"] = [Note("1", "Jane Tech", "Should not arrive.", clock.GetUtcNow())];

        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.Notes.Should().Be(0);
        connector.NoteReads.Should().Be(0); // and no wasted provider call
        (await db.TicketNotes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Time_logged_provider_side_is_reflected_in_the_tickets_stored_totals()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7810"));
        connector.TimeEntries["7810"] =
        [
            new UnifiedTimeEntry("1", "20", 0.5m, true, clock.GetUtcNow(), "billable work"),
            new UnifiedTimeEntry("2", "20", 0.25m, false, clock.GetUtcNow(), "non-billable work"),
        ];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        var ticket = await db.Tickets.SingleAsync();
        ticket.TimeWorkedHours.Should().Be(0.75m);
        ticket.BillableHours.Should().Be(0.5m);
        ticket.NonBillableHours.Should().Be(0.25m);
    }

    [Fact]
    public async Task Time_totals_refresh_even_when_the_ticket_itself_is_unchanged()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7810"));

        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        // Time is added provider-side. That bumps the provider's activity date, so the ticket comes
        // back on the next page, but none of its own fields changed — the upsert reports unchanged.
        connector.TimeEntries["7810"] = [new UnifiedTimeEntry("1", "20", 1.25m, true, clock.GetUtcNow(), "tech work")];

        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.Updated.Should().Be(0); // proves the ticket really was seen as unchanged
        (await db.Tickets.SingleAsync()).TimeWorkedHours.Should().Be(1.25m);
    }

    [Fact]
    public async Task A_tickets_time_is_read_once_per_run_not_once_per_consumer()
    {
        // Two things in the same loop iteration want a ticket's time entries: the time-entry notes
        // and the worked/billable totals. Each used to fetch them separately, so a full sync issued
        // exactly two identical reads per ticket — 270 requests for 135 tickets, the single largest
        // call volume in the run, and invisible because both answers were correct.
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { SupportsTimeEntries = true };
        connector.Tickets.Add(Incoming("7810"));
        connector.TimeEntries["7810"] =
            [new UnifiedTimeEntry("1", "20", 1.25m, true, clock.GetUtcNow(), "tech work")
                { TechnicianName = "Tech One" }];

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        connector.TimeReads.Should().Be(1, "both consumers share one snapshot of the same moment");
        // And the shared read still feeds both: the note arrived AND the totals were rewritten.
        (await db.TicketNotes.CountAsync(n => n.ExternalNoteId == "te-1")).Should().Be(1);
        (await db.Tickets.SingleAsync()).TimeWorkedHours.Should().Be(1.25m);
    }

    [Fact]
    public async Task Time_totals_are_left_alone_when_the_provider_has_no_time_support()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector(); // SupportsTimeEntries defaults to false
        connector.Tickets.Add(Incoming("7810"));

        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        // No wasted call, and no zeroing of totals a different source may own.
        connector.TimeReads.Should().Be(0);
        (await db.Tickets.SingleAsync()).TimeWorkedHours.Should().Be(0m); // untouched default
    }

    [Fact]
    public async Task A_note_deleted_in_the_provider_is_removed_from_the_thread()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7810"));
        connector.Notes["7810"] =
        [
            Note("1", "Jane Tech", "Kept.", clock.GetUtcNow()),
            Note("2", "Jane Tech", "Posted in error.", clock.GetUtcNow()),
        ];
        (await Runner(db, connector, clock).RunAsync(Conn, full: true)).Notes.Should().Be(2);

        connector.Notes["7810"] = [Note("1", "Jane Tech", "Kept.", clock.GetUtcNow())];
        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.NotesRemoved.Should().Be(1);
        (await db.TicketNotes.Select(n => n.Body).ToListAsync()).Should().Equal("Kept.");
    }

    [Fact]
    public async Task A_reply_written_in_the_portal_is_never_removed_by_reconciliation()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7810"));
        await Runner(db, connector, clock).RunAsync(Conn, full: true);
        var ticket = await db.Tickets.SingleAsync();

        // Written here and pushed out, so it carries a provider id — but the portal is its origin.
        db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = Org, TicketId = ticket.Id, ExternalNoteId = "500",
            AuthorName = "Demo Admin", AuthoredByClient = true, Body = "My original reply.",
            IsPublic = true, NoteCreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        // A technician deleted the PSA's copy. The customer's own message must survive.
        connector.Notes["7810"] = [];
        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.NotesRemoved.Should().Be(0);
        (await db.TicketNotes.SingleAsync()).Body.Should().Be("My original reply.");
    }

    [Fact]
    public async Task A_ticket_whose_notes_could_not_be_read_keeps_its_thread()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7810"));
        connector.Notes["7810"] = [Note("1", "Jane Tech", "Still valid.", clock.GetUtcNow())];
        await Runner(db, connector, clock).RunAsync(Conn, full: true);

        // An unknown list is not an empty list: a rate-limited read must not wipe the conversation.
        connector.NoteReadFailure = new ConnectorException(ConnectorFailureKind.RateLimited, "429");
        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.NotesRemoved.Should().Be(0);
        (await db.TicketNotes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_note_filtered_out_by_the_system_note_setting_is_not_mistaken_for_a_deletion()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString(), importSystemNotes: true);
        var connector = new StubConnector();
        connector.Tickets.Add(Incoming("7810"));
        connector.Notes["7810"] =
        [
            Note("1", "Jane Tech", "Human reply.", clock.GetUtcNow()),
            Note("2", "", "SLA warning raised.", clock.GetUtcNow()),
        ];
        (await Runner(db, connector, clock).RunAsync(Conn, full: true)).Notes.Should().Be(2);

        // The connection now excludes system notes, but the SLA note is still THERE provider-side.
        // Were the comparison built from the FILTERED list rather than everything the provider
        // returned, this run would delete a note that was never actually removed.
        var connection = await db.PsaConnections.SingleAsync();
        connection.ImportSystemNotes = false;
        await db.SaveChangesAsync();

        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.NotesRemoved.Should().Be(0);
        (await db.TicketNotes.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_failing_note_read_does_not_fail_the_ticket_sync()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync(Guid.NewGuid().ToString());
        var connector = new StubConnector { NoteReadFailure = new ConnectorException(ConnectorFailureKind.RateLimited, "429") };
        connector.Tickets.Add(Incoming("7809"));

        var run = await Runner(db, connector, clock).RunAsync(Conn, full: true);

        run.Created.Should().Be(1);
        run.Notes.Should().Be(0);
    }
}
