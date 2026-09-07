using Desk.Domain.Marketing;

namespace Desk.Application.Marketing;

/// <param name="Website">Honeypot. A real browser leaves it empty because it is hidden; bots fill
/// every field they find. Non-empty means the submission is accepted with a normal response and
/// then dropped, so the bot learns nothing from being blocked.</param>
public sealed record SubmitEnquiryInput(
    EnquiryKind Kind,
    string Name,
    string Email,
    string? Company,
    string? Phone,
    string Message,
    string? PreferredTime,
    string? SourcePage,
    string? Website);

/// <summary>
/// Why a submission was refused, in terms the public form can put in front of a visitor.
/// Deliberately coarse: an anonymous caller should learn enough to correct the form and nothing
/// about what is stored behind it.
/// </summary>
public enum EnquiryRefusal
{
    /// <summary>Stored — or a tripped honeypot, which is answered as though it were stored.</summary>
    None = 0,

    /// <summary>A required field was blank, or the email address was not usable.</summary>
    Incomplete = 1,

    /// <summary>A field was longer than its limit. <see cref="SubmitEnquiryResult.Field"/> names
    /// which, so the visitor is not left to guess which of six fields to shorten.</summary>
    TooLong = 2,
}

/// <param name="Field">Human wording for the offending field ("message", "phone number"), not the
/// property name — it goes straight into a sentence a visitor reads.</param>
/// <param name="Limit">The limit that field exceeded, so the message can state the number.</param>
public sealed record SubmitEnquiryResult(EnquiryRefusal Refusal, string? Field = null, int Limit = 0)
{
    public bool Accepted => Refusal == EnquiryRefusal.None;

    public static readonly SubmitEnquiryResult Ok = new(EnquiryRefusal.None);
    public static readonly SubmitEnquiryResult Incomplete = new(EnquiryRefusal.Incomplete);

    public static SubmitEnquiryResult TooLong(string field, int limit) =>
        new(EnquiryRefusal.TooLong, field, limit);
}

public sealed record EnquiryDto(
    Guid Id,
    EnquiryKind Kind,
    string Name,
    string Email,
    string? Company,
    string? Phone,
    string Message,
    string? PreferredTime,
    EnquiryStatus Status,
    string? SourcePage,
    DateTimeOffset CreatedAt);

public sealed record EnquiryListResult(int Total, int NewCount, IReadOnlyList<EnquiryDto> Items);

public interface IEnquiryService
{
    /// <summary>
    /// Anonymous submission from the public site. Validates, then stores. Nothing is written when
    /// the result is a refusal, so a caller that fixes the named field and sends again creates one
    /// enquiry rather than two.
    /// </summary>
    Task<SubmitEnquiryResult> SubmitAsync(SubmitEnquiryInput input, CancellationToken ct = default);

    Task<EnquiryListResult> ListAsync(EnquiryStatus? status = null, CancellationToken ct = default);

    Task<bool> SetStatusAsync(Guid id, EnquiryStatus status, CancellationToken ct = default);
}
