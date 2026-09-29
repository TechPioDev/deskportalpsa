using System.Text.RegularExpressions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Knowledge;
using Desk.Application.Tickets;
using Desk.Domain.Knowledge;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Knowledge;

/// <summary>
/// The knowledge base: articles the team writes, for itself or for clients, read beside each client's
/// own FAQ; suggested while a client types a ticket; and counted when one solves the problem instead.
///
/// Search is plain word matching over titles and bodies, done in memory over the articles the reader
/// may see. An MSP's knowledge base is hundreds of articles, not millions, and this way the one rule
/// that matters - who may read what - is applied once, before any matching, and cannot be bypassed by
/// a clever query.
/// </summary>
public sealed partial class KnowledgeBaseService(DeskDbContext db, IAuditWriter audit, TimeProvider clock) : IKnowledgeBaseService
{
    private const int SuggestionCount = 5;

    // ---- Staff ----

    public async Task<IReadOnlyList<KbArticleSummaryDto>> ListAsync(string? search, CancellationToken ct = default)
    {
        var articles = await db.KbArticles.AsNoTracking().Include(a => a.Clients).ToListAsync(ct);
        var names = await ClientNamesAsync(ct);
        var solved = await db.KbDeflections.AsNoTracking()
            .Where(d => d.Source == KbSource.Team)
            .GroupBy(d => d.ArticleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var terms = Terms(search);
        return [.. articles
            .Where(a => terms.Count == 0 || terms.All(t => Contains(a.Title, t) || Contains(a.Body, t) || Contains(a.Category, t)))
            .OrderBy(a => a.Category ?? "~").ThenBy(a => a.Title)
            .Select(a => new KbArticleSummaryDto(a.Id, a.Title, a.Category, a.Audience.ToString(),
                [.. a.Clients.Select(c => names.GetValueOrDefault(c.ClientCompanyId, "Unknown client")).OrderBy(n => n)],
                a.IsPublished, a.UpdatedAt, a.UpdatedByName ?? a.AuthorName, solved.GetValueOrDefault(a.Id)))];
    }

    public async Task<KbArticleDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var a = await db.KbArticles.AsNoTracking().Include(x => x.Clients).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Article");
        return Dto(a);
    }

    public async Task<KbArticleDto> SaveAsync(Guid? id, KbArticleInput input, Guid appUserId, string authorName, CancellationToken ct = default)
    {
        var title = Clean(input.Title, KbArticle.MaxTitleLength, "the title") ?? throw new ValidationFailedException("Give the article a title.");
        var body = Clean(input.Body, KbArticle.MaxBodyLength, "the article") ?? "";
        var category = Clean(input.Category, KbArticle.MaxCategoryLength, "the category");
        if (!Enum.TryParse<KbAudience>(input.Audience, true, out var audience) || !Enum.IsDefined(audience))
            throw new ValidationFailedException("Choose who can read it: staff only, all clients, or chosen clients.");

        var clientIds = (input.ClientIds ?? []).Distinct().ToList();
        if (audience == KbAudience.SelectedClients)
        {
            if (clientIds.Count == 0) throw new ValidationFailedException("Choose at least one client to show it to.");
            // Only this organization's clients; an id from anywhere else is simply not one of them.
            var known = await db.ClientCompanies.Where(c => clientIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
            if (known.Count != clientIds.Count) throw new ValidationFailedException("One of the chosen clients was not found.");
        }
        else
        {
            clientIds = [];
        }

        KbArticle article;
        if (id is { } existing)
        {
            article = await db.KbArticles.Include(a => a.Clients).FirstOrDefaultAsync(a => a.Id == existing, ct)
                ?? throw new NotFoundException("Article");
            article.UpdatedByName = authorName;
        }
        else
        {
            article = new KbArticle { Title = title, AuthorAppUserId = appUserId, AuthorName = authorName };
            db.KbArticles.Add(article);
        }

        article.Title = title;
        article.Body = body;
        article.Category = category;
        article.Audience = audience;
        article.IsPublished = input.IsPublished;
        db.KbArticleClients.RemoveRange(article.Clients.Where(c => !clientIds.Contains(c.ClientCompanyId)).ToList());
        foreach (var clientId in clientIds.Where(c => article.Clients.All(x => x.ClientCompanyId != c)))
            article.Clients.Add(new KbArticleClient { ArticleId = article.Id, ClientCompanyId = clientId });

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("kb.article.save", nameof(KbArticle), article.Id.ToString(),
            new { article.Title, audience = audience.ToString(), article.IsPublished }, ct);
        return Dto(article);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var article = await db.KbArticles.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Article");
        db.KbArticles.Remove(article);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("kb.article.delete", nameof(KbArticle), id.ToString(), new { article.Title }, ct);
    }

    public async Task<KbStatsDto> StatsAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var since = clock.GetUtcNow().AddDays(-days);
        var rows = await db.KbDeflections.AsNoTracking()
            .Where(d => d.OccurredAt >= since)
            .Select(d => new { d.Source, d.ArticleId, d.Query, d.OccurredAt, d.ClientCompanyId })
            .ToListAsync(ct);
        var titles = await TitlesAsync(rows.Select(r => (r.Source, r.ArticleId)), ct);
        var names = await ClientNamesAsync(ct);

        var top = rows.GroupBy(r => (r.Source, r.ArticleId))
            .Select(g => new KbTopArticleDto(g.Key.Source.ToString(), g.Key.ArticleId, titles.GetValueOrDefault(g.Key, "A deleted article"), g.Count()))
            .OrderByDescending(t => t.Solved).ThenBy(t => t.Title).Take(10).ToList();
        var recent = rows.OrderByDescending(r => r.OccurredAt).Take(20)
            .Select(r => new KbRecentDeflectionDto(titles.GetValueOrDefault((r.Source, r.ArticleId), "A deleted article"), r.Query,
                names.GetValueOrDefault(r.ClientCompanyId), r.OccurredAt))
            .ToList();
        return new KbStatsDto(days, rows.Count, top, recent);
    }

