namespace Desk.Application.Admin;

/// <summary>
/// One rule: tickets the PSA files under these levels are this in the portal. A level left out
/// means "whatever it is"; a target left out is left to a broader rule, or unsaid.
/// </summary>
public sealed record ClassificationRuleDto(
    Guid? Id, string? TicketType, string? IssueType, string? SubIssueType,
    string? Category, string? WorkType, string? Subcategory);

/// <summary>
/// One classification the connection's tickets are filed under, and what the rules make of it.
/// <paramref name="Mapped"/> false is UNMAPPED: no rule names it, and nothing was put in its place.
/// </summary>
public sealed record ClassificationSeenDto(
    string? TicketType, string? IssueType, string? SubIssueType, int Tickets,
    bool Mapped, string? Category, string? WorkType, string? Subcategory);

/// <summary>How far a connection's classification rules reach. Counted from the tickets and the rules as they are now.</summary>
public sealed record ClassificationHealthDto(
    int Rules,
    /// <summary>Tickets the PSA filed under at least one level. The rest have nothing to map.</summary>
    int ClassifiedTickets,
    /// <summary>Of those, the ones no rule speaks for.</summary>
    int UnmappedTickets,
    /// <summary>Different classifications tickets are filed under, and how many of them a rule speaks for.</summary>
    int Classifications, int MappedClassifications,
    /// <summary>Percent of classified tickets a rule speaks for. Null where no ticket is classified.</summary>
    double? Coverage,
    /// <summary>Rules that speak for no ticket here: a PSA value renamed or retired, or a rule written ahead of its first ticket.</summary>
    int RulesMatchingNothing,
    /// <summary>Tickets whose stored category, work type or subcategory is not what the rules now say: "apply" would rewrite them.</summary>
    int TicketsOutOfStep);

public sealed record ClassificationMappingDto(
    Guid ConnectionId, string ConnectionName, string Provider,
    IReadOnlyList<ClassificationRuleDto> Rules,
    /// <summary>What tickets are filed under, unmapped first and then by how many tickets. At most <see cref="SeenShown"/>.</summary>
    IReadOnlyList<ClassificationSeenDto> Seen,
    int SeenShown,
    ClassificationHealthDto Health,
    /// <summary>The portal's words already in use on this connection's rules and tickets, offered when writing a rule. Not a fixed list.</summary>
    IReadOnlyList<string> Categories, IReadOnlyList<string> WorkTypes, IReadOnlyList<string> Subcategories);

/// <summary>A ticket already here, as it is and as the rules being tried would leave it.</summary>
public sealed record ClassificationPreviewTicketDto(
    string Reference, string Title, string? TicketType, string? IssueType, string? SubIssueType,
    string? CategoryNow, string? CategoryThen, string? WorkTypeNow, string? WorkTypeThen, string? SubcategoryNow, string? SubcategoryThen);

/// <summary>What a set of rules would do, worked out and not done.</summary>
public sealed record ClassificationPreviewDto(
    IReadOnlyList<ClassificationSeenDto> Seen, ClassificationHealthDto Health,
    /// <summary>Tickets already here that applying these rules would rewrite, and a few of them.</summary>
    int TicketsThatWouldChange, IReadOnlyList<ClassificationPreviewTicketDto> Sample);

public sealed record ClassificationApplyResultDto(int TicketsChanged, IReadOnlyList<string> Changes);

/// <summary>
/// The rules by which a connection's PSA classification becomes the portal's category, work type and
/// subcategory. Each connection has its own. Reading needs mappings.view, changing mappings.manage;
/// every change is audited, and a connection of another organization is not found.
/// </summary>
public interface IClassificationMappingService
{
    Task<ClassificationMappingDto> GetAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the connection's rules with these. Everything wrong with them is said together and
    /// nothing is saved unless all of it is right. Tickets already here are not touched: that is
    /// <see cref="ApplyAsync"/>, asked for separately.
    /// </summary>
    Task<ClassificationMappingDto> SaveAsync(Guid connectionId, IReadOnlyList<ClassificationRuleDto> rules, CancellationToken ct = default);

    /// <summary>What these rules would do to the tickets already here. Changes nothing, and saves nothing.</summary>
    Task<ClassificationPreviewDto> PreviewAsync(Guid connectionId, IReadOnlyList<ClassificationRuleDto> rules, CancellationToken ct = default);

    /// <summary>Gives the tickets already here what the saved rules say, without waiting for each to be read from the PSA again.</summary>
    Task<ClassificationApplyResultDto> ApplyAsync(Guid connectionId, CancellationToken ct = default);
}
