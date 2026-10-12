using System.Security.Cryptography;
using System.Text;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Identity;
using Desk.Domain.Common;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Identity;

/// <summary>
/// Invitations and password resets: a one-time link, mailed, that lets a person set their
/// password at the identity provider and come in.
///
/// The portal is the keeper of the link and its wording; the identity provider only receives the
/// user and the password at the end. A link is a random token whose hash is stored; the token
/// itself goes in the mail once and, for an invitation, is shown to the inviter once to copy.
///
/// What a link's page says before anything is typed gives away no more than the mail did: the
/// person's name, the organization, when it expires. An unknown link and a revoked one answer the
/// same. Asking for a password reset answers the same for an address nobody has.
/// </summary>
public sealed class InvitationService(
    DeskDbContext db, ISettableTenantContext tenant, ICurrentUser user, IKeycloakAdmin keycloak, TemplateMailer mail,
    IAuditWriter audit, PortalOptions portal, TimeProvider clock, SignInInvitationPolicy? policy = null) : IInvitationService
{
    private readonly SignInInvitationPolicy _policy = policy ?? SignInInvitationPolicy.Default;

    // ---- making ------------------------------------------------------------------------------

    public async Task<InvitationSentDto> InviteStaffAsync(Guid appUserId, CancellationToken ct = default)
    {
        var person = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == appUserId, ct) ?? throw new NotFoundException("User");
        if (!person.IsActive) throw new ValidationFailedException("This user is deactivated. Activate them first.");
        if (person.IdpSubject is not null) throw new ValidationFailedException("This person already signs in. Send them a password reset instead.");
        return await MakeAndSendAsync(InvitationKind.Staff, person.Id, null, person.Email, person.DisplayName, ct);
    }

    public async Task<InviteAllResultDto> InviteAllPendingStaffAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var open = await db.SignInInvitations
            .Where(i => i.Kind == InvitationKind.Staff && i.Purpose == InvitationPurpose.Invite && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => i.AppUserId).ToListAsync(ct);
        var pending = await db.AppUsers
            .Where(u => u.IsActive && u.IdpSubject == null && !open.Contains(u.Id))
            .OrderBy(u => u.DisplayName).ToListAsync(ct);
        var mailed = 0;
        var failed = new List<string>();
        foreach (var person in pending)
        {
            var sent = await MakeAndSendAsync(InvitationKind.Staff, person.Id, null, person.Email, person.DisplayName, ct);
            if (sent.Mailed) mailed++; else failed.Add($"{person.Email}: {sent.MailError}");
        }
        return new InviteAllResultDto(pending.Count, mailed, failed);
    }

    public async Task<InvitationSentDto> InviteClientAsync(Guid clientUserId, CancellationToken ct = default)
    {
        var person = await db.ClientUsers.FirstOrDefaultAsync(u => u.Id == clientUserId, ct) ?? throw new NotFoundException("User");
        if (!person.IsActive) throw new ValidationFailedException("This user is deactivated. Activate them first.");
        if (person.IdpSubject is not null) throw new ValidationFailedException("This person already signs in. Send them a password reset instead.");
        return await MakeAndSendAsync(InvitationKind.Client, null, person.Id, person.Email, person.DisplayName, ct);
    }

    private async Task<InvitationSentDto> MakeAndSendAsync(
        InvitationKind kind, Guid? appUserId, Guid? clientUserId, string email, string displayName, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var orgId = tenant.OrganizationId ?? throw new InvalidOperationException("An invitation is made inside an organization.");
        // An earlier open invitation to the same person is revoked: one live link per person.
        var earlier = await db.SignInInvitations
            .Where(i => i.Purpose == InvitationPurpose.Invite && i.ConsumedAt == null && i.RevokedAt == null
                && (appUserId != null ? i.AppUserId == appUserId : i.ClientUserId == clientUserId))
            .ToListAsync(ct);
        foreach (var e in earlier) e.RevokedAt = now;

        var token = NewToken();
        var invitation = new SignInInvitation
        {
            MspOrganizationId = orgId, Kind = kind, Purpose = InvitationPurpose.Invite, AppUserId = appUserId, ClientUserId = clientUserId,
            Email = email, TokenHash = Hash(token), ExpiresAt = now + _policy.Lifetime,
            CreatedByUserId = user.UserId, CreatedByName = user.DisplayName, SentCount = 0,
        };
        db.SignInInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        var link = portal.Link("invite/" + token);
        var (mailed, error) = await mail.SendAsync(orgId, EmailTemplates.UserInvited, email,
            await ValuesAsync(displayName, email, link, invitation.ExpiresAt, invitation.CreatedByName, ct),
            nameof(SignInInvitation), invitation.Id, ct);
        if (mailed) { invitation.SentCount = 1; invitation.LastSentAt = now; await db.SaveChangesAsync(ct); }

        await audit.WriteAsync("user.invited", kind == InvitationKind.Staff ? "AppUser" : "ClientUser", (appUserId ?? clientUserId).ToString(),
            new { invitationId = invitation.Id, email, expiresAt = invitation.ExpiresAt, mailed, error, replaced = earlier.Count }, ct);
        return new InvitationSentDto(ToDto(invitation, now), link, mailed, error);
    }

    public async Task RevokeAsync(Guid invitationId, CancellationToken ct = default)
    {
        var invitation = await db.SignInInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct) ?? throw new NotFoundException("Invitation");
        if (invitation.ConsumedAt is not null) throw new ValidationFailedException("This invitation was already used.");
        if (invitation.RevokedAt is not null) return;
        invitation.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("user.invitation_revoked", nameof(SignInInvitation), invitation.Id.ToString(), new { invitation.Email }, ct);
    }

    public async Task<IReadOnlyList<InvitationDto>> ListAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var rows = await db.SignInInvitations.AsNoTracking()
            .Where(i => i.Purpose == InvitationPurpose.Invite)
            .OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(r => ToDto(r, now)).ToList();
    }

    // ---- using -------------------------------------------------------------------------------

    public async Task<InvitationLookupDto?> LookupAsync(string token, CancellationToken ct = default)
    {
        var invitation = await FindByTokenAsync(token, ct);
        // A withdrawn link reads exactly like one that never existed: nothing to learn by probing.
        if (invitation is null || invitation.RevokedAt is not null) return null;
        var now = clock.GetUtcNow();
        var (name, _) = await PersonAsync(invitation, ct);
        var org = await db.Set<MspOrganization>().AsNoTracking().FirstAsync(o => o.Id == invitation.MspOrganizationId, ct);
        var state = StateOf(invitation, now);
        return new InvitationLookupDto(invitation.Purpose.ToString(), invitation.Kind.ToString(), name, invitation.Email, org.Name, invitation.ExpiresAt,
            state.ToString(), state == InvitationLinkState.Open && keycloak.IsConfigured);
    }

    public async Task<InvitationAcceptedDto> AcceptAsync(string token, string password, CancellationToken ct = default)
    {
        var invitation = await FindByTokenAsync(token, ct) ?? throw new NotFoundException("Invitation");
        var now = clock.GetUtcNow();
        switch (StateOf(invitation, now))
        {
            case InvitationLinkState.Used: throw new ValidationFailedException("This link has already been used. Sign in with the password you chose, or ask for a password reset.");
            case InvitationLinkState.Expired: throw new ValidationFailedException("This link has expired. Ask your administrator for a new invitation.");
            case InvitationLinkState.Revoked: throw new NotFoundException("Invitation");
        }
        CheckPassword(password, invitation.Email);

        var (name, _) = await PersonAsync(invitation, ct);
        string subject;
        if (invitation.Purpose == InvitationPurpose.PasswordReset)
        {
            subject = await keycloak.FindUserIdByEmailAsync(invitation.Email, ct)
                ?? throw new ValidationFailedException("There is no sign-in for this address any more. Ask your administrator for an invitation.");
            await keycloak.SetPasswordAsync(subject, password, ct);
        }
        else
        {
            subject = await keycloak.CreateUserAsync(invitation.Email, name, password, ct);
        }

        // The link is spent in the same save that records the sign-in, so neither happens alone.
        if (invitation.AppUserId is { } staffId)
        {
            var person = await db.AppUsers.FirstAsync(u => u.Id == staffId, ct);
            person.IdpSubject ??= subject;
        }
        else if (invitation.ClientUserId is { } clientId)
        {
            var person = await db.ClientUsers.FirstAsync(u => u.Id == clientId, ct);
            person.IdpSubject ??= subject;
        }
        invitation.ConsumedAt = now;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(invitation.Purpose == InvitationPurpose.Invite ? "user.invitation_accepted" : "user.password_reset",
            invitation.Kind == InvitationKind.Staff ? "AppUser" : "ClientUser", (invitation.AppUserId ?? invitation.ClientUserId).ToString(),
            new { invitationId = invitation.Id, invitation.Email }, ct);

        if (invitation.Purpose == InvitationPurpose.Invite)
            await mail.SendAsync(invitation.MspOrganizationId, EmailTemplates.UserWelcome, invitation.Email,
                await ValuesAsync(name, invitation.Email, portal.Link("dashboard"), null, null, ct), nameof(SignInInvitation), invitation.Id, ct);

        return new InvitationAcceptedDto(invitation.Email, invitation.Kind.ToString(), "/api/auth/login?login_hint=" + Uri.EscapeDataString(invitation.Email));
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@')) return;
        var now = clock.GetUtcNow();
        // Nobody is signed in here: the address decides whom, and only a person who can already
        // sign in gets a reset. An address nobody has gets nothing, silently.
        var staff = await db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.IsActive && u.IdpSubject != null && u.Email.ToLower() == email.ToLower())
            .Select(u => new { u.Id, Org = u.MspOrganizationId, u.DisplayName, u.Email, Kind = InvitationKind.Staff }).ToListAsync(ct);
        var clients = await db.ClientUsers.IgnoreQueryFilters()
            .Where(u => u.IsActive && u.IdpSubject != null && u.Email.ToLower() == email.ToLower())
            .Select(u => new { u.Id, Org = (Guid?)u.MspOrganizationId, u.DisplayName, u.Email, Kind = InvitationKind.Client }).ToListAsync(ct);
        var people = staff.Concat(clients).Where(p => p.Org != null).Select(p => (p.Id, Org: p.Org!.Value, p.DisplayName, p.Email, p.Kind)).ToList();
        if (people.Count == 0) return;
        if (!tenant.HasScope) tenant.SetTenant(people[0].Org);

        foreach (var person in people)
        {
            // Any earlier reset for the same person stops working: one live link per person.
            var earlier = await db.SignInInvitations.IgnoreQueryFilters()
                .Where(i => i.Purpose == InvitationPurpose.PasswordReset && i.ConsumedAt == null && i.RevokedAt == null
                    && (person.Kind == InvitationKind.Staff ? i.AppUserId == person.Id : i.ClientUserId == person.Id)).ToListAsync(ct);
            foreach (var e in earlier) e.RevokedAt = now;
            var token = NewToken();
            var reset = new SignInInvitation
            {
                MspOrganizationId = person.Org, Kind = person.Kind, Purpose = InvitationPurpose.PasswordReset,
                AppUserId = person.Kind == InvitationKind.Staff ? person.Id : null, ClientUserId = person.Kind == InvitationKind.Client ? person.Id : null,
                Email = person.Email, TokenHash = Hash(token), ExpiresAt = now + SignInInvitationPolicy.ResetLifetime,
            };
            db.SignInInvitations.Add(reset);
            await db.SaveChangesAsync(ct);
            var (mailed, _) = await mail.SendAsync(person.Org, EmailTemplates.PasswordReset, person.Email,
                await ValuesAsync(person.DisplayName, person.Email, portal.Link("reset-password/" + token), reset.ExpiresAt, null, ct),
                nameof(SignInInvitation), reset.Id, ct);
            if (mailed) { reset.SentCount = 1; reset.LastSentAt = now; await db.SaveChangesAsync(ct); }
        }
    }

    // ---- the worker's turn -------------------------------------------------------------------

    public async Task<int> SendRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var sent = 0;
        var open = await db.SignInInvitations
            .Where(i => i.Purpose == InvitationPurpose.Invite && i.ConsumedAt == null && i.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var invitation in open)
        {
            if (invitation.ExpiresAt <= now)
            {
                if (invitation.ExpiryNoticeSentAt is not null) continue;
                var (name, _) = await PersonAsync(invitation, ct);
                await mail.SendAsync(invitation.MspOrganizationId, EmailTemplates.InviteExpired, invitation.Email,
                    await ValuesAsync(name, invitation.Email, null, invitation.ExpiresAt, invitation.CreatedByName, ct), nameof(SignInInvitation), invitation.Id, ct);
                invitation.ExpiryNoticeSentAt = now;
                sent++;
                continue;
            }
            if (invitation.RemindersSent >= _policy.RemindersAfter.Count) continue;
            if (invitation.CreatedAt + _policy.RemindersAfter[invitation.RemindersSent] > now) continue;
            // The same link again: it is still good, and a second link would make the first one a dead end.
            var token = NewToken();
            invitation.TokenHash = Hash(token);
            var (who, _) = await PersonAsync(invitation, ct);
            var (mailed, _) = await mail.SendAsync(invitation.MspOrganizationId, EmailTemplates.InviteReminder, invitation.Email,
                await ValuesAsync(who, invitation.Email, portal.Link("invite/" + token), invitation.ExpiresAt, invitation.CreatedByName, ct),
                nameof(SignInInvitation), invitation.Id, ct);
            invitation.RemindersSent++;
            if (mailed) { invitation.SentCount++; invitation.LastSentAt = now; sent++; }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    // ---- pieces ------------------------------------------------------------------------------

    private async Task<SignInInvitation?> FindByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var hash = Hash(token.Trim());
        // Nobody is signed in: the link decides the organization, as an alert key does.
        var invitation = await db.SignInInvitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null) return null;
        if (tenant.HasScope)
        {
            if (tenant.OrganizationId != invitation.MspOrganizationId) return null;
        }
        else tenant.SetTenant(invitation.MspOrganizationId);
        return invitation;
    }

    private async Task<(string Name, bool Active)> PersonAsync(SignInInvitation invitation, CancellationToken ct)
    {
        if (invitation.AppUserId is { } staffId)
        {
            var p = await db.AppUsers.AsNoTracking().Where(u => u.Id == staffId).Select(u => new { u.DisplayName, u.IsActive }).FirstOrDefaultAsync(ct);
            return (p?.DisplayName ?? invitation.Email, p?.IsActive ?? false);
        }
        if (invitation.ClientUserId is { } clientId)
        {
            var p = await db.ClientUsers.AsNoTracking().Where(u => u.Id == clientId).Select(u => new { u.DisplayName, u.IsActive }).FirstOrDefaultAsync(ct);
            return (p?.DisplayName ?? invitation.Email, p?.IsActive ?? false);
        }
        return (invitation.Email, false);
    }

    private async Task<IReadOnlyDictionary<string, string>> ValuesAsync(
        string displayName, string email, string? link, DateTimeOffset? expiresAt, string? invitedBy, CancellationToken ct)
    {
        var org = await db.Set<MspOrganization>().AsNoTracking()
            .Where(o => o.Id == tenant.OrganizationId).Select(o => new { o.Name, o.TimeZone }).FirstOrDefaultAsync(ct);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user.name"] = displayName, ["user.first_name"] = FirstName(displayName), ["user.email"] = email,
            ["organization.name"] = org?.Name ?? EmailTemplates.Product, ["product"] = EmailTemplates.Product, ["product_url"] = portal.PublicUrl,
            ["invited_by"] = string.IsNullOrWhiteSpace(invitedBy) ? "your administrator" : invitedBy,
        };
        if (link is not null) values["link"] = link;
        if (expiresAt is { } when) values["expires"] = Local(when, org?.TimeZone);
        return values;
    }

    private static string Local(DateTimeOffset when, string? timeZone)
    {
        var zone = TimeZones.Resolve(timeZone ?? "UTC");
        var local = TimeZoneInfo.ConvertTime(when, zone);
        return local.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string FirstName(string displayName)
        => (displayName ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private void CheckPassword(string password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _policy.MinimumPasswordLength)
            throw new ValidationFailedException($"Choose a password of at least {_policy.MinimumPasswordLength} characters.");
        if (password.Length > 200) throw new ValidationFailedException("That password is too long.");
        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ValidationFailedException("The password cannot be your e-mail address.");
        if (password.Distinct().Count() < 4) throw new ValidationFailedException("Choose a password with more variety in it.");
    }

    private static InvitationLinkState StateOf(SignInInvitation i, DateTimeOffset now)
        => i.ConsumedAt is not null ? InvitationLinkState.Used
            : i.RevokedAt is not null ? InvitationLinkState.Revoked
            : i.ExpiresAt <= now ? InvitationLinkState.Expired
            : InvitationLinkState.Open;

    private static InvitationDto ToDto(SignInInvitation i, DateTimeOffset now)
        => new(i.Id, i.Kind.ToString(), i.Purpose.ToString(), i.AppUserId, i.ClientUserId, i.Email, i.CreatedAt, i.ExpiresAt, i.LastSentAt, i.SentCount, i.RemindersSent,
            i.ConsumedAt, i.RevokedAt, i.CreatedByName, StateOf(i, now) switch
            {
                InvitationLinkState.Used => "Accepted", InvitationLinkState.Revoked => "Revoked", InvitationLinkState.Expired => "Expired", _ => "Sent",
            });

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