    // ---- Client ----

    public async Task<IReadOnlyList<HelpArticleSummaryDto>> HelpAsync(ClientAccess access, string? search, CancellationToken ct = default)
    {
        var terms = Terms(search);
        return [.. (await ReadableAsync(access, ct))
            .Where(a => terms.Count == 0 || terms.All(t => Contains(a.Title, t) || Contains(a.Body, t) || Contains(a.Category, t)))
            .OrderBy(a => a.Category ?? "~").ThenBy(a => a.Title)
            .Select(Summary)];
    }

    public async Task<HelpArticleDto> HelpArticleAsync(ClientAccess access, string source, Guid id, CancellationToken ct = default)
    {
        var kind = ParseSource(source);
        var article = (await ReadableAsync(access, ct)).FirstOrDefault(a => a.Source == kind && a.Id == id)
            ?? throw new NotFoundException("Article");
        return new HelpArticleDto(article.Source.ToString(), article.Id, article.Title, article.Body, article.Category, article.UpdatedAt);
    }

    public async Task<IReadOnlyList<HelpArticleSummaryDto>> SuggestAsync(ClientAccess access, string? query, CancellationToken ct = default)
    {
        // Suggestions look for any of the words, ranked by how many - and a title match counts more
        // than a body match, because "VPN" in the title is an article about the VPN, while "VPN" in
        // the body might be an aside in an article about printers.
        var terms = Terms(query).Where(t => !StopWords.Contains(t)).ToList();
        if (terms.Count == 0) return [];
        return [.. (await ReadableAsync(access, ct))
            .Select(a => (Article: a, Score: terms.Sum(t => (Contains(a.Title, t) ? 3 : 0) + (Contains(a.Body, t) || Contains(a.Category, t) ? 1 : 0))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Article.Title)
            .Take(SuggestionCount)
            .Select(x => Summary(x.Article))];
    }

    public async Task SolvedAsync(ClientAccess access, string source, Guid id, string? query, CancellationToken ct = default)
    {
        var kind = ParseSource(source);
        // Only an article this client could actually have read; anything else is not found.
        if (!(await ReadableAsync(access, ct)).Any(a => a.Source == kind && a.Id == id))
            throw new NotFoundException("Article");
        db.KbDeflections.Add(new KbDeflection
        {
            ClientCompanyId = access.ClientCompanyId,
            ClientUserId = access.ClientUserId,
            Source = kind,
            ArticleId = id,
            Query = Clean(query, 200, "the title", truncate: true),
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    // ---- Shared ----

    private sealed record Readable(KbSource Source, Guid Id, string Title, string Body, string? Category, DateTimeOffset UpdatedAt);

    /// <summary>
    /// Every article this client user may read: the team's published articles for all clients or for
    /// this one, and this company's own published FAQ. Staff-only articles and drafts never appear,
    /// whatever the search.
    /// </summary>
    private async Task<List<Readable>> ReadableAsync(ClientAccess access, CancellationToken ct)
    {
        var company = access.ClientCompanyId;
        var team = await db.KbArticles.AsNoTracking()
            .Where(a => a.IsPublished
                        && (a.Audience == KbAudience.AllClients
                            || (a.Audience == KbAudience.SelectedClients && a.Clients.Any(c => c.ClientCompanyId == company))))
            .Select(a => new Readable(KbSource.Team, a.Id, a.Title, a.Body, a.Category, a.UpdatedAt))
            .ToListAsync(ct);
        var own = await db.FaqArticles.AsNoTracking()
            .Where(f => f.ClientCompanyId == company && f.IsPublished)
            .Select(f => new Readable(KbSource.ClientFaq, f.Id, f.Question, f.Answer, f.Category, f.UpdatedAt))
            .ToListAsync(ct);
        return [.. team, .. own];
    }

    private async Task<Dictionary<(KbSource, Guid), string>> TitlesAsync(IEnumerable<(KbSource Source, Guid Id)> keys, CancellationToken ct)
    {
        var list = keys.Distinct().ToList();
        var teamIds = list.Where(k => k.Source == KbSource.Team).Select(k => k.Id).ToList();
        var faqIds = list.Where(k => k.Source == KbSource.ClientFaq).Select(k => k.Id).ToList();
        var titles = new Dictionary<(KbSource, Guid), string>();
        foreach (var a in await db.KbArticles.AsNoTracking().Where(a => teamIds.Contains(a.Id)).Select(a => new { a.Id, a.Title }).ToListAsync(ct))
            titles[(KbSource.Team, a.Id)] = a.Title;
        foreach (var f in await db.FaqArticles.AsNoTracking().Where(f => faqIds.Contains(f.Id)).Select(f => new { f.Id, f.Question }).ToListAsync(ct))
            titles[(KbSource.ClientFaq, f.Id)] = f.Question;
        return titles;
    }

    private async Task<Dictionary<Guid, string>> ClientNamesAsync(CancellationToken ct)
        => await db.ClientCompanies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);

    private static KbSource ParseSource(string source)
        => Enum.TryParse<KbSource>(source, true, out var kind) && Enum.IsDefined(kind) ? kind : throw new NotFoundException("Article");

    private static HelpArticleSummaryDto Summary(Readable a) => new(a.Source.ToString(), a.Id, a.Title, a.Category, Excerpt(a.Body));

    private static KbArticleDto Dto(KbArticle a) => new(
        a.Id, a.Title, a.Body, a.Category, a.Audience.ToString(), [.. a.Clients.Select(c => c.ClientCompanyId)],
        a.IsPublished, a.AuthorName, a.UpdatedByName, a.UpdatedAt);

    /// <summary>The words worth matching on: lower-cased, three letters or more, at most eight of them.</summary>
    public static IReadOnlyList<string> Terms(string? text)
        => string.IsNullOrWhiteSpace(text) ? []
            : [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3).Distinct().Take(8)];

    /// <summary>Words too common to say anything about which article is meant.</summary>
    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "not", "can", "cant", "cannot", "with", "our", "are", "was", "has", "have", "this",
        "that", "from", "any", "all", "but", "you", "your", "its", "how", "what", "why", "when", "does", "doesnt",
        "dont", "working", "work", "works", "issue", "problem", "please", "help", "need", "unable", "getting",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    private static bool Contains(string? haystack, string term)
        => haystack is not null && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first lines of the body without the markdown marks, for a list.</summary>
    private static string Excerpt(string body)
    {
        var plain = MarkPattern().Replace(body, " ");
        plain = SpacePattern().Replace(plain, " ").Trim();
        return plain.Length <= 160 ? plain : plain[..157].TrimEnd() + "…";
    }

    [GeneratedRegex(@"[*_`#>|\[\]()-]+")]
    private static partial Regex MarkPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();

    private static string? Clean(string? value, int max, string what, bool truncate = false)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (clean is null || clean.Length <= max) return clean;
        return truncate ? clean[..max] : throw new ValidationFailedException($"Keep {what} to {max:N0} characters.");
    }
}
