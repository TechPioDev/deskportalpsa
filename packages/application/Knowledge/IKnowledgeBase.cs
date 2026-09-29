using Desk.Application.Tickets;

namespace Desk.Application.Knowledge;

// ---- Staff ----

public sealed record KbArticleSummaryDto(
    Guid Id, string Title, string? Category, string Audience, IReadOnlyList<string> ClientNames,
    bool IsPublished, DateTimeOffset UpdatedAt, string? UpdatedByName,
    /// <summary>How many times a client said this article solved their problem.</summary>
    int Solved);

public sealed record KbArticleDto(
    Guid Id, string Title, string Body, string? Category, string Audience, IReadOnlyList<Guid> ClientIds,
    bool IsPublished, string? AuthorName, string? UpdatedByName, DateTimeOffset UpdatedAt);

public sealed record KbArticleInput(
    string Title, string? Body, string? Category, string Audience, IReadOnlyList<Guid>? ClientIds, bool IsPublished);

/// <summary>What the articles did for the desk over a period.</summary>
public sealed record KbStatsDto(
    int Days,
    /// <summary>Tickets a client did not raise because an article solved it.</summary>
    int TicketsAvoided,
    IReadOnlyList<KbTopArticleDto> TopArticles,
    /// <summary>The words clients typed before an article helped, newest first.</summary>
    IReadOnlyList<KbRecentDeflectionDto> Recent);

public sealed record KbTopArticleDto(string Source, Guid Id, string Title, int Solved);

public sealed record KbRecentDeflectionDto(string Title, string? Query, string? ClientName, DateTimeOffset OccurredAt);

// ---- Client ----

/// <summary>An article as a client's Help page lists it: either list, one shape.</summary>
public sealed record HelpArticleSummaryDto(string Source, Guid Id, string Title, string? Category, string Excerpt);

public sealed record HelpArticleDto(string Source, Guid Id, string Title, string Body, string? Category, DateTimeOffset UpdatedAt);

public interface IKnowledgeBaseService
{
    Task<IReadOnlyList<KbArticleSummaryDto>> ListAsync(string? search, CancellationToken ct = default);
    Task<KbArticleDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<KbArticleDto> SaveAsync(Guid? id, KbArticleInput input, Guid appUserId, string authorName, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<KbStatsDto> StatsAsync(int days, CancellationToken ct = default);

    Task<IReadOnlyList<HelpArticleSummaryDto>> HelpAsync(ClientAccess access, string? search, CancellationToken ct = default);
    Task<HelpArticleDto> HelpArticleAsync(ClientAccess access, string source, Guid id, CancellationToken ct = default);

    /// <summary>The few articles most likely to answer a ticket being typed. Empty for too few words to go on.</summary>
    Task<IReadOnlyList<HelpArticleSummaryDto>> SuggestAsync(ClientAccess access, string? query, CancellationToken ct = default);

    /// <summary>Records that an article solved the client's problem, so no ticket was raised.</summary>
    Task SolvedAsync(ClientAccess access, string source, Guid id, string? query, CancellationToken ct = default);
}
