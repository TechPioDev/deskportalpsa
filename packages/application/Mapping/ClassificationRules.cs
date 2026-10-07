using Desk.Domain.Mapping;

namespace Desk.Application.Mapping;

/// <summary>
/// What the rules of a connection make of one classification.
///
/// <paramref name="Matched"/> is whether any rule names it. Where none does the three are null and
/// the classification is unmapped: nothing is put in their place.
/// </summary>
public sealed record ClassificationResult(
    bool Matched, string? Category, string? WorkType, string? Subcategory,
    Guid? CategoryRuleId = null, Guid? WorkTypeRuleId = null, Guid? SubcategoryRuleId = null)
{
    public static readonly ClassificationResult Unmapped = new(false, null, null, null);
}

/// <summary>
/// Reads a connection's classification rules. One place, used by the sync as a ticket arrives, by
/// the preview, by the health figures and by "apply to tickets already here", so that the four
/// cannot disagree about what a rule means.
///
/// A rule speaks for a ticket when every level it names is the level the ticket is filed under,
/// compared without regard to case or surrounding space. Several rules can speak for one ticket: a
/// rule for the type, and another for one issue type under it. Each of the category, the work type
/// and the subcategory is then taken from the most exact rule that gives it, so the broad rule
/// says what kind of work it is and the narrow one adds what only it knows.
/// </summary>
public static class ClassificationRules
{
    public const int MaxLength = 200;

    /// <summary>A level or a target as it is stored and compared: trimmed, and nothing for blank.</summary>
    public static string? Clean(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= MaxLength ? v : v[..MaxLength];
    }

    public static bool Same(string? a, string? b)
        => string.Equals(Clean(a) ?? "", Clean(b) ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>A ticket the PSA filed under nothing has nothing to map, which is not the same as unmapped.</summary>
    public static bool IsClassified(string? ticketType, string? issueType, string? subIssueType)
        => Clean(ticketType) is not null || Clean(issueType) is not null || Clean(subIssueType) is not null;

    public static bool Speaks(ClassificationMapping rule, string? ticketType, string? issueType, string? subIssueType)
        => Names(rule.TicketType, ticketType) && Names(rule.IssueType, issueType) && Names(rule.SubIssueType, subIssueType);

    private static bool Names(string ruleLevel, string? level)
        => Clean(ruleLevel) is not { } wanted || string.Equals(wanted, Clean(level), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How exact a rule is. One that names more levels is more exact; of two naming as many, the
    /// one naming the deeper level is. No two rules of a connection name the same levels, so no two
    /// that speak for the same ticket are as exact as each other.
    /// </summary>
    public static int Exactness(ClassificationMapping rule)
    {
        var (type, issue, sub) = (Clean(rule.TicketType) is not null, Clean(rule.IssueType) is not null, Clean(rule.SubIssueType) is not null);
        var named = (type ? 1 : 0) + (issue ? 1 : 0) + (sub ? 1 : 0);
        return named * 8 + (sub ? 4 : 0) + (issue ? 2 : 0) + (type ? 1 : 0);
    }

    public static ClassificationResult Resolve(
        IReadOnlyList<ClassificationMapping> rules, string? ticketType, string? issueType, string? subIssueType)
    {
        if (rules.Count == 0 || !IsClassified(ticketType, issueType, subIssueType)) return ClassificationResult.Unmapped;

        ClassificationMapping? category = null, workType = null, subcategory = null;
        var matched = false;
        foreach (var rule in rules)
        {
            // A rule that names no level would speak for everything. None is saved; one that got
            // in some other way is not obeyed.
            if (Exactness(rule) == 0 || !Speaks(rule, ticketType, issueType, subIssueType)) continue;
            matched = true;
            if (Clean(rule.Category) is not null && (category is null || Exactness(rule) > Exactness(category))) category = rule;
            if (Clean(rule.WorkType) is not null && (workType is null || Exactness(rule) > Exactness(workType))) workType = rule;
            if (Clean(rule.Subcategory) is not null && (subcategory is null || Exactness(rule) > Exactness(subcategory))) subcategory = rule;
        }
        return matched
            ? new ClassificationResult(true, Clean(category?.Category), Clean(workType?.WorkType), Clean(subcategory?.Subcategory),
                category?.Id, workType?.Id, subcategory?.Id)
            : ClassificationResult.Unmapped;
    }
}
