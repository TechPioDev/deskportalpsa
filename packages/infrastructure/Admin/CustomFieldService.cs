using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// Which of a connection's PSA custom fields are brought in, under what name, and for whom.
///
/// The fields come from the PSA: none is named in the code. An administrator chooses, field by
/// field. Until then nothing of a field is stored; once chosen it is shown to staff, and to the
/// client only when that is said as well, as a separate decision that the audit log names.
/// </summary>
public sealed class CustomFieldService(DeskDbContext db, ICustomFieldSource source, IAuditWriter audit) : ICustomFieldService
{
    public const string ReadOnlyReason = "Read only: the portal does not write custom fields to a PSA, so a value is changed in the PSA and read from it.";
    public const int MaxLabel = 200;

    private async Task<PsaConnection> FindAsync(Guid connectionId, CancellationToken ct)
        => await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
           ?? throw new NotFoundException("PSA connection");

    private Task<List<PsaCustomField>> SavedAsync(PsaConnection connection, bool tracked, CancellationToken ct)
    {
        var rows = db.PsaCustomFields.Where(f => f.MspOrganizationId == connection.MspOrganizationId && f.PsaConnectionId == connection.Id);
        return (tracked ? rows : rows.AsNoTracking()).ToListAsync(ct);
    }

    /// <summary>What the PSA lists now, or null with the reason where it could not be asked.</summary>
    private async Task<(bool Supported, IReadOnlyList<ExternalFieldDefinition>? Listed, string? Note)> ListedAsync(Guid connectionId, CancellationToken ct)
    {
        const string savedOnly = "This shows the decisions already saved. A field can be changed or left; a new one cannot be chosen until the PSA can be asked.";
        try
        {
            var (supported, fields) = await source.ListCustomFieldsAsync(connectionId, ct);
            if (!supported) return (false, [], "This PSA's connector does not read custom fields.");
            var listed = fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Key))
                .GroupBy(f => f.Key, StringComparer.Ordinal).Select(g => g.First())
                .ToList();
            return (true, listed, null);
        }
        catch (ConnectorException)
        {
            return (true, null, "The PSA's custom fields could not be read just now. " + savedOnly);
        }
        catch (DeskException ex) when (ex is not NotFoundException)
        {
            // The portal itself would not ask: the connection is switched off, or has no usable keys. Its own words say which.
            return (true, null, $"The PSA was not asked for its custom fields: {ex.Message} " + savedOnly);
        }
    }

    public async Task<CustomFieldSettingsDto> GetAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var saved = (await SavedAsync(connection, tracked: false, ct)).ToDictionary(f => f.ExternalKey, StringComparer.Ordinal);
        var (supported, listed, note) = await ListedAsync(connection.Id, ct);
        return Dto(connection, supported, listed, saved, note);
    }

    private static CustomFieldSettingsDto Dto(
        PsaConnection connection, bool supported, IReadOnlyList<ExternalFieldDefinition>? listed, IReadOnlyDictionary<string, PsaCustomField> saved, string? note)
    {
        var fields = new List<CustomFieldDto>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in listed ?? [])
        {
            known.Add(f.Key);
            var label = string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label.Trim();
            saved.TryGetValue(f.Key, out var row);
            fields.Add(new CustomFieldDto(f.Key, label, TypeOf(f.DataType), true,
                row?.Import ?? false, row?.PortalLabel ?? label, row is { Import: true, ClientVisible: true }, false, ReadOnlyReason));
        }
        // A decision about a field the PSA no longer lists (renamed, removed) or could not be asked
        // about is still shown: it is still in force, and still to be undone here.
        foreach (var row in saved.Values.Where(r => !known.Contains(r.ExternalKey)))
            fields.Add(new CustomFieldDto(row.ExternalKey, row.ExternalLabel, TypeOf(row.DataType), false,
                row.Import, row.PortalLabel, row.Import && row.ClientVisible, false, ReadOnlyReason));

        var ordered = fields
            .OrderBy(f => f.Import ? 0 : 1)
            .ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();
        var notes = new List<string>();
        if (note is not null) notes.Add(note);
        return new CustomFieldSettingsDto(connection.Id, connection.Name, connection.Provider.ToString(), supported, ordered,
            ordered.Count(f => f.Import), ordered.Count(f => f.ClientVisible), notes);
    }

    private static string TypeOf(string? dataType)
        => CustomFieldTypes.All.Contains((dataType ?? "").ToLowerInvariant()) ? dataType!.ToLowerInvariant() : CustomFieldTypes.Text;

    public async Task<CustomFieldSettingsDto> SaveAsync(Guid connectionId, IReadOnlyList<SetCustomFieldInput> changes, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        changes ??= [];
        var saved = (await SavedAsync(connection, tracked: true, ct)).ToDictionary(f => f.ExternalKey, StringComparer.Ordinal);
        var (supported, listed, note) = await ListedAsync(connection.Id, ct);
        var definitions = (listed ?? []).ToDictionary(f => f.Key, StringComparer.Ordinal);

        // Everything wrong, said together.
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new List<(SetCustomFieldInput Input, string Key, string Label)>();
        foreach (var change in changes)
        {
            var key = change.Key?.Trim() ?? "";
            if (key.Length == 0) { problems.Add("A field has no key."); continue; }
            if (!seen.Add(key)) { problems.Add($"The field {key} is in the list twice."); continue; }
            var isKnown = definitions.ContainsKey(key) || saved.ContainsKey(key);
            if (!isKnown)
            {
                // A field cannot be invented, and one cannot be chosen blind while the PSA is silent.
                problems.Add(listed is null
                    ? $"The field {key} has no decision saved, and the PSA cannot be asked about it just now."
                    : $"The PSA does not list a custom field {key}.");
                continue;
            }
            var psaLabel = definitions.TryGetValue(key, out var d) && !string.IsNullOrWhiteSpace(d.Label) ? d.Label.Trim() : saved.GetValueOrDefault(key)?.ExternalLabel ?? key;
            var label = string.IsNullOrWhiteSpace(change.PortalLabel) ? psaLabel : change.PortalLabel.Trim();
            if (label.Length > MaxLabel) problems.Add($"The name for {psaLabel} is longer than {MaxLabel} characters.");
            if (change.ClientVisible && !change.Import)
                problems.Add($"{psaLabel} is set to be shown to clients and also to be ignored. A field that is not brought in is shown to nobody.");
            wanted.Add((change, key, label));
        }

        // Two imported fields under one name would be two rows a reader cannot tell apart.
        var after = saved.Values.Where(r => r.Import && !seen.Contains(r.ExternalKey)).Select(r => r.PortalLabel)
            .Concat(wanted.Where(w => w.Input.Import).Select(w => w.Label));
        foreach (var twice in after.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"More than one imported field would be called \"{twice.Key}\". Give each its own name.");
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems.Distinct()));

        var (imported, ignored, renamed, shownToClients, hiddenFromClients) = (new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        foreach (var (input, key, label) in wanted)
        {
            definitions.TryGetValue(key, out var definition);
            saved.TryGetValue(key, out var row);
            var clientVisible = input.Import && input.ClientVisible;
            if (row is null)
            {
                // Nothing was decided and nothing is being decided: no row is made to say "ignored".
                if (!input.Import) continue;
                row = new PsaCustomField
                {
                    MspOrganizationId = connection.MspOrganizationId, PsaConnectionId = connection.Id, ExternalKey = key,
                    ExternalLabel = Limit(definition?.Label, key), DataType = TypeOf(definition?.DataType), PortalLabel = label,
                };
                db.PsaCustomFields.Add(row);
                saved[key] = row;
            }
            else if (definition is not null)
            {
                // Keep what the PSA calls it, and its kind, as they are now.
                (row.ExternalLabel, row.DataType) = (Limit(definition.Label, key), TypeOf(definition.DataType));
            }

            if (row.Import != input.Import) (input.Import ? imported : ignored).Add(label);
            if (row.Import && input.Import && row.PortalLabel != label) renamed.Add($"{row.PortalLabel} → {label}");
            if (row.ClientVisible != clientVisible) (clientVisible ? shownToClients : hiddenFromClients).Add(label);
            (row.Import, row.PortalLabel, row.ClientVisible) = (input.Import, label, clientVisible);
        }

        if (imported.Count + ignored.Count + renamed.Count + shownToClients.Count + hiddenFromClients.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("customfields.changed", "PsaConnection", connection.Id.ToString(),
                new { connection.Name, imported, ignored, renamed, shownToClients, hiddenFromClients }, ct);
            // Named on its own as well: "who decided clients may see this" should be one search away.
            if (shownToClients.Count > 0)
                await audit.WriteAsync("customfields.shown_to_clients", "PsaConnection", connection.Id.ToString(),
                    new { connection.Name, fields = shownToClients }, ct);
        }
        else if (db.ChangeTracker.HasChanges())
        {
            // Only what the PSA calls a field, or its kind, moved on: kept, and not worth an audit entry.
            await db.SaveChangesAsync(ct);
        }

        return Dto(connection, supported, listed, saved, note);
    }

    private static string Limit(string? label, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(label) ? fallback : label.Trim();
        return text.Length <= MaxLabel ? text : text[..MaxLabel];
    }
}
