using Desk.Domain.Common;

namespace Desk.Domain.Identity;

public enum InvitationKind
{
    Staff = 1,
    Client = 2,
}

public enum InvitationPurpose
{
    /// <summary>A person who has no sign-in yet is asked to set a password and come in.</summary>
    Invite = 1,
    /// <summary>A person who has a sign-in asked for a new password.</summary>
    PasswordReset = 2,
}

/// <summary>
/// A one-time link that lets a person set their password: an invitation to a new user, or a
/// password reset for an existing one. The link itself is never stored, only a hash of its
/// token, so a copy of the database does not let anyone in.
///
/// One row per link. A link is spent when it is used, revoked when an administrator withdraws it,
/// and expired when its time runs out; a new invitation is a new row with a new link, which makes
/// the old one worthless.
/// </summary>
public class SignInInvitation : TenantEntity
{
    public InvitationKind Kind { get; set; }
    public InvitationPurpose Purpose { get; set; } = InvitationPurpose.Invite;

    /// <summary>The staff user, for a staff invitation.</summary>
    public Guid? AppUserId { get; set; }
    /// <summary>The client user, for a client invitation.</summary>
    public Guid? ClientUserId { get; set; }

    /// <summary>The address the link was sent to, as it stood when it was made.</summary>
    public required string Email { get; set; }
    /// <summary>SHA-256 of the token in the link, hex.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Who made it, for the e-mail and the audit trail. Null for a password reset the person asked for themselves.</summary>
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }

    /// <summary>How many times the link was mailed: the first send and every reminder or resend.</summary>
    public int SentCount { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    /// <summary>How many of the scheduled reminders have gone.</summary>
    public int RemindersSent { get; set; }
    /// <summary>When the "this has expired" notice went, so it goes once.</summary>
    public DateTimeOffset? ExpiryNoticeSentAt { get; set; }

    public bool IsConsumed => ConsumedAt is not null;
    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt <= now;
    public bool IsOpenAt(DateTimeOffset now) => !IsConsumed && !IsRevoked && !IsExpiredAt(now);
}

/// <summary>
/// An organization's own wording for one kind of e-mail. Only the templates an administrator has
/// changed are rows; every other template is the product's default, which lives in code.
/// </summary>
public class EmailTemplate : TenantEntity
{
    /// <summary>One of the keys in the catalogue (for example "user.invited").</summary>
    public required string Key { get; set; }
    public required string Subject { get; set; }
    /// <summary>Plain text with blank lines between paragraphs and {{placeholders}}.</summary>
    public required string Body { get; set; }
    /// <summary>The words on the button that carries the link, where the e-mail has one.</summary>
    public string? ButtonLabel { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public string? UpdatedByName { get; set; }
}

/// <summary>What was sent to whom, and whether it went. One row per message, kept for a while.</summary>
public class EmailLogEntry : TenantEntity
{
    public required string TemplateKey { get; set; }
    public required string To { get; set; }
    public required string Subject { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    /// <summary>What the message was about: "AppUser", "ClientUser", "SignInInvitation".</summary>
    public string? SubjectType { get; set; }
    public Guid? SubjectId { get; set; }
}
