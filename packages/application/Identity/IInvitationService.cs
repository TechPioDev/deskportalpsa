using Desk.Domain.Identity;

namespace Desk.Application.Identity;

/// <summary>
/// The identity provider's user store, as much of it as the portal needs: make a user with a
/// password, find one by e-mail, set a password. Nothing else is read or written there.
/// </summary>
public interface IKeycloakAdmin
{
    /// <summary>False where the server has no admin client configured; invitations can still be made and mailed, but not accepted.</summary>
    bool IsConfigured { get; }
    /// <summary>The identity provider's id for the user with this e-mail, or null.</summary>
    Task<string?> FindUserIdByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>Creates an enabled user with a verified e-mail and this password. Returns the identity provider's id.</summary>
    Task<string> CreateUserAsync(string email, string displayName, string password, CancellationToken ct = default);
    Task SetPasswordAsync(string userId, string password, CancellationToken ct = default);
}

/// <summary>Why a link cannot be used, said to the person holding it in words that give nothing else away.</summary>
public enum InvitationLinkState
{
    Open = 0,
    Used = 1,
    Expired = 2,
    Revoked = 3,
}

/// <summary>What the page at a link shows before anything is typed. No identifiers beyond what the person was mailed.</summary>
/// <remarks>Purpose, Kind and State are the enum names as words ("Invite", "Staff", "Open"), which is what a page compares against.</remarks>
public sealed record InvitationLookupDto(
    string Purpose, string Kind, string DisplayName, string Email, string OrganizationName,
    DateTimeOffset ExpiresAt, string State, bool CanBeAccepted);

/// <summary>After a link was used: who got in, and where sign-in continues.</summary>
public sealed record InvitationAcceptedDto(string Email, string Kind, string SignInPath);

/// <summary>One invitation as an administrator sees it.</summary>
public sealed record InvitationDto(
    Guid Id, string Kind, string Purpose, Guid? AppUserId, Guid? ClientUserId, string Email,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastSentAt, int SentCount, int RemindersSent,
    DateTimeOffset? ConsumedAt, DateTimeOffset? RevokedAt, string? CreatedByName, string State);

/// <summary>
/// An invitation that was just made: the link is in it ONCE, for the inviter to copy where mail
/// does not reach. It is not stored and cannot be shown again.
/// </summary>
public sealed record InvitationSentDto(InvitationDto Invitation, string Link, bool Mailed, string? MailError);

public sealed record InviteAllResultDto(int Invited, int Mailed, IReadOnlyList<string> Failed);

public sealed record SignInInvitationPolicy(TimeSpan Lifetime, IReadOnlyList<TimeSpan> RemindersAfter, int MinimumPasswordLength)
{
    /// <summary>Seven days to accept, reminded on the third and the sixth, twelve characters at least.</summary>
    public static readonly SignInInvitationPolicy Default = new(
        TimeSpan.FromDays(7), [TimeSpan.FromDays(3), TimeSpan.FromDays(6)], 12);
    /// <summary>A password reset is asked for and used at once; an hour is plenty.</summary>
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
}

public interface IInvitationService
{
    /// <summary>Makes a staff invitation and mails it. A second invitation for the same person replaces the first.</summary>
    Task<InvitationSentDto> InviteStaffAsync(Guid appUserId, CancellationToken ct = default);
    /// <summary>Invites every active staff user who has never signed in and has no open invitation.</summary>
    Task<InviteAllResultDto> InviteAllPendingStaffAsync(CancellationToken ct = default);
    /// <summary>Makes a client invitation and mails it. Called by whoever may manage that company's users; the caller has checked that.</summary>
    Task<InvitationSentDto> InviteClientAsync(Guid clientUserId, CancellationToken ct = default);
    /// <summary>Withdraws an open invitation. The link in the mail stops working.</summary>
    Task RevokeAsync(Guid invitationId, CancellationToken ct = default);
    /// <summary>Every invitation of the organization, newest first; password resets are not listed.</summary>
    Task<IReadOnlyList<InvitationDto>> ListAsync(CancellationToken ct = default);

    /// <summary>What the page at a link shows. Null where there is no such link, which reads the same as a revoked one.</summary>
    Task<InvitationLookupDto?> LookupAsync(string token, CancellationToken ct = default);
    /// <summary>Uses a link: sets the password at the identity provider, links the sign-in, spends the link.</summary>
    Task<InvitationAcceptedDto> AcceptAsync(string token, string password, CancellationToken ct = default);

    /// <summary>
    /// Mails a password-reset link to this address if a person who can sign in has it. Says nothing
    /// either way: the answer is the same for an address nobody has.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken ct = default);

    /// <summary>The worker's turn: reminders that are due, and expiry notices. Returns how many mails went.</summary>
    Task<int> SendRemindersAsync(CancellationToken ct = default);
}

/// <summary>
/// The organization's e-mail wording. The defaults are the product's; an administrator may change
/// each, see it rendered, send it to themselves, and put the default back.
/// </summary>
public interface IEmailTemplateService
{
    Task<IReadOnlyList<EmailTemplateDto>> ListAsync(CancellationToken ct = default);
    Task<EmailTemplateDto> GetAsync(string key, CancellationToken ct = default);
    Task<EmailTemplateDto> SaveAsync(string key, EmailTemplateInput input, CancellationToken ct = default);
    Task<EmailTemplateDto> ResetAsync(string key, CancellationToken ct = default);
    /// <summary>The template as it would be sent, with sample values, without sending it.</summary>
    Task<RenderedEmailDto> PreviewAsync(string key, EmailTemplateInput? unsaved = null, CancellationToken ct = default);
    /// <summary>Sends the template with sample values to the signed-in administrator.</summary>
    Task<RenderedEmailDto> SendTestAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<EmailLogDto>> RecentLogAsync(int take = 100, CancellationToken ct = default);
}

public sealed record EmailTemplateDto(
    string Key, string Name, string Description, string Subject, string Body, string? ButtonLabel, bool HasLink,
    IReadOnlyList<string> Placeholders, bool IsDefault, string? UpdatedByName, DateTimeOffset? UpdatedAt);

public sealed record EmailTemplateInput(string Subject, string Body, string? ButtonLabel);

public sealed record RenderedEmailDto(string To, string Subject, string TextBody, string HtmlBody);

public sealed record EmailLogDto(
    Guid Id, string TemplateKey, string To, string Subject, DateTimeOffset SentAt, bool Succeeded, string? Error);
