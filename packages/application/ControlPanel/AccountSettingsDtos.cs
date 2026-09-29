namespace Desk.Application.ControlPanel;

public sealed record AccountDto(
    Guid Id,
    string Name,
    string ExternalCompanyId,
    string? ConnectionName,
    bool IsActive);

public sealed record ApproverDto(Guid Id, string Name, string? Email, string? Phone, string? Scope, int SortOrder);
public sealed record ApproverInput(Guid? Id, string Name, string? Email, string? Phone, string? Scope, int SortOrder);

public sealed record EscalationLevelDto(Guid Id, int Level, string Name, string? Contact, string? Condition);
public sealed record EscalationLevelInput(Guid? Id, int Level, string Name, string? Contact, string? Condition);

public sealed record HolidayDto(Guid Id, string Date, string Name);
public sealed record HolidayInput(Guid? Id, string Date, string Name);

public sealed record DeviceDto(
    Guid Id, string Name, string? Type, string? Identifier, string? Notes,
    /// <summary>Synced from the PSA: its name, type, serial and warranty are the PSA's to change, and
    /// only the notes can be edited here.</summary>
    bool FromPsa = false,
    bool IsActive = true,
    DateTimeOffset? WarrantyExpiresAt = null,
    DateTimeOffset? LastSyncedAt = null,
    int OpenTickets = 0,
    int TotalTickets = 0);

/// <summary>A ticket raised about a device, as the device page lists it.</summary>
public sealed record DeviceTicketDto(Guid Id, string Reference, string Title, string Status, bool IsOpen, DateTimeOffset RaisedAt);

public sealed record DeviceDetailDto(DeviceDto Device, IReadOnlyList<DeviceTicketDto> Tickets);
public sealed record DeviceInput(Guid? Id, string Name, string? Type, string? Identifier, string? Notes);

public sealed record BusinessHoursDto(string? TimeZone, string ScheduleJson, string? Notes);
public sealed record BusinessHoursInput(string? TimeZone, string ScheduleJson, string? Notes);

/// <summary>Tally of a PSA import: how many contacts/devices were created vs updated.</summary>
public sealed record PsaImportResult(int UsersCreated, int UsersUpdated, int DevicesCreated, int DevicesUpdated);

public sealed record AgreementDto(string Name, string? Type, string? Status, DateTimeOffset? StartDate, DateTimeOffset? EndDate);

/// <summary>Supported=false: the provider has no holiday calendar — different from an empty one.</summary>
public sealed record HolidayImportResult(bool Supported, int Created, int Skipped);

/// <summary>
/// The PSA's view of this account, read live: the agreements/contracts that govern it, and the
/// queues its tickets actually flow through. Supported=false means the provider has no contract
/// concept; Unavailable=true means the provider could not be reached right now — two different
/// truths, stated separately, because the queue list (derived from already-synced tickets) is
/// still good either way and must not vanish with the provider.
/// </summary>
public sealed record AccountPsaViewDto(
    bool AgreementsSupported,
    IReadOnlyList<AgreementDto> Agreements,
    IReadOnlyList<string> MonitoredQueues,
    bool AgreementsUnavailable = false);
