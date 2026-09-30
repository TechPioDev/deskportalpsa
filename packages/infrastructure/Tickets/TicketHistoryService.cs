using System.Globalization;
using System.Text.Json;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// What happened to a ticket, in order, from the records the portal already keeps: the handover rows
/// for who passed it to whom (with what they said), and the audit trail for everything else. Nothing
/// is written here, and nothing is inferred beyond the ticket's own dates.
///
/// Handovers come from their own rows rather than the matching audit entry, which would say the same
/// thing twice with less in it.
/// </summary>
public sealed class TicketHistoryService(DeskDbContext db) : ITicketHistoryService
{
    public async Task<IReadOnlyList<TicketHistoryEntry>> ForAsync(Ticket ticket, CancellationToken ct = default)
    {
        var id = ticket.Id.ToString();
        var audits = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == "Ticket" && a.EntityId == id)
            .Select(a => new { a.Action, a.ActorDisplayName, a.CreatedAt, a.DetailJson })
            .ToListAsync(ct);
        var handovers = await db.TicketAssignments.AsNoTracking()
            .Where(h => h.TicketId == ticket.Id)
            .Select(h => new { h.FromAppUserId, h.ToAppUserId, h.AssignedByUserId, h.Note, h.CreatedAt })
            .ToListAsync(ct);

        var people = handovers.SelectMany(h => new[] { h.FromAppUserId, h.ToAppUserId, h.AssignedByUserId })
            .Append(ticket.CreatedByUserId)
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var names = people.Count == 0 ? new Dictionary<Guid, string>()
            : await db.AppUsers.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Name(Guid? who) => who is { } w && names.TryGetValue(w, out var n) ? n : null;

        var entries = new List<TicketHistoryEntry>();

        // Raised. Written as an audit from 30 Sep 2026; before that the ticket's own dates say it.
        if (!audits.Any(a => a.Action == "ticket.created"))
            entries.Add(ticket.Origin == TicketOrigin.Psa
                ? new TicketHistoryEntry(ticket.PsaCreatedAt ?? ticket.CreatedAt, null, "created", "Raised in the PSA")
                : new TicketHistoryEntry(ticket.CreatedAt, Name(ticket.CreatedByUserId), "created",
                    ticket.Origin == TicketOrigin.Rmm && ticket.CreatedByUserId is null ? "Opened by a monitoring alert" : "Raised"));

        foreach (var h in handovers)
        {
            var to = Name(h.ToAppUserId) ?? "someone no longer here";
            entries.Add(new TicketHistoryEntry(h.CreatedAt, Name(h.AssignedByUserId), "assigned",
                h.FromAppUserId is { } from ? $"Passed from {Name(from) ?? "someone no longer here"} to {to}" : $"Assigned to {to}",
                string.IsNullOrWhiteSpace(h.Note) ? null : h.Note));
        }

        foreach (var a in audits)
        {
            if (a.Action == "ticket.assigned.portal") continue; // told better by the handover row above
            var d = Parse(a.DetailJson);
            var (kind, summary) = a.Action switch
            {
                "ticket.created" => ("created", "Raised" + (Str(d, "board") is { } b ? $" on {b}" : "")),
                "ticket.status.changed" => ("status", $"Status {Pretty(Str(d, "from"))} → {Pretty(Str(d, "to"))}"
                    + (Bool(d, "resolutionRecorded") ? ", with a resolution" : "")),
                "ticket.reopened" => ("reopened", $"Reopened: {Pretty(Str(d, "from"))} → {Pretty(Str(d, "to"))}"),
                "ticket.edited" => ("edited", Edited(d)),
                "ticket.assigned.team" => ("team", Str(d, "Name") is { } team ? $"Passed to the {team} team" : "Passed to a team"),
                "ticket.unassigned.team" => ("team", "Taken off its team"),
                "ticket.assigned" => ("assigned", "Reassigned in the PSA"),
                "ticket.time.logged" => ("time", $"Logged {Hours(d, "Hours")}"),
                "ticket.time.edited" => ("time", $"Changed time {Hours(Obj(d, "from"), "Hours")} → {Hours(Obj(d, "to"), "Hours")}"),
                "ticket.time.deleted" => ("time", $"Removed {Hours(d, "Hours")} of time"),
                "ticket.resynced" => ("other", "Sent to the PSA again"),
                "ticket.resync_failed" => ("other", "Sending to the PSA failed"),
                "ticket.follower.added" => ("other", "Started following"),
                "ticket.follower.removed" => ("other", "Stopped following"),
                _ => ("other", a.Action.Replace("ticket.", "").Replace('.', ' ').Replace('_', ' ')),
            };
            entries.Add(new TicketHistoryEntry(a.CreatedAt, a.ActorDisplayName, kind, summary));
        }

        return entries.OrderByDescending(e => e.At).ToList();
    }

    private static readonly Dictionary<string, string> FieldNames = new()
    {
        ["title"] = "title", ["description"] = "description", ["priority"] = "priority", ["dueAt"] = "due date",
        ["topicId"] = "topic", ["category"] = "category", ["departmentId"] = "department", ["clientCompanyId"] = "client",
    };

    /// <summary>"Changed priority (NORMAL → HIGH), due date and description". Values are shown only where they read well.</summary>
    private static string Edited(JsonElement? d)
    {
        if (d is not { ValueKind: JsonValueKind.Object } o) return "Changed the details";
        var parts = new List<string>();
        foreach (var p in o.EnumerateObject())
        {
            var field = FieldNames.GetValueOrDefault(p.Name, p.Name);
            if (p.Name is "priority" or "category" or "title"
                && p.Value.TryGetProperty("from", out var f) && p.Value.TryGetProperty("to", out var t))
                parts.Add($"{field} ({Pretty(Text(f))} → {Pretty(Text(t))})");
            else
                parts.Add(field);
        }
        return parts.Count == 0 ? "Changed the details"
            : "Changed " + (parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1]);
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Obj(JsonElement? d, string name)
        => Prop(d, name) is { ValueKind: JsonValueKind.Object } o ? o : null;

    private static JsonElement? Prop(JsonElement? d, string name)
    {
        if (d is not { ValueKind: JsonValueKind.Object } o) return null;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement? d, string name) => Prop(d, name) is { } v ? Text(v) : null;
    private static bool Bool(JsonElement? d, string name) => Prop(d, name) is { ValueKind: JsonValueKind.True };

    private static string? Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.GetRawText(),
    };

    private static string Hours(JsonElement? d, string name)
    {
        var v = Prop(d, name);
        return v is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var h)
            ? h.ToString("0.##", CultureInfo.InvariantCulture) + "h"
            : "some time";
    }

    /// <summary>IN_PROGRESS → "In progress". Nothing → "none".</summary>
    private static string Pretty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var words = value.Replace('_', ' ').Trim().ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}
