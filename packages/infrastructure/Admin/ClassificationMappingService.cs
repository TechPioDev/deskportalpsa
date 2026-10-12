using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// The rules by which one connection's PSA classification becomes the portal's category, work type
/// and subcategory, and what those rules make of the tickets already here.
///
/// Nothing about a ticket's classification is stored but the PSA's own three levels and the three
/// portal words. What is unmapped, what a rule would change and what is out of step are all worked
/// out from those and the rules as they are, by the same <see cref="ClassificationRules"/> the sync
/// reads a ticket with: there is no second opinion to drift from the first.
/// </summary>
public sealed class ClassificationMappingService(
    DeskDbContext db, IAuditWriter audit, IMappingEngine mapping, TimeProvider clock) : IClassificationMappingService
{
    public const int MaxRules = 500;
    public const int SeenShown = 300;
    private const int BatchSize = 1000;

    /// <summary>
    /// Tickets of the connection that share everything the classification depends on, and what
    /// they hold now. One grouped pass over the connection's tickets, from which the health
    /// figures, the preview and "apply" are all counted.
    /// </summary>
    internal sealed record Group(
        string? TicketType, string? IssueType, string? SubIssueType, string? PsaCategory,
        string? Category, string? WorkType, string? Subcategory, string? Queue, Guid? Client, int Count);

    /// <summary>What the rules say a group's tickets should hold.</summary>
    internal sealed record Target(bool Mapped, string? Category, string? WorkType, string? Subcategory, string? RuleCategory);

    private async Task<PsaConnection> FindAsync(Guid connectionId, CancellationToken ct)
        => await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
           ?? throw new NotFoundException("PSA connection");

    private static Task<List<ClassificationMapping>> RulesAsync(DeskDbContext db, PsaConnection connection, CancellationToken ct)
        => db.ClassificationMappings.AsNoTracking()
            .Where(m => m.MspOrganizationId == connection.MspOrganizationId && m.PsaConnectionId == connection.Id)
            .ToListAsync(ct);

    /// <summary>
    /// Whether what the category field's own mapping says can differ from one client or board to
    /// another. Only then are the groups split by them: a PSA of two thousand clients is otherwise
    /// two thousand times the groups for nothing.
    /// </summary>
    private static bool CategoryByPlace(IReadOnlyList<FieldMapping> fieldRules)
        => fieldRules.Any(r => r.IsActive && string.Equals(r.ExternalField, "category", StringComparison.OrdinalIgnoreCase)
            && r.Scope is MappingScope.ClientCompanyOverride or MappingScope.QueueOrBoardOverride);

    private static async Task<List<Group>> GroupsAsync(DeskDbContext db, Guid connectionId, bool byPlace, CancellationToken ct)
    {
        var tickets = db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == connectionId);
        if (byPlace)
            return (await tickets
                    .GroupBy(t => new { t.PsaTicketType, t.PsaIssueType, t.PsaSubIssueType, t.PsaCategory, t.PortalCategory, t.PortalWorkType, t.PortalSubcategory, t.QueueOrBoard, t.ClientCompanyId })
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToListAsync(ct))
                .Select(g => new Group(g.Key.PsaTicketType, g.Key.PsaIssueType, g.Key.PsaSubIssueType, g.Key.PsaCategory,
                    g.Key.PortalCategory, g.Key.PortalWorkType, g.Key.PortalSubcategory, g.Key.QueueOrBoard, g.Key.ClientCompanyId, g.Count))
                .ToList();
        return (await tickets
                .GroupBy(t => new { t.PsaTicketType, t.PsaIssueType, t.PsaSubIssueType, t.PsaCategory, t.PortalCategory, t.PortalWorkType, t.PortalSubcategory })
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new Group(g.Key.PsaTicketType, g.Key.PsaIssueType, g.Key.PsaSubIssueType, g.Key.PsaCategory,
                g.Key.PortalCategory, g.Key.PortalWorkType, g.Key.PortalSubcategory, null, null, g.Count))
            .ToList();
    }

    /// <summary>
    /// What a ticket of this group should hold, exactly as the sync would decide it on reading the
    /// ticket again: a rule's category where a rule gives one, and otherwise what the category
    /// field's own mapping makes of the PSA's category, as before there were rules.
    /// </summary>
    private static Target TargetOf(IMappingEngine mapping, PsaConnection connection, IReadOnlyList<ClassificationMapping> rules, IReadOnlyList<FieldMapping> fieldRules, Group g)
    {
        var filed = ClassificationRules.Resolve(rules, g.TicketType, g.IssueType, g.SubIssueType);
        var category = filed.Category;
        if (category is null && g.PsaCategory is not null)
        {
            var context = new MappingContext
            {
                Provider = connection.Provider, PsaConnectionId = connection.Id, QueueOrBoardKey = g.Queue, ClientCompanyId = g.Client,
            };
            var result = mapping.MapToPortal(fieldRules, context, "category", g.PsaCategory);
            category = (result.Resolved ? result.Value : null) ?? g.PsaCategory;
        }
        return new Target(filed.Matched, category, filed.WorkType, filed.Subcategory, filed.Category);
    }

    private static bool OutOfStep(Group g, Target t)
        => g.Category != t.Category || g.WorkType != t.WorkType || g.Subcategory != t.Subcategory;

    /// <summary>
    /// Whether these rules have anything to say about a group at all: only where the PSA filed the
    /// tickets under something. A ticket filed under nothing is left exactly as it is, whatever it
    /// holds. That includes one raised in the portal and not yet read back from the PSA, whose
    /// category is what the person raising it chose; it is not this page's to wipe.
    /// </summary>
    private static bool Touched(Group g) => ClassificationRules.IsClassified(g.TicketType, g.IssueType, g.SubIssueType);

    private static (List<ClassificationSeenDto> Seen, ClassificationHealthDto Health, int WouldChange) Figures(
        IMappingEngine mapping, PsaConnection connection, IReadOnlyList<ClassificationMapping> rules, IReadOnlyList<FieldMapping> fieldRules, IReadOnlyList<Group> groups)
    {
        var seen = new Dictionary<(string, string, string), ClassificationSeenDto>();
        var (classified, unmapped, outOfStep) = (0, 0, 0);
        foreach (var g in groups)
        {
            if (!Touched(g)) continue;
            var target = TargetOf(mapping, connection, rules, fieldRules, g);
            if (OutOfStep(g, target)) outOfStep += g.Count;
            classified += g.Count;
            if (!target.Mapped) unmapped += g.Count;
            // Counted under the words as they are compared, so "Hardware" and "hardware " are one line.
            var key = (Key(g.TicketType), Key(g.IssueType), Key(g.SubIssueType));
            seen[key] = seen.TryGetValue(key, out var held)
                ? held with { Tickets = held.Tickets + g.Count }
                : new ClassificationSeenDto(ClassificationRules.Clean(g.TicketType), ClassificationRules.Clean(g.IssueType), ClassificationRules.Clean(g.SubIssueType),
                    g.Count, target.Mapped, target.RuleCategory, target.WorkType, target.Subcategory);
        }

        var idle = rules.Count(r => !seen.Values.Any(s => ClassificationRules.Speaks(r, s.TicketType, s.IssueType, s.SubIssueType)));
        var ordered = seen.Values
            .OrderBy(s => s.Mapped ? 1 : 0)
            .ThenByDescending(s => s.Tickets)
            .ThenBy(s => s.TicketType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.IssueType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SubIssueType, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var health = new ClassificationHealthDto(rules.Count, classified, unmapped, ordered.Count, ordered.Count(s => s.Mapped),
            classified == 0 ? null : Math.Round(100.0 * (classified - unmapped) / classified, 1), idle, outOfStep);
        return (ordered, health, outOfStep);
    }

    private static string Key(string? level) => (ClassificationRules.Clean(level) ?? "").ToUpperInvariant();

    /// <summary>The figures alone, for the connection's mapping health.</summary>
    public static async Task<ClassificationHealthDto> HealthAsync(
        DeskDbContext db, IMappingEngine mapping, PsaConnection connection, IReadOnlyList<FieldMapping> fieldRules, CancellationToken ct = default)
    {
        var rules = await RulesAsync(db, connection, ct);
        var groups = await GroupsAsync(db, connection.Id, CategoryByPlace(fieldRules), ct);
        return Figures(mapping, connection, rules, fieldRules, groups).Health;
    }

    public async Task<ClassificationMappingDto> GetAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var rules = await RulesAsync(db, connection, ct);
        var fieldRules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var groups = await GroupsAsync(db, connection.Id, CategoryByPlace(fieldRules), ct);
        var (seen, health, _) = Figures(mapping, connection, rules, fieldRules, groups);

        IReadOnlyList<string> Words(IEnumerable<string?> ofRules, IEnumerable<string?> ofTickets)
            => ofRules.Concat(ofTickets).Select(ClassificationRules.Clean).Where(w => w is not null).Select(w => w!)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w, StringComparer.OrdinalIgnoreCase).Take(200).ToList();

        return new ClassificationMappingDto(connection.Id, connection.Name, connection.Provider.ToString(),
            rules.OrderBy(r => r.TicketType, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.IssueType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.SubIssueType, StringComparer.OrdinalIgnoreCase).Select(Dto).ToList(),
            seen.Take(SeenShown).ToList(), SeenShown, health,
            Words(rules.Select(r => r.Category), groups.Select(g => g.Category)),
            Words(rules.Select(r => r.WorkType), groups.Select(g => g.WorkType)),
            Words(rules.Select(r => r.Subcategory), groups.Select(g => g.Subcategory)));
    }

    private static ClassificationRuleDto Dto(ClassificationMapping r) => new(
        r.Id, ClassificationRules.Clean(r.TicketType), ClassificationRules.Clean(r.IssueType), ClassificationRules.Clean(r.SubIssueType),
        r.Category, r.WorkType, r.Subcategory);

    /// <summary>
    /// The rules as they would be stored, or everything that is wrong with them. All of it at once:
    /// someone who has written forty rules is told about the three that will not do, not about one
    /// and then, on trying again, the next.
    /// </summary>
    private static List<ClassificationMapping> Checked(PsaConnection connection, IReadOnlyList<ClassificationRuleDto>? input)
    {
        input ??= [];
        var problems = new List<string>();
        if (input.Count > MaxRules) problems.Add($"There are {input.Count} rules; a connection can have {MaxRules}.");

        var rules = new List<ClassificationMapping>();
        var named = new Dictionary<(string, string, string), int>();
        for (var i = 0; i < input.Count && i < MaxRules; i++)
        {
            var r = input[i];
            var row = $"Rule {i + 1}";
            foreach (var (what, value) in new[] { ("first level", r.TicketType), ("second level", r.IssueType), ("third level", r.SubIssueType),
                         ("category", r.Category), ("work type", r.WorkType), ("subcategory", r.Subcategory) })
                if (value is not null && value.Trim().Length > ClassificationRules.MaxLength)
                    problems.Add($"{row}: the {what} is longer than {ClassificationRules.MaxLength} characters.");

            var (type, issue, sub) = (ClassificationRules.Clean(r.TicketType), ClassificationRules.Clean(r.IssueType), ClassificationRules.Clean(r.SubIssueType));
            var (category, workType, subcategory) = (ClassificationRules.Clean(r.Category), ClassificationRules.Clean(r.WorkType), ClassificationRules.Clean(r.Subcategory));
            // No rule for "everything else". It would give a meaning to a classification nobody
            // has looked at, which is the one thing these rules are never to do.
            if (type is null && issue is null && sub is null)
                problems.Add($"{row} names none of the PSA's levels. A rule has to say which classification it is for; there is no rule for everything else.");
            if (category is null && workType is null && subcategory is null)
                problems.Add($"{row} gives no category, work type or subcategory, so it says nothing.");

            var key = (Key(type), Key(issue), Key(sub));
            if (named.TryGetValue(key, out var first))
                problems.Add($"{row} is for the same classification as rule {first}. One rule for each.");
            else
                named[key] = i + 1;

            rules.Add(new ClassificationMapping
            {
                MspOrganizationId = connection.MspOrganizationId, PsaConnectionId = connection.Id,
                TicketType = type ?? "", IssueType = issue ?? "", SubIssueType = sub ?? "",
                Category = category, WorkType = workType, Subcategory = subcategory,
            });
        }

        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems));
        return rules;
    }

    private static string Describe(ClassificationMapping r)
    {
        var levels = string.Join(" / ", new[] { r.TicketType, r.IssueType, r.SubIssueType }.Select(l => ClassificationRules.Clean(l) ?? "any"));
        var gives = string.Join(", ", new[] { ("category", r.Category), ("work type", r.WorkType), ("subcategory", r.Subcategory) }
            .Where(t => t.Item2 is not null).Select(t => $"{t.Item1} {t.Item2}"));
        return $"{levels} → {gives}";
    }

    public async Task<ClassificationMappingDto> SaveAsync(Guid connectionId, IReadOnlyList<ClassificationRuleDto> rules, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var wanted = Checked(connection, rules);
        var held = await db.ClassificationMappings
            .Where(m => m.MspOrganizationId == connection.MspOrganizationId && m.PsaConnectionId == connection.Id)
            .ToListAsync(ct);

        (string, string, string) KeyOf(ClassificationMapping r) => (Key(r.TicketType), Key(r.IssueType), Key(r.SubIssueType));
        var before = held.ToDictionary(KeyOf);
        var after = wanted.ToDictionary(KeyOf);
        var (added, changed, removed) = (new List<string>(), new List<string>(), new List<string>());

        foreach (var (key, rule) in before)
            if (!after.ContainsKey(key))
            {
                removed.Add(Describe(rule));
                db.ClassificationMappings.Remove(rule);
            }
        foreach (var (key, rule) in after)
        {
            if (!before.TryGetValue(key, out var existing))
            {
                added.Add(Describe(rule));
                db.ClassificationMappings.Add(rule);
                continue;
            }
            // The same classification: the rule is kept and what it says is changed, so that the
            // audit of it reads as a change and not as a rule that left and another that came.
            var was = Describe(existing);
            (existing.TicketType, existing.IssueType, existing.SubIssueType) = (rule.TicketType, rule.IssueType, rule.SubIssueType);
            (existing.Category, existing.WorkType, existing.Subcategory) = (rule.Category, rule.WorkType, rule.Subcategory);
            if (Describe(existing) != was) changed.Add($"{was}  ⇒  {Describe(existing)}");
        }

        if (added.Count + changed.Count + removed.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("classification.mapping.changed", "PsaConnection", connection.Id.ToString(),
                new { connection.Name, rules = wanted.Count, added, changed, removed }, ct);
        }
        return await GetAsync(connectionId, ct);
    }

    public async Task<ClassificationPreviewDto> PreviewAsync(Guid connectionId, IReadOnlyList<ClassificationRuleDto> rules, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var tried = Checked(connection, rules);
        var fieldRules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var byPlace = CategoryByPlace(fieldRules);
        var groups = await GroupsAsync(db, connection.Id, byPlace, ct);
        var (seen, health, wouldChange) = Figures(mapping, connection, tried, fieldRules, groups);

        // A few of the tickets that would change, from the groups with the most of them.
        const int sampleSize = 10;
        var sample = new List<ClassificationPreviewTicketDto>();
        foreach (var g in groups.Where(g => Touched(g) && OutOfStep(g, TargetOf(mapping, connection, tried, fieldRules, g))).OrderByDescending(g => g.Count))
        {
            if (sample.Count >= sampleSize) break;
            var target = TargetOf(mapping, connection, tried, fieldRules, g);
            var rows = await InGroup(db.Tickets.AsNoTracking(), connection.Id, g, byPlace)
                .OrderByDescending(t => t.LastSyncedAt).ThenBy(t => t.Id)
                .Take(Math.Min(3, sampleSize - sample.Count))
                .Select(t => new { t.ExternalTicketId, t.Number, t.Title })
                .ToListAsync(ct);
            sample.AddRange(rows.Select(t => new ClassificationPreviewTicketDto(t.ExternalTicketId ?? t.Number ?? "", t.Title,
                g.TicketType, g.IssueType, g.SubIssueType, g.Category, target.Category, g.WorkType, target.WorkType, g.Subcategory, target.Subcategory)));
        }
        return new ClassificationPreviewDto(seen.Take(SeenShown).ToList(), health, wouldChange, sample);
    }

    /// <summary>The tickets of one group, by everything the group was made from.</summary>
    private static IQueryable<Desk.Domain.Tickets.Ticket> InGroup(IQueryable<Desk.Domain.Tickets.Ticket> tickets, Guid connectionId, Group g, bool byPlace)
    {
        var scope = tickets.Where(t => t.PsaConnectionId == connectionId
            && t.PsaTicketType == g.TicketType && t.PsaIssueType == g.IssueType && t.PsaSubIssueType == g.SubIssueType
            && t.PsaCategory == g.PsaCategory
            && t.PortalCategory == g.Category && t.PortalWorkType == g.WorkType && t.PortalSubcategory == g.Subcategory);
        return byPlace ? scope.Where(t => t.QueueOrBoard == g.Queue && t.ClientCompanyId == g.Client) : scope;
    }

    public async Task<ClassificationApplyResultDto> ApplyAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var rules = await RulesAsync(db, connection, ct);
        var fieldRules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var byPlace = CategoryByPlace(fieldRules);

        // One read of the connection's classified tickets, and of each no more than what it is filed
        // under and what it holds: not the ticket. What is out of step is found here, by id, and
        // rewritten below a thousand ids at a time. A connection of a quarter of a million tickets
        // is one pass and then a statement for each thousand that change; loading each ticket to
        // change three words on it took a second for every few thousand.
        var held = db.Tickets.AsNoTracking()
            .Where(t => t.PsaConnectionId == connection.Id
                && (t.PsaTicketType != null || t.PsaIssueType != null || t.PsaSubIssueType != null))
            .Select(t => new
            {
                t.Id, t.PsaTicketType, t.PsaIssueType, t.PsaSubIssueType, t.PsaCategory,
                t.PortalCategory, t.PortalWorkType, t.PortalSubcategory, t.QueueOrBoard, t.ClientCompanyId,
            });
        var targets = new Dictionary<Group, Target>();
        var todo = new Dictionary<Group, List<Guid>>();
        await foreach (var t in held.AsAsyncEnumerable().WithCancellation(ct))
        {
            var g = new Group(t.PsaTicketType, t.PsaIssueType, t.PsaSubIssueType, t.PsaCategory,
                t.PortalCategory, t.PortalWorkType, t.PortalSubcategory, byPlace ? t.QueueOrBoard : null, byPlace ? t.ClientCompanyId : null, 0);
            if (!Touched(g)) continue;
            if (!targets.TryGetValue(g, out var target)) targets[g] = target = TargetOf(mapping, connection, rules, fieldRules, g);
            if (!OutOfStep(g, target)) continue;
            if (!todo.TryGetValue(g, out var ids)) todo[g] = ids = [];
            ids.Add(t.Id);
        }

        var changes = new Dictionary<string, int>();
        var total = 0;
        var now = clock.GetUtcNow();
        foreach (var (g, ids) in todo)
        {
            var target = targets[g];
            var changed = 0;
            foreach (var batch in ids.Chunk(BatchSize))
                // By id, and only where the ticket is still filed as it was when it was read a
                // moment ago: one a sync has refiled since is the sync's, and already right. The
                // version moves on as it does for any change, so that anything holding the ticket
                // from before this knows it has changed.
                changed += await db.Tickets
                    .Where(t => t.MspOrganizationId == connection.MspOrganizationId && t.PsaConnectionId == connection.Id && batch.Contains(t.Id)
                        && t.PsaTicketType == g.TicketType && t.PsaIssueType == g.IssueType && t.PsaSubIssueType == g.SubIssueType
                        && t.PsaCategory == g.PsaCategory)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(t => t.PortalCategory, target.Category)
                        .SetProperty(t => t.PortalWorkType, target.WorkType)
                        .SetProperty(t => t.PortalSubcategory, target.Subcategory)
                        .SetProperty(t => t.Version, t => t.Version + 1)
                        .SetProperty(t => t.UpdatedAt, now), ct);
            if (changed == 0) continue;
            total += changed;
            var levels = string.Join(" / ", new[] { g.TicketType, g.IssueType, g.SubIssueType }.Select(l => ClassificationRules.Clean(l) ?? "–"));
            var line = target.Mapped
                ? $"{levels} → {string.Join(", ", new[] { target.Category, target.WorkType, target.Subcategory }.Select(v => v ?? "–"))}"
                : $"{levels} → unmapped";
            changes[line] = changes.GetValueOrDefault(line) + changed;
        }

        var summary = changes.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} ({c.Value})").Take(100).ToList();
        await audit.WriteAsync("classification.mapping.applied", "PsaConnection", connection.Id.ToString(),
            new { connection.Name, tickets = total, changes = summary }, ct);
        return new ClassificationApplyResultDto(total, summary);
    }
}
