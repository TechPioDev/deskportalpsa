using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.ControlPanel;
using Desk.Domain.ControlPanel;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.ControlPanel;

/// <summary>
/// Keeps each client's device list in step with the PSA, so nobody has to type it in.
///
/// A device is matched on the PSA's own id. A device already on the list from before - added by hand,
/// or by the old one-off import - is adopted by serial, then by name, rather than duplicated. One the
/// PSA stops listing is marked inactive, never deleted: tickets still point at it, and "that laptop was
/// retired in March" is itself worth seeing next to its history.
///
/// Last, tickets that arrived naming a device before the device itself was synced are linked up.
/// </summary>
public sealed class DeviceSyncService(
    DeskDbContext db, IConnectorResolver connectors, TimeProvider clock, ILogger<DeviceSyncService> logger) : IDeviceSyncService
{
    public async Task<DeviceSyncResult> SyncConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var companies = await db.ClientCompanies
            .Where(c => c.PsaConnectionId == connectionId && c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        if (companies.Count == 0) return DeviceSyncResult.None;

        var connector = await connectors.ResolveAsync(connectionId, ct);
        var total = DeviceSyncResult.None;
        foreach (var company in companies)
        {
            try
            {
                total += await SyncAsync(company, connector, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not sync devices for client {Company}", company.Name);
                total += new DeviceSyncResult(0, 0, 0, 0, 1);
            }
        }
        return total;
    }

    public async Task<DeviceSyncResult> SyncCompanyAsync(Guid clientCompanyId, CancellationToken ct = default)
    {
        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == clientCompanyId, ct)
            ?? throw new NotFoundException("Client");
        return await SyncAsync(company, await connectors.ResolveAsync(company.PsaConnectionId, ct), ct);
    }

    private async Task<DeviceSyncResult> SyncAsync(ClientCompany company, IServiceManagementConnector connector, CancellationToken ct)
    {
        // A company with no real id in the PSA (Autotask's "Company 0") has no devices to ask for.
        if (!long.TryParse(company.ExternalCompanyId, out var externalId) || externalId <= 0)
            return DeviceSyncResult.None;

        var incoming = await connector.GetDevicesAsync(company.ExternalCompanyId, ct);
        var rows = await db.Devices.Where(d => d.ClientCompanyId == company.Id).ToListAsync(ct);
        var now = clock.GetUtcNow();
        int created = 0, updated = 0, retired = 0;
        var seen = new HashSet<Guid>();

        foreach (var device in incoming)
        {
            var row = rows.FirstOrDefault(r => r.PsaConnectionId == company.PsaConnectionId && r.ExternalId == device.ExternalId)
                      ?? rows.FirstOrDefault(r => r.ExternalId is null && !string.IsNullOrWhiteSpace(device.Identifier)
                                                  && string.Equals(r.Identifier, device.Identifier, StringComparison.OrdinalIgnoreCase))
                      ?? rows.FirstOrDefault(r => r.ExternalId is null && string.Equals(r.Name, device.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new Device { MspOrganizationId = company.MspOrganizationId, ClientCompanyId = company.Id, Name = device.Name };
                db.Devices.Add(row);
                rows.Add(row);
                created++;
            }
            else
            {
                updated++;
            }

            // Cut to the columns' sizes: a PSA's free-text title can be longer than the portal keeps.
            row.Name = Clip(device.Name, 200)!;
            row.Type = Clip(device.Type, 100) ?? row.Type;
            row.Identifier = Clip(device.Identifier, 200) ?? row.Identifier;
            row.IsActive = device.IsActive;
            row.WarrantyExpiresAt = device.WarrantyExpiresAt;
            row.PsaConnectionId = company.PsaConnectionId;
            row.ExternalId = device.ExternalId;
            row.LastSyncedAt = now;
            seen.Add(row.Id);
        }

        foreach (var gone in rows.Where(r => r.PsaConnectionId == company.PsaConnectionId && r.ExternalId is not null
                                             && r.IsActive && !seen.Contains(r.Id)))
        {
            gone.IsActive = false;
            retired++;
        }
        await db.SaveChangesAsync(ct);

        // Tickets that arrived naming one of these devices before it was here.
        var byExternal = rows.Where(r => r.PsaConnectionId == company.PsaConnectionId && r.ExternalId is not null)
            .GroupBy(r => r.ExternalId!).ToDictionary(g => g.Key, g => g.First().Id);
        var waiting = await db.Tickets
            .Where(t => t.ClientCompanyId == company.Id && t.PsaConnectionId == company.PsaConnectionId
                        && t.DeviceExternalId != null && t.DeviceId == null)
            .ToListAsync(ct);
        var linked = 0;
        foreach (var ticket in waiting)
        {
            if (byExternal.TryGetValue(ticket.DeviceExternalId!, out var deviceId))
            {
                ticket.DeviceId = deviceId;
                linked++;
            }
        }
        if (linked > 0) await db.SaveChangesAsync(ct);

        return new DeviceSyncResult(created, updated, retired, linked, 0);
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Every enabled connection of every organization, each in its own tenant scope.</summary>
public sealed class DeviceSyncRunner(IServiceScopeFactory scopes, ILogger<DeviceSyncRunner> logger) : IDeviceSyncRunner
{
    public async Task<DeviceSyncResult> RunAllAsync(CancellationToken ct = default)
    {
        List<(Guid Id, Guid Org, string Name)> connections;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            connections = (await db.PsaConnections.AsNoTracking()
                    .Where(c => c.IsEnabled)
                    .Select(c => new { c.Id, c.MspOrganizationId, c.Name })
                    .ToListAsync(ct))
                .Select(c => (c.Id, c.MspOrganizationId, c.Name)).ToList();
        }

        var total = DeviceSyncResult.None;
        foreach (var (id, org, name) in connections)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org);
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<IDeviceSyncService>().SyncConnectionAsync(id, ct);
                logger.LogInformation(
                    "Devices for {Connection}: {Created} new, {Updated} refreshed, {Retired} retired, {Linked} tickets linked, {Failed} clients failed",
                    name, result.Created, result.Updated, result.Retired, result.TicketsLinked, result.CompaniesFailed);
                total += result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One connection failing - bad credentials, say - must not stop the others.
                logger.LogWarning(ex, "Device sync failed for {Connection}", name);
            }
        }
        return total;
    }
}
