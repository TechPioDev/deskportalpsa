using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// How well a connection's mapping covers what its PSA actually sends.
///
/// A value no rule maps passes through as the PSA wrote it, and looks exactly like a mapping that
/// let it through on purpose: the portal shows something plausible and nobody finds out. A status
/// called "Complete" then counts as open work, because nothing told the portal it was a way of
/// saying closed. The sync logs such a value; a log is not somewhere an administrator looks.
///
/// Nothing here is stored. A ticket keeps the status and priority it arrived with beside the
/// portal's, so what is unmapped is worked out from the tickets and the rules as they are now. It
/// cannot drift from them, and it needs no register to be kept in step.
/// </summary>
public sealed partial class ConnectionAdminService
{
    public const string Blocking = "Blocking";
    public const string Warning = "Warning";
    public const string Optional = "Optional";

    /// <summary>One value of a field, as tickets hold it and as the PSA lists it.</summary>
    private sealed record SeenValue(string Value, int Tickets, int Unmapped, string? MapsTo, bool Fallback, bool Listed);

    /// <summary>Tickets of one connection that share everything a mapping rule can depend on.</summary>
    private sealed record TicketGroup(string? Status, string? Priority, string? Queue, Guid? Client, int Count);

    private async Task<List<TicketGroup>> TicketGroupsAsync(Guid connectionId, CancellationToken ct)
        => (await db.Tickets.AsNoTracking()
                .Where(t => t.PsaConnectionId == connectionId)
                .GroupBy(t => new { t.PsaStatus, t.PsaPriority, t.QueueOrBoard, t.ClientCompanyId })
                .Select(g => new { g.Key.PsaStatus, g.Key.PsaPriority, g.Key.QueueOrBoard, g.Key.ClientCompanyId, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new TicketGroup(g.PsaStatus, g.PsaPriority, g.QueueOrBoard, g.ClientCompanyId, g.Count))
            .ToList();

    /// <summary>The context the sync gives the engine for a ticket, so the answer here is the answer there.</summary>
    private static MappingContext ContextOf(Desk.Domain.Tenancy.PsaConnection connection, string? queue, Guid? client) => new()
    {
        Provider = connection.Provider, PsaConnectionId = connection.Id, QueueOrBoardKey = queue, ClientCompanyId = client,
    };

    public async Task<MappingHealthDto> MappingHealthAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var rules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var notes = new List<string>();

        // What the PSA lists, where it can be asked. The health of the mapping is still worth
        // having when it cannot: what tickets hold is in the portal already.
        ConnectionFieldsDto? fields = null;
        try
        {
            fields = await GetFieldsAsync(connectionId, ct);
        }
        catch (Exception ex) when (ex is ConnectorException or DeskException)
        {
            notes.Add("The PSA's own lists could not be read just now, so this shows the values tickets have arrived with and not the ones the PSA lists besides.");
        }

        var groups = await TicketGroupsAsync(connectionId, ct);
        var total = groups.Sum(g => g.Count);

        MappingResult Inbound(string field, string value, TicketGroup? g)
            => _mapping.MapToPortal(rules, ContextOf(connection, g?.Queue, g?.Client), field, value);

        MappingFieldHealthDto Field(string field, string name, Func<TicketGroup, string?> of, IReadOnlyList<FieldOptionDto>? listed, bool isStatus)
        {
            var values = new Dictionary<string, SeenValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var value = of(g)?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                var result = Inbound(field, value, g);
                var seen = values.GetValueOrDefault(value) ?? new SeenValue(value, 0, 0, null, false, false);
                values[value] = seen with
                {
                    Tickets = seen.Tickets + g.Count,
                    Unmapped = seen.Unmapped + (result.Resolved ? 0 : g.Count),
                    MapsTo = seen.MapsTo ?? (result.Resolved ? result.Value : null),
                    Fallback = seen.Fallback || (result.Resolved && result.UsedFallback),
                };
            }
            foreach (var option in listed ?? [])
            {
                var value = option.SyncValue.Trim();
                if (value.Length == 0) continue;
                if (values.TryGetValue(value, out var seen))
                {
                    values[value] = seen with { Listed = true };
                    continue;
                }
                var result = Inbound(field, value, null);
                values[value] = new SeenValue(value, 0, 0, result.Resolved ? result.Value : null, result.Resolved && result.UsedFallback, true);
            }

            var items = values.Values
                .Select(v => new MappingValueDto(v.Value, v.MapsTo, v.Fallback, v.Tickets, v.Unmapped, v.Listed,
                    // What the portal does with tickets in a status nothing maps: it reads the PSA's
                    // word for it, and only "resolved" or "closed" in that word means finished.
                    isStatus && v.Unmapped > 0 ? (TicketStatusRules.Finished(v.Value) ? "Finished" : "Open") : null))
                .OrderBy(i => i.UnmappedTickets > 0 ? 0 : i.MapsTo is null ? 1 : 2)
                .ThenByDescending(i => i.Tickets)
                .ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var mapped = items.Count(i => i.MapsTo is not null && i.UnmappedTickets == 0);
            return new MappingFieldHealthDto(field, name, items.Count, mapped,
                items.Count == 0 ? null : Math.Round(100.0 * mapped / items.Count, 1),
                items.Sum(i => i.UnmappedTickets), items);
        }

        var statuses = Field("status", "Statuses", g => g.Status, fields?.Statuses, isStatus: true);
        var priorities = Field("priority", "Priorities", g => g.Priority, fields?.Priorities, isStatus: false);

        // A ticket is counted once however many of its values nothing maps.
        var unmappedTickets = groups
            .Where(g => (!string.IsNullOrWhiteSpace(g.Status) && !Inbound("status", g.Status!.Trim(), g).Resolved)
                     || (!string.IsNullOrWhiteSpace(g.Priority) && !Inbound("priority", g.Priority!.Trim(), g).Resolved))
            .Sum(g => g.Count);

        // The other direction: a status set in the portal has to become something the PSA accepts.
        var context = ContextOf(connection, null, null);
        var outbound = PortalVocabulary.Statuses.Select(status =>
        {
            var result = _mapping.MapToProvider(rules, context, "status", status);
            var sendsAs = result.Resolved ? result.Value : null;
            var known = fields is null || fields.Statuses.Count == 0 || sendsAs is null
                || fields.Statuses.Any(o => Same(o.Label, sendsAs) || Same(o.Value, sendsAs) || Same(o.SyncValue, sendsAs));
            return new OutboundStatusDto(status, sendsAs,
                sendsAs is null ? "Nothing maps it to a status in the PSA, so the PSA is sent the portal's own word and may refuse it."
                : !known ? $"It is sent as \"{sendsAs}\", which the PSA does not list."
                : null);
        }).ToList();

        var people = await TechnicianHealthAsync(connectionId, fields, ct);
        var withoutClient = await db.Tickets.AsNoTracking()
            .CountAsync(t => t.PsaConnectionId == connectionId
                && db.ClientCompanies.Any(c => c.Id == t.ClientCompanyId && c.ExternalCompanyId == "unknown"), ct);

        var level =
            statuses.Values > 0 && statuses.Mapped == 0 ? Blocking
            : unmappedTickets > 0 || outbound.Any(o => o.Problem is not null) ? Warning
            : statuses.Mapped < statuses.Values || priorities.Mapped < priorities.Values || people.Unlinked.Count > 0 ? Optional
            : Pass;

        return new MappingHealthDto(connection.Id, connection.Name, clock.GetUtcNow(), level,
            [statuses, priorities], outbound, people, total, unmappedTickets, withoutClient, notes);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Who in the PSA holds this connection's tickets, and which of them the portal knows as one of
    /// its own people. Linking is optional: an unlinked login is shown under the PSA's own name.
    /// </summary>
    private async Task<TechnicianHealthDto> TechnicianHealthAsync(Guid connectionId, ConnectionFieldsDto? fields, CancellationToken ct)
    {
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var holders = (await db.Tickets.AsNoTracking()
                .Where(t => t.PsaConnectionId == connectionId && t.AssignedTechnicianExternalId != null && t.AssignedTechnicianExternalId != "")
                .GroupBy(t => t.AssignedTechnicianExternalId!)
                .Select(g => new { Id = g.Key, Name = g.Max(t => t.AssignedTechnicianName), Tickets = g.Count() })
                .ToListAsync(ct))
            // The account the portal itself writes as is nobody.
            .Where(h => !account.IsAccount(connectionId, h.Id))
            .ToList();
        var linked = (await db.UserPsaIdentities.AsNoTracking()
                .Where(i => i.PsaConnectionId == connectionId)
                .Select(i => i.ExternalTechnicianId)
                .ToListAsync(ct))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var known = new Dictionary<string, (string Id, string? Name, int Tickets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in holders) known[h.Id.Trim()] = (h.Id.Trim(), h.Name, h.Tickets);
        foreach (var t in fields?.Technicians ?? [])
            if (!account.IsAccount(connectionId, t.Value) && !known.ContainsKey(t.Value.Trim()))
                known[t.Value.Trim()] = (t.Value.Trim(), t.Label, 0);

        // Logins an administrator has said to leave alone are not "still to link". A linked login
        // is linked whatever else was once said about it.
        var ignored = (await db.PsaTechnicianIgnores.AsNoTracking()
                .Where(i => i.PsaConnectionId == connectionId)
                .Select(i => i.ExternalTechnicianId)
                .ToListAsync(ct))
            .Select(id => id.Trim())
            .Where(id => known.ContainsKey(id) && !linked.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ignored) known.Remove(id);

        var isLinked = known.Values.Count(p => linked.Contains(p.Id));
        var unlinked = known.Values.Where(p => !linked.Contains(p.Id))
            .OrderByDescending(p => p.Tickets).ThenBy(p => p.Name ?? p.Id, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(p => new UnlinkedTechnicianDto(p.Id, p.Name, p.Tickets))
            .ToList();
        return new TechnicianHealthDto(known.Count, isLinked,
            known.Count == 0 ? null : Math.Round(100.0 * isLinked / known.Count, 1), unlinked, ignored.Count);
    }

    public async Task<IReadOnlyList<MappingPreviewRowDto>> MappingPreviewAsync(Guid connectionId, int take = 10, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 50);
        var connection = await FindAsync(connectionId, ct);
        var rules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);

        MappingPreviewRowDto Row(string reference, string title, string? status, string? priority, string? queue, Guid? client, bool fromPsa)
        {
            var context = ContextOf(connection, queue, client);
            string? Mapped(string field, string? value)
                => string.IsNullOrWhiteSpace(value) ? null
                    : _mapping.MapToPortal(rules, context, field, value.Trim()) is { Resolved: true } hit ? hit.Value : null;
            return new MappingPreviewRowDto(reference, title, status, Mapped("status", status), priority, Mapped("priority", priority), fromPsa);
        }

        // Tickets already here, as they arrived. Nothing is asked of the PSA for these.
        var stored = await db.Tickets.AsNoTracking()
            .Where(t => t.PsaConnectionId == connectionId)
            .OrderByDescending(t => t.LastSyncedAt).ThenBy(t => t.Id)
            .Take(take)
            .Select(t => new { t.ExternalTicketId, t.Title, t.PsaStatus, t.PsaPriority, t.QueueOrBoard, t.ClientCompanyId })
            .ToListAsync(ct);
        if (stored.Count > 0)
            return stored.Select(t => Row(t.ExternalTicketId ?? "", t.Title, t.PsaStatus, t.PsaPriority, t.QueueOrBoard, t.ClientCompanyId, fromPsa: false)).ToList();

        // Nothing imported yet - a connection being set up. A handful are read from the PSA to show
        // what the rules would make of them. Read, mapped in memory, and not kept.
        var connector = await ConnectorForAsync(connectionId, ct);
        var sample = await connector.GetTicketsAsync(new TicketFilter { PageSize = take }, ct);
        return sample.Items.Take(take)
            .Select(t => Row(t.ExternalId, t.Title, t.Status, t.Priority, t.QueueOrBoard, null, fromPsa: true))
            .ToList();
    }

