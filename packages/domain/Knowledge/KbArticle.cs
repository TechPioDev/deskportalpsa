using Desk.Domain.Common;

namespace Desk.Domain.Knowledge;

/// <summary>Who can read an article the team wrote.</summary>
public enum KbAudience
{
    /// <summary>The team only: runbooks, "how we reset Acme's firewall".</summary>
    Staff = 0,

    /// <summary>Every client's users, and the team.</summary>
    AllClients = 1,

    /// <summary>The users of the clients listed in <see cref="KbArticle.Clients"/>, and the team.</summary>
    SelectedClients = 2,
}

/// <summary>
/// A knowledge-base article written by the MSP's own team. The client's own FAQ articles (Control
/// Panel → Knowledge Base) are a separate, client-authored list; a client's Help page shows both.
///
/// The body is the same light markdown notes use - bold, lists, tables, code - so an article reads
/// the way a technician's note does.
/// </summary>
public class KbArticle : TenantEntity
{
    public required string Title { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>Free-text grouping on the Help page: "Email", "VPN", "Printers".</summary>
    public string? Category { get; set; }

    public KbAudience Audience { get; set; } = KbAudience.Staff;

    /// <summary>Drafts are visible to the team only, whatever the audience says.</summary>
    public bool IsPublished { get; set; }

    public Guid? AuthorAppUserId { get; set; }
    public string? AuthorName { get; set; }
    public string? UpdatedByName { get; set; }

    public ICollection<KbArticleClient> Clients { get; set; } = new List<KbArticleClient>();

    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 20_000;
    public const int MaxCategoryLength = 60;
}

/// <summary>One client an article with <see cref="KbAudience.SelectedClients"/> is shown to.</summary>
public class KbArticleClient : TenantEntity
{
    public Guid ArticleId { get; set; }
    public KbArticle? Article { get; set; }
    public Guid ClientCompanyId { get; set; }
}

/// <summary>Which of the two article lists an article came from.</summary>
public enum KbSource
{
    /// <summary>Written by the MSP's team (<see cref="KbArticle"/>).</summary>
    Team = 0,

    /// <summary>The client's own FAQ (Control Panel → Knowledge Base).</summary>
    ClientFaq = 1,
}

/// <summary>
/// A ticket that was not raised: a client typing a new ticket was shown an article, and said it solved
/// their problem. Counted, because "12 tickets avoided this month, most of them by the VPN article" is
/// the number that says whether writing articles is worth the team's time.
/// </summary>
public class KbDeflection : TenantEntity
{
    public Guid ClientCompanyId { get; set; }
    public Guid ClientUserId { get; set; }
    public KbSource Source { get; set; }
    public Guid ArticleId { get; set; }

    /// <summary>The title the client had typed, so the team sees the words people use.</summary>
    public string? Query { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
