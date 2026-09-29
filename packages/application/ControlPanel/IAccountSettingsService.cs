using Desk.Application.Tickets;

namespace Desk.Application.ControlPanel;

/// <summary>
/// Per-account configuration the client maintains (CP-2): approvers, escalation path, business
/// hours, holidays and devices. All scoped to the caller's account; each section is gated by the
/// caller's control-panel access (administrator, or an explicit grant for that section).
/// </summary>
public interface IAccountSettingsService
{
    Task<AccountDto> GetAccountAsync(ClientAccess access, CancellationToken ct = default);

    Task<IReadOnlyList<ApproverDto>> ListApproversAsync(ClientAccess access, CancellationToken ct = default);
    Task<ApproverDto> SaveApproverAsync(ClientAccess access, ApproverInput input, CancellationToken ct = default);
    Task DeleteApproverAsync(ClientAccess access, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<EscalationLevelDto>> ListEscalationAsync(ClientAccess access, CancellationToken ct = default);
    Task<EscalationLevelDto> SaveEscalationAsync(ClientAccess access, EscalationLevelInput input, CancellationToken ct = default);
    Task DeleteEscalationAsync(ClientAccess access, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<HolidayDto>> ListHolidaysAsync(ClientAccess access, CancellationToken ct = default);
    Task<HolidayDto> SaveHolidayAsync(ClientAccess access, HolidayInput input, CancellationToken ct = default);
    Task DeleteHolidayAsync(ClientAccess access, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<DeviceDto>> ListDevicesAsync(ClientAccess access, CancellationToken ct = default);
    Task<DeviceDetailDto> GetDeviceAsync(ClientAccess access, Guid id, CancellationToken ct = default);
    Task<DeviceDto> SaveDeviceAsync(ClientAccess access, DeviceInput input, CancellationToken ct = default);
    Task DeleteDeviceAsync(ClientAccess access, Guid id, CancellationToken ct = default);

    Task<BusinessHoursDto> GetBusinessHoursAsync(ClientAccess access, CancellationToken ct = default);
    Task<BusinessHoursDto> SaveBusinessHoursAsync(ClientAccess access, BusinessHoursInput input, CancellationToken ct = default);

    /// <summary>Pulls the account's contacts and devices from its PSA and upserts them locally.</summary>
    Task<PsaImportResult> ImportFromPsaAsync(ClientAccess access, CancellationToken ct = default);

    /// <summary>Live PSA view of the account: agreements/contracts + the queues its tickets flow through.</summary>
    Task<AccountPsaViewDto> PsaViewAsync(ClientAccess access, CancellationToken ct = default);

    /// <summary>Imports the provider's holiday calendar into the account's holidays (no duplicates).</summary>
    Task<HolidayImportResult> ImportHolidaysFromPsaAsync(ClientAccess access, CancellationToken ct = default);
}