    public async Task<MappingApplyResultDto> ApplyMappingAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var rules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var changes = new Dictionary<string, int>();

        // Only a ticket still showing the PSA's own word - the mark of a value that passed through
        // unmapped. A status someone set in the portal, or one a rule already translated, is not
        // this rule's to rewrite. Tickets are taken a batch at a time, so a connection of any size
        // is done in bounded steps.
        const int batchSize = 500;

        async Task<int> RewriteAsync(string field, string raw, (string? Queue, Guid? Client)? place, bool isStatus)
        {
            var result = _mapping.MapToPortal(rules, ContextOf(connection, place?.Queue, place?.Client), field, raw.Trim());
            if (!result.Resolved || string.IsNullOrEmpty(result.Value) || result.Value == raw) return 0;
            var changed = 0;
            while (true)
            {
                var scope = db.Tickets.Where(t => t.PsaConnectionId == connectionId);
                if (place is { } at) scope = scope.Where(t => t.QueueOrBoard == at.Queue && t.ClientCompanyId == at.Client);
                scope = isStatus
                    ? scope.Where(t => t.PsaStatus == raw && t.PortalStatus == raw)
                    : scope.Where(t => t.PsaPriority == raw && t.PortalPriority == raw);
                var batch = await scope.OrderBy(t => t.Id).Take(batchSize).ToListAsync(ct);
                if (batch.Count == 0) break;
                foreach (var ticket in batch)
                {
                    if (isStatus) ticket.PortalStatus = result.Value; else ticket.PortalPriority = result.Value;
                    ticket.Version++;
                }
                await db.SaveChangesAsync(ct);
                // Saved, so let go of: a ticket carries its whole description, and a connection of
                // any size must not be held in memory a batch after it was done.
                foreach (var ticket in batch) db.Entry(ticket).State = EntityState.Detached;
                changed += batch.Count;
                if (batch.Count < batchSize) break;
            }
            if (changed > 0) changes[$"{field}: {raw.Trim()} → {result.Value}"] = changes.GetValueOrDefault($"{field}: {raw.Trim()} → {result.Value}") + changed;
            return changed;
        }

