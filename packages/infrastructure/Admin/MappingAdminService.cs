using System.Text.Json;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

public sealed class MappingAdminService(
    DeskDbContext db,
    IAuditWriter audit,
    ICurrentUser user) : IMappingAdminService
{
    /// <summary>Serializable form of a mapping rule used for version snapshots.</summary>
    private sealed record SnapshotRule(
        MappingScope Scope, Guid? PsaConnectionId, Guid? ClientCompanyId, string? QueueOrBoardKey, string? TicketTypeKey,
        string PortalField, string? PortalValue, string ExternalField, string? ExternalValue,
        MappingDirection Direction, bool IsRequired, string? FallbackValue, bool IsActive);

    public async Task<IReadOnlyList<MappingRuleDto>> ListAsync(ProviderType provider, CancellationToken ct = default)
        => await db.FieldMappings.AsNoTracking()
            .Where(m => m.Provider == provider)
            .OrderBy(m => m.Scope).ThenBy(m => m.PortalField)
            .Select(m => Dto(m))
            .ToListAsync(ct);

    public async Task<MappingRuleDto> UpsertAsync(UpsertMappingInput input, string? changeNote, CancellationToken ct = default)
    {
        // A rule that names a connection must name one this caller can see, of the provider the rule
        // is for. The id used to be stored as sent: a rule could be filed against a connection of
        // another organization, where the scheduled sync - which reads across organizations - would
        // have applied it.
        if (input.PsaConnectionId is { } connectionId)
        {
            var provider = await db.PsaConnections.AsNoTracking()
                .Where(c => c.Id == connectionId)
                .Select(c => (ProviderType?)c.Provider)
                .FirstOrDefaultAsync(ct)
                ?? throw new ValidationFailedException("That PSA connection does not exist.");
            if (provider != input.Provider)
                throw new ValidationFailedException("That connection belongs to a different PSA than this rule.");
        }
        else if (input.Scope == MappingScope.ConnectionOverride)
            throw new ValidationFailedException("A rule for one connection has to name the connection.");

        FieldMapping rule;
        if (input.Id is { } id)
        {
            rule = await db.FieldMappings.FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("Mapping rule");
            rule.Version++;
        }
        else
        {
            // Upsert semantics: without an id, match an existing rule for the same
            // provider/scope/connection/field/portal-value/direction and update it. Otherwise a
            // repeated save (API retry, re-running a setup script) silently creates duplicate rules,
            // which then make resolution ambiguous.
            var existing = await db.FieldMappings.FirstOrDefaultAsync(m =>
                m.Provider == input.Provider
                && m.Scope == input.Scope
                && m.PsaConnectionId == input.PsaConnectionId
                && m.PortalField == input.PortalField
                && m.PortalValue == input.PortalValue
                && m.Direction == input.Direction, ct);
            if (existing is not null)
            {
                rule = existing;
                rule.Version++;
            }
            else
            {
                rule = new FieldMapping { Provider = input.Provider, PortalField = input.PortalField, ExternalField = input.ExternalField };
                db.FieldMappings.Add(rule);
            }
        }

        rule.Provider = input.Provider;
        rule.Scope = input.Scope;
        rule.PsaConnectionId = input.PsaConnectionId;
        rule.PortalField = input.PortalField;
        rule.PortalValue = input.PortalValue;
        rule.ExternalField = input.ExternalField;
        rule.ExternalValue = input.ExternalValue;
        rule.Direction = input.Direction;
        rule.IsRequired = input.IsRequired;
        rule.FallbackValue = input.FallbackValue;
        await db.SaveChangesAsync(ct);

        await SnapshotAsync(input.Provider, input.PsaConnectionId, changeNote, ct);
        await audit.WriteAsync("mapping.upserted", "FieldMapping", rule.Id.ToString(),
            new { input.Provider, input.Scope, input.PortalField }, ct);

        return Dto(rule);
    }

    /// <summary>The portal's values a PSA's values of this field are mapped to. Null: not a field mapped that way.</summary>
    private static IReadOnlyList<string>? Vocabulary(string? field) => field?.Trim().ToLowerInvariant() switch
    {
        "status" => Desk.Domain.Tickets.PortalVocabulary.Statuses,
        "priority" => Desk.Domain.Tickets.PortalVocabulary.Priorities,
        _ => null,
    };

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public async Task<InboundMappingResultDto> SetInboundAsync(
        Guid connectionId, IReadOnlyList<SetInboundMappingInput> changes, string? changeNote, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.AsNoTracking()
            .Where(c => c.Id == connectionId)
            .Select(c => new { c.Id, c.Provider, c.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("PSA connection");
        if (changes.Count == 0) return new InboundMappingResultDto(0, []);

        // Everything is checked before anything is changed, and every problem is said at once.
        var problems = new List<string>();
        if (changes.Count > 500) problems.Add("At most 500 values can be changed at once.");
        foreach (var change in changes)
        {
            var allowed = Vocabulary(change.Field);
            if (allowed is null)
                problems.Add($"\"{change.Field}\" is not a field whose values are mapped to the portal's own.");
            else if (string.IsNullOrWhiteSpace(change.Value))
                problems.Add($"A {change.Field.Trim().ToLowerInvariant()} from the PSA is missing its value.");
            else if (change.Value.Trim().Length > 200)
                problems.Add($"\"{change.Value.Trim()[..40]}…\" is too long to be a value from the PSA.");
            else if (change.PortalValue is not null && !allowed.Contains(change.PortalValue))
                problems.Add($"\"{change.PortalValue}\" is not one of the portal's {change.Field.Trim().ToLowerInvariant()} values ({string.Join(", ", allowed)}).");
        }
        problems.AddRange(changes
            .Where(c => Vocabulary(c.Field) is not null && !string.IsNullOrWhiteSpace(c.Value))
            .GroupBy(c => (Field: c.Field.Trim().ToLowerInvariant(), Value: c.Value.Trim().ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"\"{g.First().Value.Trim()}\" is given two answers."));
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems.Distinct()));

        // This connection's own rules. A provider-wide rule is not this connection's to change, and a
        // rule for one connection outranks it in any case.
        var rules = await db.FieldMappings
            .Where(m => m.Provider == connection.Provider && m.PsaConnectionId == connectionId && m.Scope == MappingScope.ConnectionOverride)
            .ToListAsync(ct);
        var done = new List<string>();

        foreach (var change in changes)
        {
            var field = change.Field.Trim().ToLowerInvariant();
            var value = change.Value.Trim();
            // The rules that decide what this value ARRIVES as. There should be one; rules written
            // before this could be two, and which of them won was not something anyone chose.
            var arriving = rules
                .Where(r => r.IsActive && Same(r.ExternalField, field) && Same(r.ExternalValue, value)
                            && r.Direction is MappingDirection.Bidirectional or MappingDirection.ProviderToPortal)
                .ToList();
            var before = arriving.Select(r => r.PortalValue).FirstOrDefault(v => v is not null);
            if (arriving.Count <= 1 && before == change.PortalValue) continue;

            foreach (var rule in arriving)
            {
                if (rule.Direction == MappingDirection.Bidirectional)
                {
                    // It is also what the portal SENDS for its own value, and that half is not being
                    // changed: only what arrives is.
                    rule.Direction = MappingDirection.PortalToProvider;
                    rule.Version++;
                }
                else
                {
                    db.FieldMappings.Remove(rule);
                    rules.Remove(rule);
                }
            }
            if (change.PortalValue is { } target)
            {
                var rule = new FieldMapping
                {
                    Provider = connection.Provider, Scope = MappingScope.ConnectionOverride, PsaConnectionId = connectionId,
                    PortalField = field, PortalValue = target, ExternalField = field, ExternalValue = value,
                    Direction = MappingDirection.ProviderToPortal,
                };
                db.FieldMappings.Add(rule);
                rules.Add(rule);
            }
            done.Add($"{field}: {value} → {change.PortalValue ?? "not mapped"} (was {before ?? "not mapped"})");
        }

        if (done.Count == 0) return new InboundMappingResultDto(0, []);
        await db.SaveChangesAsync(ct);
        await SnapshotAsync(connection.Provider, connectionId,
            string.IsNullOrWhiteSpace(changeNote) ? $"{done.Count} {(done.Count == 1 ? "value" : "values")} from the PSA mapped" : changeNote, ct);
        // Who, when, which connection, and each value's old and new answer.
        await audit.WriteAsync("mapping.inbound.changed", "PsaConnection", connectionId.ToString(),
            new { connection.Name, changes = done }, ct);
        return new InboundMappingResultDto(done.Count, done);
    }

    public async Task DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        var rule = await db.FieldMappings.FirstOrDefaultAsync(m => m.Id == ruleId, ct)
            ?? throw new NotFoundException("Mapping rule");
        var (provider, connectionId, field, portalValue) = (rule.Provider, rule.PsaConnectionId, rule.PortalField, rule.PortalValue);

        db.FieldMappings.Remove(rule);
        await db.SaveChangesAsync(ct);

        // Same versioning/audit discipline as an upsert, so a deletion is recoverable by rollback.
        await SnapshotAsync(provider, connectionId, $"delete {field} {portalValue}", ct);
        await audit.WriteAsync("mapping.deleted", "FieldMapping", ruleId.ToString(),
            new { provider, field, portalValue }, ct);
    }

    public async Task<IReadOnlyList<MappingVersionDto>> VersionsAsync(ProviderType provider, Guid? connectionId, CancellationToken ct = default)
        => await db.FieldMappingVersions.AsNoTracking()
            .Where(v => v.Provider == provider && v.PsaConnectionId == connectionId)
            .OrderByDescending(v => v.Version)
            .Select(v => new MappingVersionDto(v.Id, v.Provider, v.PsaConnectionId, v.Version, v.ChangedByUserId, v.ChangeNote, v.CreatedAt))
            .ToListAsync(ct);

    public async Task RollbackAsync(Guid versionId, CancellationToken ct = default)
    {
        var version = await db.FieldMappingVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new NotFoundException("Mapping version");

        var snapshot = JsonSerializer.Deserialize<List<SnapshotRule>>(version.SnapshotJson) ?? [];

        // Replace the current rule set for this provider/connection scope with the snapshot.
        var current = await db.FieldMappings
            .Where(m => m.Provider == version.Provider && m.PsaConnectionId == version.PsaConnectionId)
            .ToListAsync(ct);
        db.FieldMappings.RemoveRange(current);
        foreach (var s in snapshot)
            db.FieldMappings.Add(new FieldMapping
            {
                Provider = version.Provider, Scope = s.Scope, PsaConnectionId = s.PsaConnectionId,
                ClientCompanyId = s.ClientCompanyId, QueueOrBoardKey = s.QueueOrBoardKey, TicketTypeKey = s.TicketTypeKey,
                PortalField = s.PortalField, PortalValue = s.PortalValue, ExternalField = s.ExternalField,
                ExternalValue = s.ExternalValue, Direction = s.Direction, IsRequired = s.IsRequired,
                FallbackValue = s.FallbackValue, IsActive = s.IsActive,
            });
        await db.SaveChangesAsync(ct);

        await SnapshotAsync(version.Provider, version.PsaConnectionId, $"Rollback to v{version.Version}", ct);
        await audit.WriteAsync("mapping.rolledback", "FieldMappingVersion", versionId.ToString(),
            new { version.Provider, RolledBackTo = version.Version }, ct);
    }

    public async Task<MappingSnapshotStatusDto> SnapshotStatusAsync(ProviderType provider, Guid? connectionId, CancellationToken ct = default)
    {
        var live = await LiveRulesAsync(provider, connectionId, ct);
        var latest = await db.FieldMappingVersions.AsNoTracking()
            .Where(v => v.Provider == provider && v.PsaConnectionId == connectionId)
            .OrderByDescending(v => v.Version)
            .FirstOrDefaultAsync(ct);
        if (latest is null)
            return new MappingSnapshotStatusDto(null, null, live.Count, 0, live.Count == 0);

        var saved = JsonSerializer.Deserialize<List<SnapshotRule>>(latest.SnapshotJson) ?? [];
        return new MappingSnapshotStatusDto(latest.Version, latest.CreatedAt, live.Count, saved.Count, SameRules(live, saved));
    }

    public async Task<MappingSnapshotStatusDto> SaveSnapshotAsync(ProviderType provider, Guid? connectionId, string? note, CancellationToken ct = default)
    {
        var before = await SnapshotStatusAsync(provider, connectionId, ct);
        if (before.MatchesLiveRules && before.LatestVersion is not null)
            return before;

        await SnapshotAsync(provider, connectionId, string.IsNullOrWhiteSpace(note) ? "Snapshot saved from the mappings page" : note.Trim(), ct);
        var after = await SnapshotStatusAsync(provider, connectionId, ct);
        await audit.WriteAsync("mapping.snapshot", "FieldMappingVersion", after.LatestVersion?.ToString(),
            new { Provider = provider, ConnectionId = connectionId, Version = after.LatestVersion, Rules = after.LiveRules, ReplacedDriftedVersion = before.LatestVersion }, ct);
        return after;
    }

    /// <summary>Same rules regardless of order — a snapshot records a set, not a sequence.</summary>
    private static bool SameRules(IReadOnlyList<SnapshotRule> a, IReadOnlyList<SnapshotRule> b)
        => a.Count == b.Count
           && a.Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal)
               .SequenceEqual(b.Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal));

    private Task<List<SnapshotRule>> LiveRulesAsync(ProviderType provider, Guid? connectionId, CancellationToken ct)
        => db.FieldMappings
            .Where(m => m.Provider == provider && m.PsaConnectionId == connectionId)
            .Select(m => new SnapshotRule(m.Scope, m.PsaConnectionId, m.ClientCompanyId, m.QueueOrBoardKey, m.TicketTypeKey,
                m.PortalField, m.PortalValue, m.ExternalField, m.ExternalValue, m.Direction, m.IsRequired, m.FallbackValue, m.IsActive))
            .ToListAsync(ct);

    private async Task SnapshotAsync(ProviderType provider, Guid? connectionId, string? note, CancellationToken ct)
    {
        var rules = await LiveRulesAsync(provider, connectionId, ct);

        var nextVersion = 1 + await db.FieldMappingVersions
            .Where(v => v.Provider == provider && v.PsaConnectionId == connectionId)
            .Select(v => (int?)v.Version).MaxAsync(ct) ?? 1;

        db.FieldMappingVersions.Add(new FieldMappingVersion
        {
            Provider = provider,
            PsaConnectionId = connectionId,
            Version = nextVersion,
            SnapshotJson = JsonSerializer.Serialize(rules),
            ChangedByUserId = user.Subject ?? "system",
            ChangeNote = note,
        });
        await db.SaveChangesAsync(ct);
    }

    private static MappingRuleDto Dto(FieldMapping m) => new(
        m.Id, m.Provider, m.Scope, m.PsaConnectionId, m.PortalField, m.PortalValue,
        m.ExternalField, m.ExternalValue, m.Direction, m.IsRequired, m.FallbackValue, m.IsActive, m.Version);
}
