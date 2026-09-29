namespace Desk.Application.Tickets;

/// <summary>
/// What the client sees under a finished ticket: whether they can rate it, why not when they cannot,
/// and their current answer.
/// </summary>
public sealed record SatisfactionStateDto(
    bool CanRate, string? Reason, int? Rating, string? Comment, DateTimeOffset? RatedAt, DateTimeOffset? OpenUntil);

/// <summary>A rating as staff see it on the ticket.</summary>
public sealed record TicketRatingDto(int Rating, string? Comment, DateTimeOffset RatedAt, string? RatedBy, string? TechnicianName);

public sealed record SatisfactionGroupDto(string Key, string Name, int Ratings, int Satisfied, double? CsatPct, double Average);

public sealed record SatisfactionCommentDto(
    Guid TicketId, string Reference, string Title, int Rating, string? Comment,
    string? ClientName, string? TechnicianName, DateTimeOffset RatedAt);

/// <summary>
/// Satisfaction over a period. CSAT is the share of ratings that are 4 or 5; null, never 0% or 100%,
/// when nobody rated anything.
/// </summary>
public sealed record SatisfactionSummaryDto(
    int Ratings, int Satisfied, double? CsatPct, double? Average,
    /// <summary>How many ratings of 1, 2, 3, 4 and 5, in that order.</summary>
    IReadOnlyList<int> Distribution,
    IReadOnlyList<SatisfactionGroupDto> ByTechnician,
    IReadOnlyList<SatisfactionGroupDto> ByClient,
    /// <summary>The latest ratings that came with a comment, and every poor one whether or not it did.</summary>
    IReadOnlyList<SatisfactionCommentDto> Recent);

public interface ISatisfactionService
{
    Task<SatisfactionStateDto> StateAsync(ClientAccess access, Guid ticketId, CancellationToken ct = default);
    Task<SatisfactionStateDto> RateAsync(ClientAccess access, Guid ticketId, int rating, string? comment, CancellationToken ct = default);
    Task<SatisfactionSummaryDto> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
