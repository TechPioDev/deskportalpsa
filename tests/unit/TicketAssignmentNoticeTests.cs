using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Notifications;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The person a ticket is given to is told so by e-mail.
///
/// What these hold: a ticket raised for somebody else mails that person once, naming who gave it
/// and which ticket, and quotes nothing but its title; one passed on later mails the person it went
/// to; nobody is told that they took a ticket themselves; a ticket that changed hands again before
/// the turn mails only the person it rests with; going back to the board, a finished ticket, one
/// that no longer rests with them and a person who has left mail nobody and hold nothing up; an assignment a moment old waits a turn;
/// nothing is said twice; a mail failure does not repeat for ever; a device is told for a ticket
/// assigned as it was raised (the case the notification scan cannot see), is not told twice for
/// one that changes hands later, and stays silent where its owner switched that off; the people
/// kept informed are still told separately; and another organization's assignments are not
/// touched. On a SQL translator.
/// </summary>
public sealed class TicketAssignmentNoticeTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly RecordingSender _mail = new();
    private readonly PortalOptions _portal = new() { PublicUrl = "https://portal.example" };
    private readonly Guid _harpal, _gurpreet, _basit, _sarabjit, _board;

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Exception? Fail { get; set; }
        public Task<EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(new EmailSenderStatus(true, "desk@techpio.test", "organization"));
        public Task SendAsync(Guid organizationId, EmailMessage message, CancellationToken ct = default)
        {
            if (Fail is not null) throw Fail;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    public TicketAssignmentNoticeTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        AppUser Person(string name) => new() { MspOrganizationId = Org, DisplayName = name, Email = $"{name.Split(' ')[0].ToLowerInvariant()}@techpio.test", IsActive = true };
        var (harpal, gurpreet, basit, sarabjit) = (Person("Harpal Singh"), Person("Gurpreet Singh"), Person("Basit Lone"), Person("Sarabjit Singh"));
        var board = new Board { MspOrganizationId = Org, Name = "TechPio", Key = "TECPIO", Kind = BoardKind.Internal };
        (_harpal, _gurpreet, _basit, _sarabjit, _board) = (harpal.Id, gurpreet.Id, basit.Id, sarabjit.Id, board.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "Asia/Kolkata" }, harpal, gurpreet, basit, sarabjit, board);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private InternalTicketService Tickets => new(_db, _tenant, _clock, new RecordingActivity());
    private TemplateMailer Mailer => new(_db, _mail, _portal, _clock, NullLogger<TemplateMailer>.Instance);
    private TicketAssignmentNotifier Notifier => new(_db, Mailer, _portal, _clock, NullLogger<TicketAssignmentNotifier>.Instance);

    private async Task<Guid> RaiseAsync(Guid by, Guid? assignee = null, params Guid[] keepInformed)
    {
        var id = (await Tickets.CreateAsync(by, new InternalTicketInput(_board, "Set up the new firewall", "The admin password is 4471-secret.",
            AssignedAppUserId: assignee, FollowerUserIds: keepInformed))).TicketId;
        _db.ChangeTracker.Clear();
        return id;
    }

    /// <summary>What the assignment endpoint does: the ticket changes hands and the handover is written down.</summary>
    private async Task HandAsync(Guid ticketId, Guid? to, Guid by, string? note = null)
    {
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == ticketId);
        var previous = ticket.AssignedAppUserId;
        ticket.AssignedAppUserId = to;
        ticket.AssignedByUserId = by;
        _db.TicketAssignments.Add(new TicketAssignment { MspOrganizationId = Org, TicketId = ticketId, FromAppUserId = previous, ToAppUserId = to, AssignedByUserId = by, Note = note });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<int> TurnAsync(int secondsLater = 120)
    {
        _clock.Advance(TimeSpan.FromSeconds(secondsLater));
        var sent = await Notifier.RunAsync();
        _db.ChangeTracker.Clear();
        return sent;
    }

    private List<TicketAssignment> Handovers(Guid ticketId) => _db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToList();

    private async Task DeviceAsync(Guid person)
    {
        _db.PushSubscriptions.Add(new PushSubscription { MspOrganizationId = Org, AppUserId = person, Endpoint = $"https://push.example/{person:N}", P256dh = "k", Auth = "a" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // ---- being told ---------------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_raised_for_somebody_else_mails_that_person_once_and_names_who_gave_it()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);

        (await TurnAsync()).Should().Be(1);
        var mail = _mail.Sent.Single();
        mail.To.Should().Equal("gurpreet@techpio.test");
        mail.Subject.Should().Be("Harpal Singh assigned TECPIO-000001 to you");
        mail.TextBody.Should().Contain("Hi Gurpreet,").And.Contain("\"Set up the new firewall\"").And.Contain("It is yours to work on now")
            .And.Contain($"https://portal.example/dashboard/tickets/{id}");
        mail.TextBody.Should().NotContain("4471-secret", "what the ticket says stays on the ticket");
        mail.HtmlBody.Should().NotContain("4471-secret");

        // Nothing more to say, turn after turn.
        (await TurnAsync()).Should().Be(0);
        (await TurnAsync()).Should().Be(0);
        _mail.Sent.Should().HaveCount(1);
        Handovers(id).Single().NotifiedAt.Should().NotBeNull();

        var log = await _db.EmailLog.AsNoTracking().SingleAsync();
        (log.TemplateKey, log.Succeeded, log.SubjectId).Should().Be((EmailTemplates.TicketAssigned, true, (Guid?)id));
    }

    [Fact]
    public async Task A_ticket_passed_on_later_mails_the_person_it_went_to()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);
        await TurnAsync();
        _mail.Sent.Clear();

        await HandAsync(id, _basit, by: _gurpreet, note: "Needs a ladder, I am off at 6. The safe code is 9917.");

        (await TurnAsync()).Should().Be(1);
        var mail = _mail.Sent.Single();
        mail.To.Should().Equal("basit@techpio.test");
        mail.Subject.Should().Be("Gurpreet Singh assigned TECPIO-000001 to you");
        mail.TextBody.Should().NotContain("9917", "the handover note is read on the ticket, by someone who may open it");
    }

    [Fact]
    public async Task Nobody_is_told_that_they_took_a_ticket_themselves()
    {
        var mine = await RaiseAsync(_harpal, assignee: _harpal);
        var taken = await RaiseAsync(_harpal);
        await HandAsync(taken, _gurpreet, by: _gurpreet);

        (await TurnAsync()).Should().Be(0);
        _mail.Sent.Should().BeEmpty();
        Handovers(mine).Concat(Handovers(taken)).Should().OnlyContain(a => a.NotifiedAt != null, "the question is settled, so it is not asked again");
    }

    [Fact]
    public async Task A_ticket_that_changed_hands_again_before_the_turn_mails_only_the_person_it_rests_with()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);
        _clock.Advance(TimeSpan.FromSeconds(20));
        await HandAsync(id, _basit, by: _harpal);
        _clock.Advance(TimeSpan.FromSeconds(20));
        await HandAsync(id, _sarabjit, by: _harpal);
        _clock.Advance(TimeSpan.FromSeconds(20));
        // Back to the person it started with: given to them twice, told once, and nobody in between.
        await HandAsync(id, _gurpreet, by: _harpal);

        (await TurnAsync()).Should().Be(1);
        _mail.Sent.Single().To.Should().Equal("gurpreet@techpio.test");
        Handovers(id).Should().HaveCount(4).And.OnlyContain(a => a.NotifiedAt != null);
        (await TurnAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Going_back_to_the_board_and_a_finished_ticket_mail_nobody()
    {
        var returned = await RaiseAsync(_harpal, assignee: _gurpreet);
        await TurnAsync();
        _mail.Sent.Clear();
        await HandAsync(returned, null, by: _harpal);

        var done = await RaiseAsync(_harpal, assignee: _basit);
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == done);
        ticket.PortalStatus = "CLOSED";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(0);
        _mail.Sent.Should().BeEmpty();
        Handovers(returned).Concat(Handovers(done)).Should().OnlyContain(a => a.NotifiedAt != null);
    }

    [Fact]
    public async Task A_ticket_that_no_longer_rests_with_them_is_not_announced()
    {
        // The holder moved on some path that writes no handover (a PSA sync correcting it, say):
        // the last handover on record names someone who no longer has the ticket.
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == id);
        ticket.AssignedAppUserId = _basit;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(0);
        _mail.Sent.Should().BeEmpty();
        Handovers(id).Single().NotifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_person_who_has_left_is_skipped_and_holds_nothing_up()
    {
        var gone = await RaiseAsync(_harpal, assignee: _gurpreet);
        var here = await RaiseAsync(_harpal, assignee: _basit);
        var left = await _db.AppUsers.SingleAsync(u => u.Id == _gurpreet);
        left.IsActive = false;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(1);
        _mail.Sent.Single().To.Should().Equal("basit@techpio.test");
        Handovers(gone).Concat(Handovers(here)).Should().OnlyContain(a => a.NotifiedAt != null);
    }

    [Fact]
    public async Task An_assignment_a_moment_old_waits_for_the_next_turn()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);

        (await TurnAsync(secondsLater: 4)).Should().Be(0, "it may still be corrected");
        Handovers(id).Single().NotifiedAt.Should().BeNull();
        (await TurnAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_message_that_could_not_be_mailed_is_logged_and_not_sent_again_for_ever()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);
        _mail.Fail = new InvalidOperationException("The mail server refused the connection.");

        (await TurnAsync()).Should().Be(0);
        _mail.Fail = null;
        (await TurnAsync()).Should().Be(0, "the ticket is in their list; a late repeat is noise");

        var log = await _db.EmailLog.AsNoTracking().SingleAsync();
        (log.TemplateKey, log.Succeeded, log.To).Should().Be((EmailTemplates.TicketAssigned, false, "gurpreet@techpio.test"));
        log.SubjectId.Should().Be(id);
    }

    // ---- a device --------------------------------------------------------------------------------

    [Fact]
    public async Task A_device_is_told_about_a_ticket_assigned_as_it_was_raised()
    {
        await DeviceAsync(_gurpreet);
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);

        await TurnAsync();
        var push = await _db.PushNotifications.AsNoTracking().SingleAsync();
        (push.AppUserId, push.Kind, push.TicketId, push.Title, push.Body).Should().Be((_gurpreet, PushKind.Assigned, (Guid?)id, "TECPIO-000001 assigned to you", "Set up the new firewall"));
        push.Url.Should().Be($"/dashboard/tickets/{id}");
    }

    [Fact]
    public async Task A_device_is_not_told_here_about_a_ticket_that_changes_hands_later_because_the_scan_already_does()
    {
        await DeviceAsync(_basit);
        var id = await RaiseAsync(_harpal);
        await TurnAsync();
        await HandAsync(id, _basit, by: _harpal);

        (await TurnAsync()).Should().Be(1, "the e-mail is still owed");
        (await _db.PushNotifications.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_device_stays_silent_where_its_owner_switched_assignments_off()
    {
        await DeviceAsync(_gurpreet);
        _db.PushPreferences.Add(new PushPreference { MspOrganizationId = Org, AppUserId = _gurpreet, Assigned = false });
        await _db.SaveChangesAsync();
        await RaiseAsync(_harpal, assignee: _gurpreet);

        (await TurnAsync()).Should().Be(1, "the e-mail is not what they switched off");
        (await _db.PushNotifications.AsNoTracking().CountAsync()).Should().Be(0);
    }

    // ---- beside the people kept informed -----------------------------------------------------------

    [Fact]
    public async Task The_person_it_is_assigned_to_and_the_people_kept_informed_each_get_their_own_message()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet, _basit, _sarabjit);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var assigned = await Notifier.RunAsync();
        var informed = await new TicketFollowerNotifier(_db, Mailer, _portal, _clock, NullLogger<TicketFollowerNotifier>.Instance).RunAsync();

        (assigned, informed).Should().Be((1, 2));
        _mail.Sent.Select(m => (m.To.Single(), m.Subject)).Should().BeEquivalentTo(new[]
        {
            ("gurpreet@techpio.test", "Harpal Singh assigned TECPIO-000001 to you"),
            ("basit@techpio.test", "Harpal Singh added you to TECPIO-000001"),
            ("sarabjit@techpio.test", "Harpal Singh added you to TECPIO-000001"),
        });
        _ = id;
    }

    // ---- whose it is ---------------------------------------------------------------------------------

    [Fact]
    public async Task Another_organizations_assignments_are_not_touched()
    {
        var id = await RaiseAsync(_harpal, assignee: _gurpreet);
        var other = Guid.NewGuid();
        var elsewhere = new TenantContext();
        elsewhere.SetTenant(other);
        using var theirs = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, elsewhere, _clock);
        theirs.Add(new MspOrganization { Id = other, Name = "Another MSP", Slug = "another", TimeZone = "UTC" });
        await theirs.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(2));
        var sent = await new TicketAssignmentNotifier(theirs, new TemplateMailer(theirs, _mail, _portal, _clock, NullLogger<TemplateMailer>.Instance),
            _portal, _clock, NullLogger<TicketAssignmentNotifier>.Instance).RunAsync();

        sent.Should().Be(0);
        _mail.Sent.Should().BeEmpty();
        Handovers(id).Single().NotifiedAt.Should().BeNull("this organization's turn has not come yet");
    }

    [Fact]
    public void The_message_is_the_organizations_to_reword_and_offers_only_what_it_can_fill()
    {
        var assigned = EmailTemplates.Find(EmailTemplates.TicketAssigned)!;
        assigned.Placeholders.Should().Contain(["assigned_by", "ticket.number", "ticket.title", "link", "user.first_name"]);
        assigned.Placeholders.Should().NotContain("organization.name", "nothing here carries it");
        EmailTemplates.Unknown(assigned, assigned.Subject + assigned.Body).Should().BeEmpty();

        var sample = EmailTemplates.SampleValues("TechPio", "https://portal.example");
        assigned.Placeholders.Should().OnlyContain(p => sample.ContainsKey(p), "a preview shows something for every placeholder");
    }
}
