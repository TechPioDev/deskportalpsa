using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Which device a ticket is about, as a technician sets it or a client names it when raising one.
///
/// The one rule: a PSA ticket's device has to be one the PSA knows. The PSA is the system of record
/// for its tickets, and a device typed into the portal by hand does not exist there - Autotask would
/// report "no device" at the next sync and the choice would quietly vanish. So a hand-added device can
/// be set on the team's own board tickets, and a PSA ticket offers the PSA's devices, saying why when
/// it cannot offer one.
/// </summary>
public sealed class TicketDeviceService(DeskDbContext db, ITicketScopeQuery scopeQuery, IConnectorResolver connectors)
    : ITicketDeviceService
{
    public async Task<IReadOnlyList<DeviceChoiceDto>> ChoicesAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await scopeQuery.FindAsync(db.Tickets, ticketId, appUserId, Permissions.TicketsViewAll, ct)
            ?? throw new NotFoundException("Ticket");
        if (ticket.ClientCompanyId is not { } company) return [];

        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.ClientCompanyId == company)
            .OrderByDescending(d => d.IsActive).ThenBy(d => d.Name)
            .ToListAsync(ct);
        return [.. devices.Select(d => new DeviceChoiceDto(d.Id, d.Name, d.Type, d.Identifier, d.IsActive, d.FromPsa, WhyNot(ticket, d)))];
    }

    public async Task<TicketDeviceDto?> SetAsync(Guid appUserId, Guid ticketId, Guid? deviceId, CancellationToken ct = default)
    {
        var ticket = await scopeQuery.FindAsync(db.Tickets, ticketId, appUserId, Permissions.TicketsUpdate, ct)
            ?? throw new NotFoundException("Ticket");
        if (ticket.ClientCompanyId is null)
            throw new ValidationFailedException("This ticket has no client, so there are no devices to choose from.");

        Device? device = null;
        if (deviceId is { } id)
        {
            // Only this ticket's client's devices; another company's is simply not found.
            device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.ClientCompanyId == ticket.ClientCompanyId, ct)
                ?? throw new NotFoundException("Device");
            if (WhyNot(ticket, device) is { } reason) throw new ValidationFailedException(reason);
        }

        var externalId = device is not null && IsPsaTicket(ticket) ? device.ExternalId : null;
        if (IsPsaTicket(ticket))
        {
            if (string.IsNullOrEmpty(ticket.ExternalTicketId))
                throw new ValidationFailedException("This ticket is not yet synced to the PSA.");
            var connector = await connectors.ResolveAsync(ticket.PsaConnectionId!.Value, ct);
            try
            {
                await connector.SetTicketDeviceAsync(ticket.ExternalTicketId, externalId, ticket.DeviceExternalId, ct);
            }
            catch (ConnectorException ex)
            {
                throw new ValidationFailedException($"The PSA did not accept the device: {ex.Message}");
            }
        }

        ticket.DeviceId = device?.Id;
        ticket.DeviceExternalId = externalId;
        await db.SaveChangesAsync(ct);
        return device is null ? null
            : new TicketDeviceDto(device.Id, device.Name, device.Type, device.Identifier, device.IsActive, device.WarrantyExpiresAt);
    }

    public async Task<IReadOnlyList<ClientDeviceChoiceDto>> ClientChoicesAsync(ClientAccess access, CancellationToken ct = default)
    {
        // A client's ticket goes to the PSA, so only the devices the PSA knows - and only in use.
        var connection = await db.ClientCompanies.AsNoTracking()
            .Where(c => c.Id == access.ClientCompanyId).Select(c => (Guid?)c.PsaConnectionId).FirstOrDefaultAsync(ct);
        return await db.Devices.AsNoTracking()
            .Where(d => d.ClientCompanyId == access.ClientCompanyId && d.IsActive
                        && d.ExternalId != null && d.PsaConnectionId == connection)
            .OrderBy(d => d.Name)
            .Select(d => new ClientDeviceChoiceDto(d.Id, d.Name, d.Type))
            .ToListAsync(ct);
    }

    private static bool IsPsaTicket(Ticket ticket) => ticket.Origin == TicketOrigin.Psa && ticket.PsaConnectionId is not null;

    /// <summary>Why this device cannot be this ticket's, or null when it can.</summary>
    private static string? WhyNot(Ticket ticket, Device device)
    {
        if (!device.IsActive) return $"{device.Name} is retired.";
        if (!IsPsaTicket(ticket)) return null;
        if (device.ExternalId is null)
            return $"{device.Name} was added by hand, so the PSA does not know it. Add it in the PSA and it arrives here with the next daily sync.";
        if (device.PsaConnectionId != ticket.PsaConnectionId)
            return $"{device.Name} comes from a different PSA connection than this ticket.";
        return null;
    }
}
