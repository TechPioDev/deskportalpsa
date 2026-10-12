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
/// Keeping colleagues informed on a ticket: named when it is raised, told that they were put on
/// it, and told what happens after.
///
/// What these hold: the people named on the form become followers and the assignee is not doubled
/// up; a wrong or inactive person, or too many, is refused and no ticket is made; a follower is
/// told once that somebody added them, and never that they followed it themselves; replies and
/// notes by others, and the ticket being finished, reach them as one message per turn; their own
/// reply is not news to them; nothing from the thread is quoted; what happened before they
/// followed is not sent; nothing is said twice; a person who has left is skipped without holding
/// the rest up; a mail failure does not repeat for ever; a device is told too where there is one;
/// and another organization's followers are not touched. On a SQL translator.
/// </summary>
public sealed class TicketFollowerNoticeTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly RecordingSender _mail = new();
    private readonly PortalOptions _portal = new() { PublicUrl = "https://portal.example" };
    private readonly Guid _harpal, _ravi, _priya, _amit, _board;

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

    public TicketFollowerNoticeTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        AppUser Person(string name) => new() { MspOrganizationId = Org, DisplayName = name, Email = $"{name.Split(' ')[0].ToLowerInvariant()}@techpio.test", IsActive = true };
        var (harpal, ravi, priya, amit) = (Person("Harpal Singh"), Person("Ravi Kumar"), Person("Priya Sharma"), Person("Amit Verma"));
        var board = new Board { MspOrganizationId = Org, Name = "TechPio", Key = "TECPIO", Kind = BoardKind.Internal };
        (_harpal, _ravi, _priya, _amit, _board) = (harpal.Id, ravi.Id, priya.Id, amit.Id, board.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "Asia/Kolkata" }, harpal, ravi, priya, amit, board);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private InternalTicketService Tickets => new(_db, _tenant, _clock, new RecordingActivity());
    private TicketFollowerNotifier Notifier => new(_db, new TemplateMailer(_db, _mail, _portal, _clock, NullLogger<TemplateMailer>.Instance),
        _portal, _clock, NullLogger<TicketFollowerNotifier>.Instance);

    private async Task<Guid> RaiseAsync(Guid? assignee = null, params Guid[] keepInformed)
        => (await Tickets.CreateAsync(_harpal, new InternalTicketInput(_board, "NinjaRMM implementation", null,
            AssignedAppUserId: assignee, FollowerUserIds: keepInformed))).TicketId;

    private async Task NoteAsync(Guid ticketId, string author, bool isPublic = true, bool byClient = false, string body = "The licence key is 4471-secret.")
    {
        _db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = Org, TicketId = ticketId, AuthorName = author, Body = body, IsPublic = isPublic,
            AuthoredByClient = byClient, NoteCreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<int> TurnAsync(int minutesLater = 2)
    {
        _clock.Advance(TimeSpan.FromMinutes(minutesLater));
        var sent = await Notifier.RunAsync();
        _db.ChangeTracker.Clear();
        return sent;
    }

    private List<TicketFollower> Followers(Guid ticketId) => _db.TicketFollowers.AsNoTracking().Where(f => f.TicketId == ticketId).ToList();

    // ---- naming people when the ticket is raised ------------------------------------------------

    [Fact]
    public async Task The_people_named_on_the_form_follow_the_ticket_and_the_assignee_is_not_doubled_up()
    {
        var id = await RaiseAsync(assignee: _ravi, _priya, _amit, _ravi, _priya);

        Followers(id).Select(f => f.AppUserId).Should().BeEquivalentTo([_priya, _amit]);
        Followers(id).Should().OnlyContain(f => f.AddedByUserId == _harpal && f.AddedNotifiedAt == null && f.NotifiedThrough == null);
        (await _db.Tickets.AsNoTracking().SingleAsync(t => t.Id == id)).AssignedAppUserId.Should().Be(_ravi);
    }

    [Fact]
    public async Task Someone_who_is_not_active_staff_is_refused_and_no_ticket_is_made()
    {
        var gone = await _db.AppUsers.SingleAsync(u => u.Id == _amit);
        gone.IsActive = false;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var inactive = () => RaiseAsync(null, _priya, _amit);
        (await inactive.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("not an active member of staff");
        var unknown = () => RaiseAsync(null, Guid.NewGuid());
        await unknown.Should().ThrowAsync<ValidationFailedException>();

        (await _db.Tickets.CountAsync()).Should().Be(0);
        (await _db.TicketFollowers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task More_people_than_the_limit_is_refused()
    {
        var crowd = Enumerable.Range(0, InternalTicketInput.MaxFollowers + 1).Select(_ => Guid.NewGuid()).ToArray();
        var act = () => RaiseAsync(null, crowd);
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain($"up to {InternalTicketInput.MaxFollowers}");
        (await _db.Tickets.CountAsync()).Should().Be(0);
    }

    // ---- being told ---------------------------------------------------------------------------

    [Fact]
    public async Task A_person_somebody_added_is_told_once_who_added_them_and_to_which_ticket()
    {
        var id = await RaiseAsync(assignee: _ravi, _priya);

        (await TurnAsync()).Should().Be(1);
        var mail = _mail.Sent.Single();
        mail.To.Should().Equal("priya@techpio.test");
        mail.Subject.Should().Be("Harpal Singh added you to TECPIO-000001");
        mail.TextBody.Should().Contain("Hi Priya,").And.Contain("\"NinjaRMM implementation\"").And.Contain("It is not assigned to you")
            .And.Contain($"https://portal.example/dashboard/tickets/{id}");

        // Nothing more to say, turn after turn.
        (await TurnAsync()).Should().Be(0);
        (await TurnAsync()).Should().Be(0);
        _mail.Sent.Should().HaveCount(1);
        Followers(id).Single().AddedNotifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Nobody_is_told_that_they_followed_a_ticket_themselves()
    {
        var id = await RaiseAsync();
        _db.TicketFollowers.Add(new TicketFollower { MspOrganizationId = Org, TicketId = id, AppUserId = _priya, AddedByUserId = _priya });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(0);
        _mail.Sent.Should().BeEmpty();
        Followers(id).Single().AddedNotifiedAt.Should().NotBeNull("the question is settled, so it is not asked again");
    }

    [Fact]
    public async Task Replies_and_notes_by_others_arrive_as_one_message_and_quote_nothing_from_the_thread()
    {
        var id = await RaiseAsync(assignee: _ravi, _priya);
        await TurnAsync();
        _mail.Sent.Clear();

        await NoteAsync(id, "Ravi Kumar", isPublic: true);
        await NoteAsync(id, "Harpal Singh", isPublic: false);
        await NoteAsync(id, "Ben at the client", isPublic: true, byClient: true);

        (await TurnAsync()).Should().Be(1);
        var mail = _mail.Sent.Single();
        mail.Subject.Should().Be("News on TECPIO-000001: NinjaRMM implementation");
        mail.TextBody.Should().Contain("- Ravi Kumar replied")
            .And.Contain("- Harpal Singh added an internal note")
            .And.Contain("- The client replied (Ben at the client)");
        mail.TextBody.Should().NotContain("4471-secret", "the thread stays on the ticket");
        mail.HtmlBody.Should().NotContain("4471-secret");

        (await TurnAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_followers_own_reply_is_not_news_to_them_but_is_to_the_others()
    {
        var id = await RaiseAsync(null, _priya, _amit);
        await TurnAsync();
        _mail.Sent.Clear();

        await NoteAsync(id, "priya sharma ");

        (await TurnAsync()).Should().Be(1);
        _mail.Sent.Single().To.Should().Equal("amit@techpio.test");
    }

    [Fact]
    public async Task What_happened_before_someone_followed_is_not_sent_to_them()
    {
        var id = await RaiseAsync();
        await NoteAsync(id, "Ravi Kumar");
        _clock.Advance(TimeSpan.FromHours(1));
        _db.TicketFollowers.Add(new TicketFollower { MspOrganizationId = Org, TicketId = id, AppUserId = _amit, AddedByUserId = _harpal });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(1, "only the message that they were added");
        _mail.Sent.Single().Subject.Should().StartWith("Harpal Singh added you");
    }

    [Fact]
    public async Task The_ticket_being_closed_is_told_once()
    {
        var id = await RaiseAsync(assignee: _ravi, _priya);
        await TurnAsync();
        _mail.Sent.Clear();

        _clock.Advance(TimeSpan.FromMinutes(30));
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == id);
        (ticket.PortalStatus, ticket.ClosedAt) = ("CLOSED", _clock.GetUtcNow());
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(1);
        _mail.Sent.Single().TextBody.Should().Contain("- The ticket was closed");
        (await TurnAsync()).Should().Be(0);
    }

    [Fact]
    public async Task News_written_in_the_last_seconds_before_a_turn_waits_for_the_next_and_is_not_lost()
    {
        var id = await RaiseAsync(null, _priya);
        await TurnAsync();
        _mail.Sent.Clear();

        // Stamped three seconds before the turn: its save may not have finished when the turn looks.
        _clock.Advance(TimeSpan.FromMinutes(2));
        await NoteAsync(id, "Ravi Kumar");
        _clock.Advance(TimeSpan.FromSeconds(3));
        (await Notifier.RunAsync()).Should().Be(0, "inside the settling time it is left for the next turn");
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(1);
        _mail.Sent.Single().TextBody.Should().Contain("- Ravi Kumar replied");
        (await TurnAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_long_day_on_one_ticket_is_still_one_short_message()
    {
        var id = await RaiseAsync(null, _priya);
        await TurnAsync();
        _mail.Sent.Clear();
        for (var i = 0; i < 12; i++) await NoteAsync(id, "Ravi Kumar");

        (await TurnAsync()).Should().Be(1);
        var lines = _mail.Sent.Single().TextBody.Split('\n').Where(l => l.StartsWith("- ")).ToList();
        lines.Should().HaveCount(8);
        lines[^1].Should().Be("- and 5 more");
    }

    [Fact]
    public async Task Someone_who_has_left_is_skipped_and_the_others_are_still_told()
    {
        var id = await RaiseAsync(null, _priya, _amit);
        var left = await _db.AppUsers.SingleAsync(u => u.Id == _priya);
        left.IsActive = false;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await TurnAsync()).Should().Be(1);
        _mail.Sent.Single().To.Should().Equal("amit@techpio.test");
        Followers(id).Should().OnlyContain(f => f.AddedNotifiedAt != null && f.NotifiedThrough != null);
    }

    [Fact]
    public async Task A_message_that_could_not_be_mailed_is_logged_and_not_sent_again_for_ever()
    {
        var id = await RaiseAsync(null, _priya);
        _mail.Fail = new InvalidOperationException("The mail server refused the connection.");

        (await TurnAsync()).Should().Be(0);
        _mail.Fail = null;
        (await TurnAsync()).Should().Be(0, "the news is on the ticket; a late repeat is noise");

        var log = await _db.EmailLog.AsNoTracking().SingleAsync();
        (log.TemplateKey, log.Succeeded, log.To).Should().Be((EmailTemplates.FollowerAdded, false, "priya@techpio.test"));
        log.SubjectId.Should().Be(id);
    }

    [Fact]
    public async Task A_device_is_told_too_where_the_person_switched_that_on()
    {
        _db.PushSubscriptions.Add(new PushSubscription { MspOrganizationId = Org, AppUserId = _priya, Endpoint = "https://push.example/1", P256dh = "k", Auth = "a" });
        await _db.SaveChangesAsync();
        var id = await RaiseAsync(null, _priya, _amit);

        await TurnAsync();
        var push = await _db.PushNotifications.AsNoTracking().SingleAsync();
        (push.AppUserId, push.Kind, push.TicketId, push.Title).Should().Be((_priya, PushKind.Following, (Guid?)id, "Harpal Singh added you to TECPIO-000001"));
        push.Url.Should().Be($"/dashboard/tickets/{id}");
    }

    [Fact]
    public async Task Another_organizations_followers_are_not_touched()
    {
        var id = await RaiseAsync(null, _priya);
        var other = Guid.NewGuid();
        var elsewhere = new TenantContext();
        elsewhere.SetTenant(other);
        using var theirs = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, elsewhere, _clock);
        theirs.Add(new MspOrganization { Id = other, Name = "Another MSP", Slug = "another", TimeZone = "UTC" });
        await theirs.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(2));
        var sent = await new TicketFollowerNotifier(theirs, new TemplateMailer(theirs, _mail, _portal, _clock, NullLogger<TemplateMailer>.Instance),
            _portal, _clock, NullLogger<TicketFollowerNotifier>.Instance).RunAsync();

        sent.Should().Be(0);
        _mail.Sent.Should().BeEmpty();
        Followers(id).Single().AddedNotifiedAt.Should().BeNull("this organization's turn has not come yet");
    }

    [Fact]
    public async Task The_two_messages_are_the_organizations_to_reword_and_offer_only_what_they_can_fill()
    {
        var added = EmailTemplates.Find(EmailTemplates.FollowerAdded)!;
        var news = EmailTemplates.Find(EmailTemplates.FollowerNews)!;
        EmailTemplates.Unknown(added, added.Subject + added.Body + added.ButtonLabel).Should().BeEmpty();
        EmailTemplates.Unknown(news, news.Subject + news.Body + news.ButtonLabel).Should().BeEmpty();
        EmailTemplates.Unknown(news, "Dear {{organization.name}}").Should().Equal("organization.name");

        var sample = EmailTemplates.SampleValues("TechPio", "https://portal.example");
        foreach (var placeholder in added.Placeholders.Concat(news.Placeholders))
            sample.Should().ContainKey(placeholder, "a preview fills every placeholder");
        await Task.CompletedTask;
    }
}
