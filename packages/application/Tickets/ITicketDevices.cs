namespace Desk.Application.Tickets;

/// <summary>A device a technician can point a ticket at, and whether they can.</summary>
public sealed record DeviceChoiceDto(
    Guid Id, string Name, string? Type, string? Identifier, bool IsActive, bool FromPsa,
    /// <summary>Why this device cannot be chosen for this ticket, when it cannot.</summary>
    string? Unavailable);

/// <summary>A device a client can name when raising a ticket: just enough to recognise it.</summary>
public sealed record ClientDeviceChoiceDto(Guid Id, string Name, string? Type);

public interface ITicketDeviceService
{
    Task<IReadOnlyList<DeviceChoiceDto>> ChoicesAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default);

    /// <summary>Points the ticket at a device, or at none; on a PSA ticket the PSA is told first.</summary>
    Task<TicketDeviceDto?> SetAsync(Guid appUserId, Guid ticketId, Guid? deviceId, CancellationToken ct = default);

    /// <summary>The company's devices a new ticket can name.</summary>
    Task<IReadOnlyList<ClientDeviceChoiceDto>> ClientChoicesAsync(ClientAccess access, CancellationToken ct = default);
}
