using Desk.Application.Common;
using Desk.Application.Identity;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Identity;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A person is invited by a one-time link, sets their password at the identity provider through
/// it, and is signed in from then on; a person who has a sign-in can ask for a new password the
/// same way.
///
/// What these hold: the link is mailed and shown once, and only its hash is kept; the page at a
/// link gives away no more than the mail did, and an unknown link reads like a revoked one; a
/// used, expired or revoked link cannot be used; a second invitation replaces the first; the
/// password rules; reminders on the third and sixth day, an expiry notice once; a reset goes only
/// to an address that can sign in, and says nothing either way; another organization's link is
/// not found from inside this one; and an administrator's wording is validated, previewed and
/// put back. On a SQL translator.
/// </summary>
public sealed class InvitationTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly RecordingSender _mail = new();
    private readonly InMemoryKeycloakAdmin _keycloak = new();
    private readonly PortalOptions _portal = new() { PublicUrl = "https://portal.example" };
    private readonly TestCurrentUser _admin;
    private readonly Guid _adminId = Guid.NewGuid();
    private Guid _ashaId, _companyId, _priyaId;

    private sealed class RecordingSender(bool configured = true) : IEmailSender
    {
        public List<(Guid Org, EmailMessage Message)> Sent { get; } = [];
        public Exception? Fail { get; set; }
        public Task<EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(configured ? new EmailSenderStatus(true, "desk@techpio.test", "organization") : EmailSenderStatus.None);
        public Task SendAsync(Guid organizationId, EmailMessage message, CancellationToken ct = default)
        {
            if (Fail is not null) throw Fail;
            Sent.Add((organizationId, message));
            return Task.CompletedTask;
        }
        public EmailMessage Last => Sent[^1].Message;
    }

    public InvitationTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        var connection = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Main", Provider = Desk.Domain.Enums.ProviderType.AutotaskPsa, ApiEndpoint = "https://at.example/",
            CredentialSecretRef = "mem://main", IsEnabled = true,
        };
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "Acme", ExternalCompanyId = "1" };
        var admin = new AppUser { Id = _adminId, MspOrganizationId = Org, DisplayName = "Priya Nair", Email = "priya@techpio.test", IsActive = true, IdpSubject = "kc-priya" };
        var asha = new AppUser { MspOrganizationId = Org, DisplayName = "Asha Rao", Email = "asha@techpio.test", IsActive = true };
        var client = new ClientUser { MspOrganizationId = Org, ClientCompanyId = company.Id, DisplayName = "Ben Okafor", Email = "ben@acme.test", IsCompanyAdministrator = true };
        (_ashaId, _companyId, _priyaId) = (asha.Id, company.Id, client.Id);
        _db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "Asia/Kolkata" }, connection, company, admin, asha, client);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _admin = new TestCurrentUser(Org, subject: "kc-priya", name: "Priya Nair", userId: _adminId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private AuditWriter Audit => new(_db, _admin, _tenant, _clock);
    private TemplateMailer Mailer => new(_db, _mail, _portal, _clock, NullLogger<TemplateMailer>.Instance);
    private InvitationService Invitations => new(_db, _tenant, _admin, _keycloak, Mailer, Audit, _portal, _clock);
    private EmailTemplateService Templates => new(_db, _tenant, _admin, Mailer, _mail, _portal, Audit, _clock);

    /// <summary>The service as an anonymous request would build it: nobody signed in, no organization known.</summary>
    private InvitationService Anonymous(TenantContext? scope = null)
    {
        var tenant = scope ?? new TenantContext();
        var db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, tenant, _clock);
        var nobody = new TestCurrentUser(null, subject: "", name: "");
        var mailer = new TemplateMailer(db, _mail, _portal, _clock, NullLogger<TemplateMailer>.Instance);
        return new InvitationService(db, tenant, nobody, _keycloak, mailer, new AuditWriter(db, nobody, tenant, _clock), _portal, _clock);
    }

    private static string TokenOf(string link) => link[(link.LastIndexOf('/') + 1)..];

    private async Task<SignInInvitation> RowAsync(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.SignInInvitations.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<List<string>> AuditedAsync()
        => await _db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("user.") || a.Action.StartsWith("email.")).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => a.Action).ToListAsync();

    // ------------------------------------------------------------------ invited, and in

    [Fact]
    public async Task A_staff_user_is_invited_by_a_link_that_is_mailed_and_shown_once_and_only_its_hash_is_kept()
    {
        var sent = await Invitations.InviteStaffAsync(_ashaId);

        sent.Mailed.Should().BeTrue();
        sent.Link.Should().StartWith("https://portal.example/invite/");
        var token = TokenOf(sent.Link);
        token.Length.Should().BeGreaterThan(30);
        sent.Invitation.Should().BeEquivalentTo(new { Kind = "Staff", Email = "asha@techpio.test", State = "Sent", SentCount = 1, CreatedByName = "Priya Nair", ExpiresAt = _clock.GetUtcNow().AddDays(7) });

        var row = await RowAsync(sent.Invitation.Id);
        row.TokenHash.Should().Be(InvitationService.Hash(token)).And.NotContain(token);
        _mail.Sent.Should().ContainSingle();
        _mail.Last.To.Should().Equal("asha@techpio.test");
        _mail.Last.Subject.Should().Be("You're invited to PioManage by TechPio");
        _mail.Last.TextBody.Should().Contain("Hi Asha,").And.Contain("Priya Nair has invited you").And.Contain(sent.Link).And.Contain("Thursday 15 October 2026, ")
            // Seven days on, in the organization's own time zone (14:30 in Kolkata; 15 October 2026 is a Thursday). This PC cannot always resolve that
            // zone, and then the service falls back to UTC (09:00): the day is pinned and the hour may be either.
            .And.Match(t => t.Contains("15 October 2026, 14:30") || t.Contains("15 October 2026, 09:00"));
        _mail.Last.HtmlBody.Should().Contain("href=\"" + sent.Link + "\"").And.Contain("Set my password and sign in").And.Contain("PioManage");
        (await _db.EmailLog.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(new { TemplateKey = "user.invited", To = "asha@techpio.test", Succeeded = true });
        (await AuditedAsync()).Should().Equal("user.invited");
    }

    [Fact]
    public async Task The_page_at_the_link_says_who_and_when_and_nothing_more_and_an_unknown_link_reads_like_a_revoked_one()
    {
        var sent = await Invitations.InviteStaffAsync(_ashaId);
        var token = TokenOf(sent.Link);

        var page = await Anonymous().LookupAsync(token);
        page.Should().BeEquivalentTo(new
        {
            Purpose = "Invite", Kind = "Staff", DisplayName = "Asha Rao", Email = "asha@techpio.test",
            OrganizationName = "TechPio", State = "Open", CanBeAccepted = true,
        });

        (await Anonymous().LookupAsync("not-a-link")).Should().BeNull();
        (await Anonymous().LookupAsync("")).Should().BeNull();
        await Invitations.RevokeAsync(sent.Invitation.Id);
        (await Anonymous().LookupAsync(token)).Should().BeNull("revoked and unknown are the same from outside");
        await FluentActions.Awaiting(() => Anonymous().AcceptAsync(token, "a-long-enough-password-1")).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Using_the_link_sets_the_password_at_the_identity_provider_links_the_sign_in_and_spends_the_link()
    {
        var sent = await Invitations.InviteStaffAsync(_ashaId);
        var token = TokenOf(sent.Link);
        _clock.Advance(TimeSpan.FromHours(2));

        var accepted = await Anonymous().AcceptAsync(token, "correct horse battery staple");

        accepted.Should().BeEquivalentTo(new { Email = "asha@techpio.test", Kind = "Staff", SignInPath = "/api/auth/login?login_hint=asha%40techpio.test" });
        var user = _keycloak.Users["asha@techpio.test"];
        user.Password.Should().Be("correct horse battery staple");
        _db.ChangeTracker.Clear();
        (await _db.AppUsers.AsNoTracking().SingleAsync(u => u.Id == _ashaId)).IdpSubject.Should().Be(user.Id, "the sign-in is linked now, not on a later first sign-in");
        var row = await RowAsync(sent.Invitation.Id);
        row.ConsumedAt.Should().Be(_clock.GetUtcNow());
        _mail.Sent.Should().HaveCount(2);
        _mail.Last.Subject.Should().Be("Welcome to PioManage, Asha");
        _mail.Last.TextBody.Should().Contain("https://portal.example/dashboard");
        (await Invitations.ListAsync()).Single().State.Should().Be("Accepted");

        // Spent: the same link does nothing more, and says so without sending anything.
        (await Anonymous().LookupAsync(token))!.Should().BeEquivalentTo(new { State = "Used", CanBeAccepted = false });
        (await FluentActions.Awaiting(() => Anonymous().AcceptAsync(token, "another fine password 2")).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("already been used");
        _keycloak.Users["asha@techpio.test"].Password.Should().Be("correct horse battery staple");
        (await AuditedAsync()).Should().Equal("user.invited", "user.invitation_accepted");
    }

    [Fact]
    public async Task A_weak_password_is_refused_before_anything_reaches_the_identity_provider()
    {
        var token = TokenOf((await Invitations.InviteStaffAsync(_ashaId)).Link);
        foreach (var bad in new[] { "short", "asha@techpio.test", "aaaaaaaaaaaaaaaa", "" })
            await FluentActions.Awaiting(() => Anonymous().AcceptAsync(token, bad)).Should().ThrowAsync<ValidationFailedException>(bad);
        _keycloak.Users.Should().BeEmpty();
        (await RowAsync((await Invitations.ListAsync()).Single().Id)).ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_second_invitation_replaces_the_first_and_one_already_signed_in_is_not_invited()
    {
        var first = await Invitations.InviteStaffAsync(_ashaId);
        _clock.Advance(TimeSpan.FromDays(1));
        var second = await Invitations.InviteStaffAsync(_ashaId);

        (await Anonymous().LookupAsync(TokenOf(first.Link))).Should().BeNull("the earlier link is dead");
        (await Anonymous().LookupAsync(TokenOf(second.Link)))!.State.Should().Be("Open");
        (await Invitations.ListAsync()).Select(i => i.State).Should().Equal("Sent", "Revoked");

        await FluentActions.Awaiting(() => Invitations.InviteStaffAsync(_adminId)).Should().ThrowAsync<ValidationFailedException>().WithMessage("*already signs in*");
    }

    [Fact]
    public async Task Everyone_who_never_signed_in_can_be_invited_at_once_and_those_with_an_open_invitation_are_not_mailed_twice()
    {
        _db.AppUsers.AddRange(
            new AppUser { MspOrganizationId = Org, DisplayName = "Ravi Menon", Email = "ravi@techpio.test", IsActive = true },
            new AppUser { MspOrganizationId = Org, DisplayName = "Gone Person", Email = "gone@techpio.test", IsActive = false });
        await _db.SaveChangesAsync();
        await Invitations.InviteStaffAsync(_ashaId);
        _mail.Sent.Clear();

        var result = await Invitations.InviteAllPendingStaffAsync();

        result.Should().BeEquivalentTo(new { Invited = 1, Mailed = 1, Failed = Array.Empty<string>() }, "Asha has an open invitation, Priya signs in, the deactivated one is left alone");
        _mail.Sent.Should().ContainSingle().Which.Message.To.Should().Equal("ravi@techpio.test");
    }

    // ------------------------------------------------------------------ time

    [Fact]
    public async Task Reminders_go_on_the_third_and_sixth_day_with_a_fresh_link_then_an_expiry_notice_once()
    {
        var sent = await Invitations.InviteStaffAsync(_ashaId);
        var firstToken = TokenOf(sent.Link);

        _clock.Advance(TimeSpan.FromDays(2));
        (await Invitations.SendRemindersAsync()).Should().Be(0, "too soon");
        _clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        (await Invitations.SendRemindersAsync()).Should().Be(1);
        _mail.Last.Subject.Should().Be("Reminder: your PioManage invitation is waiting");
        var reminderLink = _mail.Last.TextBody.Split('\n').First(l => l.StartsWith("https://portal.example/invite/"));
        (await Anonymous().LookupAsync(firstToken)).Should().BeNull("the reminder carries a fresh link; the first one is dead, as the reminder says");
        _mail.Last.TextBody.Should().Contain("earlier link no longer works");
        (await Anonymous().LookupAsync(TokenOf(reminderLink)))!.State.Should().Be("Open");
        (await Invitations.SendRemindersAsync()).Should().Be(0, "once");

        _clock.Advance(TimeSpan.FromDays(3));
        (await Invitations.SendRemindersAsync()).Should().Be(1, "the sixth day");
        var row = await RowAsync(sent.Invitation.Id);
        (row.RemindersSent, row.SentCount).Should().Be((2, 3));

        _clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        (await Anonymous().LookupAsync(TokenOf(_mail.Last.TextBody.Split('\n').First(l => l.StartsWith("https://portal.example/invite/")))))!
            .Should().BeEquivalentTo(new { State = "Expired", CanBeAccepted = false });
        (await Invitations.SendRemindersAsync()).Should().Be(1, "the expiry notice");
        _mail.Last.Subject.Should().Be("Your PioManage invitation has expired");
        _mail.Last.TextBody.Should().NotContain("/invite/", "nothing to click: it has expired");
        (await Invitations.SendRemindersAsync()).Should().Be(0, "and that notice goes once");
        (await Invitations.ListAsync()).Single().State.Should().Be("Expired");
    }

    [Fact]
    public async Task Mail_that_cannot_go_does_not_stop_the_invitation_and_is_said_to_the_inviter_and_logged()
    {
        _mail.Fail = new InvalidOperationException("SMTP refused the connection.");
        var sent = await Invitations.InviteStaffAsync(_ashaId);

        sent.Mailed.Should().BeFalse();
        sent.MailError.Should().Contain("SMTP refused");
        sent.Link.Should().Contain("/invite/", "the inviter can still copy the link");
        (await Anonymous().LookupAsync(TokenOf(sent.Link)))!.State.Should().Be("Open");
        (await _db.EmailLog.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(new { Succeeded = false, Error = "SMTP refused the connection." });
        (await RowAsync(sent.Invitation.Id)).SentCount.Should().Be(0);
    }

    // ------------------------------------------------------------------ a client, another organization

    [Fact]
    public async Task A_client_user_is_invited_the_same_way_and_lands_in_the_client_portal()
    {
        var sent = await Invitations.InviteClientAsync(_priyaId);
        sent.Invitation.Kind.Should().Be("Client");
        var accepted = await Anonymous().AcceptAsync(TokenOf(sent.Link), "a client password that is long");
        accepted.Kind.Should().Be("Client");
        _db.ChangeTracker.Clear();
        (await _db.ClientUsers.AsNoTracking().SingleAsync(u => u.Id == _priyaId)).IdpSubject.Should().Be(_keycloak.Users["ben@acme.test"].Id);
    }

    [Fact]
    public async Task Another_organizations_link_is_not_found_from_inside_this_one()
    {
        var sent = await Invitations.InviteStaffAsync(_ashaId);
        var other = new TenantContext();
        other.SetTenant(Guid.NewGuid());
        (await Anonymous(other).LookupAsync(TokenOf(sent.Link))).Should().BeNull();
        await FluentActions.Awaiting(() => Anonymous(other).AcceptAsync(TokenOf(sent.Link), "a long enough password 9")).Should().ThrowAsync<NotFoundException>();
        _keycloak.Users.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ a new password

    [Fact]
    public async Task A_password_reset_goes_only_to_an_address_that_can_sign_in_says_nothing_either_way_and_sets_the_password_when_used()
    {
        await _keycloak.CreateUserAsync("priya@techpio.test", "Priya Nair", "old password of priya's");
        await Anonymous().RequestPasswordResetAsync("nobody@techpio.test");
        await Anonymous().RequestPasswordResetAsync("asha@techpio.test");
        _mail.Sent.Should().BeEmpty("nobody has the first address, and Asha has no sign-in yet to reset");

        await Anonymous().RequestPasswordResetAsync("PRIYA@techpio.test");
        _mail.Sent.Should().ContainSingle();
        _mail.Last.Subject.Should().Be("Reset your PioManage password");
        var link = _mail.Last.TextBody.Split('\n').First(l => l.StartsWith("https://portal.example/reset-password/"));
        (await Anonymous().LookupAsync(TokenOf(link)))!.Should().BeEquivalentTo(new { Purpose = "PasswordReset", DisplayName = "Priya Nair", ExpiresAt = _clock.GetUtcNow().AddHours(1) });
        (await Invitations.ListAsync()).Should().BeEmpty("resets are not listed among invitations");

        await Anonymous().AcceptAsync(TokenOf(link), "new password of priya's own");
        _keycloak.Users["priya@techpio.test"].Password.Should().Be("new password of priya's own");
        _mail.Sent.Should().ContainSingle("no welcome mail for a reset");
        (await Anonymous().LookupAsync(TokenOf(link)))!.State.Should().Be("Used");

        // An hour and it is gone.
        await Anonymous().RequestPasswordResetAsync("priya@techpio.test");
        var second = _mail.Last.TextBody.Split('\n').First(l => l.StartsWith("https://portal.example/reset-password/"));
        _clock.Advance(TimeSpan.FromMinutes(61));
        (await Anonymous().LookupAsync(TokenOf(second)))!.State.Should().Be("Expired");
    }

    // ------------------------------------------------------------------ the wording

    [Fact]
    public async Task An_administrators_wording_is_checked_previewed_sent_and_put_back()
    {
        var all = await Templates.ListAsync();
        all.Select(t => t.Key).Should().Equal("user.invited", "user.invite_reminder", "user.invite_expired", "user.welcome", "user.password_reset");
        all.Should().OnlyContain(t => t.IsDefault);

        (await FluentActions.Awaiting(() => Templates.SaveAsync("user.invited", new EmailTemplateInput("Hello {{user.nmae}}", "Body {{ticket.number}}", "Go")))
            .Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("{{user.nmae}} is not a placeholder").And.Contain("{{ticket.number}} is not a placeholder");
        await FluentActions.Awaiting(() => Templates.SaveAsync("user.invited", new EmailTemplateInput("Hello", "Body {{link}}", null)))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*needs a button label*");
        await FluentActions.Awaiting(() => Templates.SaveAsync("no.such", new EmailTemplateInput("a", "b", null))).Should().ThrowAsync<NotFoundException>();

        var saved = await Templates.SaveAsync("user.invited", new EmailTemplateInput("Welcome aboard, {{user.first_name}} <3", "{{organization.name}} is glad to have you <3.\n\nSet your password here: {{link}}", "Let me in"));
        saved.Should().BeEquivalentTo(new { IsDefault = false, UpdatedByName = "Priya Nair", Subject = "Welcome aboard, {{user.first_name}} <3" });

        var preview = await Templates.PreviewAsync("user.invited");
        preview.Subject.Should().Be("Welcome aboard, Asha <3");
        preview.HtmlBody.Should().Contain("Let me in").And.Contain("TechPio is glad to have you &lt;3.", "angle brackets are text, not markup").And.NotContain("you <3");
        preview.TextBody.Should().Contain("TechPio is glad to have you <3.");

        // The next invitation is sent in those words.
        await Invitations.InviteStaffAsync(_ashaId);
        _mail.Last.Subject.Should().Be("Welcome aboard, Asha <3");
        _mail.Last.TextBody.Should().Contain("TechPio is glad to have you <3.");

        var test = await Templates.SendTestAsync("user.invited");
        test.To.Should().Be("admin@test");
        _mail.Last.Subject.Should().StartWith("[Test] Welcome aboard");

        (await Templates.ResetAsync("user.invited")).IsDefault.Should().BeTrue();
        (await Templates.GetAsync("user.invited")).Subject.Should().Be("You're invited to {{product}} by {{organization.name}}");
        // Three entries at the same instant of the test clock: the set is what matters, not their order.
        (await AuditedAsync()).Should().BeEquivalentTo(["email.template_changed", "user.invited", "email.template_reset"]);
        (await Templates.RecentLogAsync()).Should().HaveCount(2);
    }

    [Fact]
    public void Tokens_are_unguessable_and_names_are_split_the_way_a_sign_in_form_expects()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => InvitationService.NewToken()).ToList();
        tokens.Should().OnlyHaveUniqueItems().And.OnlyContain(t => t.Length >= 40 && !t.Contains('+') && !t.Contains('/') && !t.Contains('='));
        InvitationService.Hash("abc").Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        KeycloakAdminClient.SplitName("Asha Rao").Should().Be(("Asha", "Rao"));
        KeycloakAdminClient.SplitName("Madonna").Should().Be(("Madonna", ""));
        KeycloakAdminClient.SplitName("Jean Luc Picard").Should().Be(("Jean", "Luc Picard"));
        new KeycloakAdminOptions { Authority = "https://id.example/realms/desk" }.AdminBase.Should().Be("https://id.example/admin/realms/desk");
        new KeycloakAdminOptions { Authority = "https://id.example/realms/desk", ClientSecret = "x" }.IsConfigured.Should().BeTrue();
        new KeycloakAdminOptions { Authority = "https://id.example/realms/desk" }.IsConfigured.Should().BeFalse();
    }
}
