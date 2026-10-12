using Desk.Domain.Enums;
using Desk.Domain.Mapping;

namespace Desk.Application.Mapping;

/// <summary>
/// Default resolution: filter rules that apply to the context and direction, then pick the most
/// specific match (narrowest scope wins; a value-specific rule beats a field-level default at the
/// same scope). Falls back to a rule's fallback value, and reports a validation miss when a
/// required field cannot resolve.
/// </summary>
public sealed class MappingEngine : IMappingEngine
{
    public MappingResult MapToProvider(IReadOnlyList<FieldMapping> rules, MappingContext ctx, string portalField, string? portalValue)
        => Resolve(rules, ctx, portalField, portalValue, outbound: true);

    public MappingResult MapToPortal(IReadOnlyList<FieldMapping> rules, MappingContext ctx, string externalField, string? externalValue)
        => Resolve(rules, ctx, externalField, externalValue, outbound: false);

    private static MappingResult Resolve(
        IReadOnlyList<FieldMapping> rules, MappingContext ctx, string field, string? value, bool outbound)
    {
        var candidates = rules
            .Where(r => r.IsActive
                        && r.Provider == ctx.Provider
                        && DirectionAllows(r.Direction, outbound)
                        && SourceField(r, outbound).Equals(field, StringComparison.OrdinalIgnoreCase)
                        && ScopeApplies(r, ctx))
            .ToList();

        if (candidates.Count == 0)
            return MappingResult.Miss($"No mapping rule for field '{field}' ({ctx.Provider}).");

        // Rank: narrower scope first; at equal scope, a value-specific rule beats a field-level one.
        // A rule qualifies only if its source value matches the input, or it is a field-level rule
        // (no explicit source value) that passes the value through.
        var best = candidates
            .OrderByDescending(r => (int)r.Scope)
            .ThenByDescending(r => ValueMatches(r, value, outbound) ? 1 : 0)
            .ThenByDescending(r => SourceValue(r, outbound) != null ? 1 : 0)
            .FirstOrDefault(r => ValueMatches(r, value, outbound) || SourceValue(r, outbound) == null);

        // Rules exist for this field but none matches this specific value: report a miss so callers
        // pass the value through unchanged rather than crashing.
        if (best is null)
            return MappingResult.Miss($"No mapping rule matches value '{value}' for field '{field}' ({ctx.Provider}).");

        var mapped = TargetValue(best, outbound);

        // A field-level rule (no explicit source/target value) passes the value through unchanged.
        if (mapped is null && SourceValue(best, outbound) is null)
            mapped = value;

        if (mapped is null)
        {
            if (best.FallbackValue is not null)
                return MappingResult.Hit(best.FallbackValue, best.Id, best.Scope, fallback: true);
            if (best.IsRequired)
                return MappingResult.Miss($"Required field '{field}' could not be mapped and has no fallback.");
        }

        return MappingResult.Hit(mapped, best.Id, best.Scope);
    }

    private static bool DirectionAllows(MappingDirection dir, bool outbound) => dir switch
    {
        MappingDirection.ReadOnly => false,
        MappingDirection.Bidirectional => true,
        MappingDirection.PortalToProvider => outbound,
        MappingDirection.ProviderToPortal => !outbound,
        _ => false,
    };

    // Outbound resolves by the portal side; inbound resolves by the external side.
    private static string SourceField(FieldMapping r, bool outbound) => outbound ? r.PortalField : r.ExternalField;
    private static string? SourceValue(FieldMapping r, bool outbound) => outbound ? r.PortalValue : r.ExternalValue;
    private static string? TargetValue(FieldMapping r, bool outbound) => outbound ? r.ExternalValue : r.PortalValue;

    private static bool ValueMatches(FieldMapping r, string? value, bool outbound)
    {
        var src = SourceValue(r, outbound);
        return src is not null && string.Equals(src, value, StringComparison.OrdinalIgnoreCase);
    }

    // A rule that names a connection is that connection's rule at EVERY scope. Only the connection
    // scope used to check, so a queue or ticket-type rule written for one PSA account also decided
    // values for another account of the same provider that happened to use the same key.
    private static bool ScopeApplies(FieldMapping r, MappingContext ctx)
        => (r.PsaConnectionId is not { } owner || owner == ctx.PsaConnectionId) && ScopeMatches(r, ctx);

    private static bool ScopeMatches(FieldMapping r, MappingContext ctx) => r.Scope switch
    {
        MappingScope.PlatformDefault => true,
        MappingScope.ProviderDefault => true,
        MappingScope.ConnectionOverride => r.PsaConnectionId == ctx.PsaConnectionId,
        MappingScope.ClientCompanyOverride => r.ClientCompanyId is not null && r.ClientCompanyId == ctx.ClientCompanyId,
        MappingScope.QueueOrBoardOverride => r.QueueOrBoardKey is not null && r.QueueOrBoardKey == ctx.QueueOrBoardKey,
        MappingScope.TicketTypeOverride => r.TicketTypeKey is not null && r.TicketTypeKey == ctx.TicketTypeKey,
        MappingScope.CustomField => true,
        MappingScope.Conditional => true, // predicate evaluation is layered on in the admin phase
        _ => false,
    };
}
