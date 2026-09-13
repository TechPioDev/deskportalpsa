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