        async Task<int> FieldAsync(string field, bool isStatus)
        {
            var held = db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == connectionId);
            held = isStatus
                ? held.Where(t => t.PsaStatus != null && t.PsaStatus != "" && t.PortalStatus == t.PsaStatus)
                : held.Where(t => t.PsaPriority != null && t.PsaPriority != "" && t.PortalPriority == t.PsaPriority);
            var total = 0;

            // A rule can be written for one client or one board, and the same word then means two
            // things on one connection: those tickets are taken a client and a board at a time.
            // Where no rule of the field is, every ticket holding a value maps the same way and
            // they are rewritten together - one pass a value, not one for each client on each board,
            // which on a PSA of two thousand clients was tens of thousands of round trips.
            var byPlace = rules.Any(r => r.IsActive && string.Equals(r.ExternalField, field, StringComparison.OrdinalIgnoreCase)
                && r.Scope is MappingScope.ClientCompanyOverride or MappingScope.QueueOrBoardOverride);
            if (!byPlace)
            {
                var values = await (isStatus ? held.Select(t => t.PsaStatus!) : held.Select(t => t.PsaPriority!)).Distinct().ToListAsync(ct);
                foreach (var raw in values) total += await RewriteAsync(field, raw, null, isStatus);
                return total;
            }

            var places = await (isStatus
                    ? held.Select(t => new { Raw = t.PsaStatus!, t.QueueOrBoard, t.ClientCompanyId })
                    : held.Select(t => new { Raw = t.PsaPriority!, t.QueueOrBoard, t.ClientCompanyId }))
                .Distinct()
                .ToListAsync(ct);
            foreach (var g in places) total += await RewriteAsync(field, g.Raw, (g.QueueOrBoard, g.ClientCompanyId), isStatus);
            return total;
        }

        var statuses = await FieldAsync("status", isStatus: true);
        var priorities = await FieldAsync("priority", isStatus: false);

        var summary = changes.OrderByDescending(c => c.Value).Select(c => $"{c.Key} ({c.Value})").ToList();
        await audit.WriteAsync("mapping.applied", "PsaConnection", connectionId.ToString(),
            new { connection.Name, statuses, priorities, changes = summary }, ct);
        return new MappingApplyResultDto(statuses, priorities, summary);
    }
}
