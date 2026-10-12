using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Tickets;
using Desk.Domain.Common;
using Desk.Domain.ControlPanel;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.ControlPanel;

/// <summary>
/// Per-account settings CRUD. Every query is tenant-isolated by the DbContext filter and further
/// constrained to the caller's account; each section enforces the caller's control-panel access
/// (administrator, or a grant for that section) and audits mutations.
/// </summary>
public sealed class AccountSettingsService(
    DeskDbContext db,
    IAuditWriter audit,
    Desk.Application.Connectors.IConnectorResolver connectors,
    IDeviceSyncService? deviceSync = null) : IAccountSettingsService
{
    // One way devices come in from the PSA, whether the daily sync or this button asked.
    private IDeviceSyncService DeviceSync => deviceSync ?? new DeviceSyncService(db, connectors, TimeProvider.System,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceSyncService>.Instance);

    public async Task<AccountPsaViewDto> PsaViewAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Accounts, ct);

        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Account");

        // Read from tickets already synced, not a live provider call: these are the queues the
        // account's work has actually flowed through, which is the honest version of "monitored".
        var queues = await db.Tickets
            .Where(t => t.ClientCompanyId == company.Id && t.QueueOrBoard != null && t.QueueOrBoard != "")
            .Select(t => t.QueueOrBoard!)
            .Distinct()
            .OrderBy(q => q)
            .ToListAsync(ct);

        // The provider half is best-effort: a broken connection must not take the queue list
        // (derived from already-synced tickets) down with it.
        try
        {
            var connector = await connectors.ResolveAsync(company.PsaConnectionId, ct);
            var caps = await connector.GetCapabilitiesAsync(ct);
            if (!caps.SupportsContracts)
                return new AccountPsaViewDto(false, [], queues);

            var agreements = await connector.GetAgreementsAsync(company.ExternalCompanyId, ct);
            return new AccountPsaViewDto(true,
                agreements.Select(a => new AgreementDto(a.Name, a.Type, a.Status, a.StartDate, a.EndDate)).ToList(),
                queues);
        }
        catch (Exception e) when (e is Desk.PsaCore.Contracts.ConnectorException or ValidationFailedException or NotFoundException)
        {
            return new AccountPsaViewDto(true, [], queues, AgreementsUnavailable: true);
        }
    }

    public async Task<HolidayImportResult> ImportHolidaysFromPsaAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Holidays, ct);

        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Account");
        var connector = await connectors.ResolveAsync(company.PsaConnectionId, ct);
        if (!(await connector.GetCapabilitiesAsync(ct)).SupportsHolidayCalendars)
            return new HolidayImportResult(false, 0, 0);

        var incoming = await connector.GetHolidaysAsync(ct);

        // Dedupe by DATE alone, not date+name: a client who renamed "Christmas Day" to "Xmas
        // closure" should not get a second row for the same closed day on every re-import.
        var existingDates = (await db.Holidays
                .Where(h => h.ClientCompanyId == company.Id)
                .Select(h => h.Date)
                .ToListAsync(ct))
            .ToHashSet();

        int created = 0, skipped = 0;
        foreach (var h in incoming)
        {
            if (existingDates.Contains(h.Date)) { skipped++; continue; }
            db.Holidays.Add(new Holiday
            {
                MspOrganizationId = company.MspOrganizationId,
                ClientCompanyId = company.Id,
                Date = h.Date,
                Name = h.Name,
            });
            existingDates.Add(h.Date);
            created++;
        }
        if (created > 0) await db.SaveChangesAsync(ct);

        await audit.WriteAsync("control_panel.holidays.imported_from_psa", nameof(Holiday), company.Id.ToString(),
            new { created, skipped }, ct);
        return new HolidayImportResult(true, created, skipped);
    }

    // ---- Import from the PSA (contacts → client users, devices/configurations → devices) ----

    public async Task<PsaImportResult> ImportFromPsaAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Accounts, ct);

        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Account");
        var connector = await connectors.ResolveAsync(company.PsaConnectionId, ct);

        int usersCreated = 0, usersUpdated = 0, devicesCreated = 0, devicesUpdated = 0;

        // Contacts → ClientUsers, matched on the PSA contact id so re-imports update rather than duplicate.
        var contacts = await connector.GetContactsAsync(company.ExternalCompanyId, ct);
        var existingUsers = await db.ClientUsers.Where(u => u.ClientCompanyId == company.Id).ToListAsync(ct);
        foreach (var c in contacts.Where(c => !string.IsNullOrWhiteSpace(c.Email)))
        {
            var row = existingUsers.FirstOrDefault(u => u.ExternalContactId == c.ExternalId)
                   ?? existingUsers.FirstOrDefault(u => string.Equals(u.Email, c.Email, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                db.ClientUsers.Add(new Desk.Domain.Tenancy.ClientUser
                {
                    MspOrganizationId = company.MspOrganizationId,
                    ClientCompanyId = company.Id,
                    Email = c.Email,
                    DisplayName = string.IsNullOrWhiteSpace(c.DisplayName) ? c.Email : c.DisplayName,
                    ExternalContactId = c.ExternalId,
                    IsActive = c.IsActive,
                });
                usersCreated++;
            }
            else
            {
                // Never touch IdpSubject/administrator flags — those are portal-owned, not PSA-owned.
                row.DisplayName = string.IsNullOrWhiteSpace(c.DisplayName) ? row.DisplayName : c.DisplayName;
                row.ExternalContactId = c.ExternalId;
                row.IsActive = c.IsActive;
                usersUpdated++;
            }
        }

        await db.SaveChangesAsync(ct);

        // Devices through the same sync the daily job runs, so the two can never match differently.
        var devices = await DeviceSync.SyncCompanyAsync(company.Id, ct);
        devicesCreated = devices.Created;
        devicesUpdated = devices.Updated;

        await audit.WriteAsync("control_panel.psa.import", "ClientCompany", company.Id.ToString(),
            new { usersCreated, usersUpdated, devicesCreated, devicesUpdated }, ct);
        return new PsaImportResult(usersCreated, usersUpdated, devicesCreated, devicesUpdated);
    }

    // ---- Account (read-only projection of the client company) ----

    public async Task<AccountDto> GetAccountAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Accounts, ct);
        var c = await db.ClientCompanies.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Account");
        var conn = await db.PsaConnections.AsNoTracking()
            .Where(p => p.Id == c.PsaConnectionId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        return new AccountDto(c.Id, c.Name, c.ExternalCompanyId, conn, c.IsActive);
    }

    // ---- Approvers ----

    public async Task<IReadOnlyList<ApproverDto>> ListApproversAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Approvers, ct);
        return await Scoped(db.Approvers, access).OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new ApproverDto(a.Id, a.Name, a.Email, a.Phone, a.Scope, a.SortOrder)).ToListAsync(ct);
    }

    public async Task<ApproverDto> SaveApproverAsync(ClientAccess access, ApproverInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Approvers, ct);
        var name = Required(input.Name, "Name");
        var row = await FindOrNew(db.Approvers, access, input.Id, () => new Approver { ClientCompanyId = access.ClientCompanyId, Name = name }, ct);
        row.Name = name; row.Email = Trim(input.Email); row.Phone = Trim(input.Phone); row.Scope = Trim(input.Scope); row.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.approver.save", nameof(Approver), row.Id.ToString(), null, ct);
        return new ApproverDto(row.Id, row.Name, row.Email, row.Phone, row.Scope, row.SortOrder);
    }

    public Task DeleteApproverAsync(ClientAccess access, Guid id, CancellationToken ct = default)
        => DeleteAsync(db.Approvers, access, id, ControlPanelSection.Approvers, "control_panel.approver.delete", nameof(Approver), ct);

    // ---- Escalation ----

    public async Task<IReadOnlyList<EscalationLevelDto>> ListEscalationAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Escalation, ct);
        return await Scoped(db.EscalationLevels, access).OrderBy(e => e.Level)
            .Select(e => new EscalationLevelDto(e.Id, e.Level, e.Name, e.Contact, e.Condition)).ToListAsync(ct);
    }

    public async Task<EscalationLevelDto> SaveEscalationAsync(ClientAccess access, EscalationLevelInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Escalation, ct);
        var name = Required(input.Name, "Name");
        var row = await FindOrNew(db.EscalationLevels, access, input.Id, () => new EscalationLevel { ClientCompanyId = access.ClientCompanyId, Name = name }, ct);
        row.Name = name; row.Level = input.Level < 1 ? 1 : input.Level; row.Contact = Trim(input.Contact); row.Condition = Trim(input.Condition);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.escalation.save", nameof(EscalationLevel), row.Id.ToString(), null, ct);
        return new EscalationLevelDto(row.Id, row.Level, row.Name, row.Contact, row.Condition);
    }

    public Task DeleteEscalationAsync(ClientAccess access, Guid id, CancellationToken ct = default)
        => DeleteAsync(db.EscalationLevels, access, id, ControlPanelSection.Escalation, "control_panel.escalation.delete", nameof(EscalationLevel), ct);

    // ---- Holidays ----

    public async Task<IReadOnlyList<HolidayDto>> ListHolidaysAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Holidays, ct);
        return await Scoped(db.Holidays, access).OrderBy(h => h.Date)
            .Select(h => new HolidayDto(h.Id, h.Date, h.Name)).ToListAsync(ct);
    }

    public async Task<HolidayDto> SaveHolidayAsync(ClientAccess access, HolidayInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Holidays, ct);
        var date = Required(input.Date, "Date");
        var name = Required(input.Name, "Name");
        var row = await FindOrNew(db.Holidays, access, input.Id, () => new Holiday { ClientCompanyId = access.ClientCompanyId, Date = date, Name = name }, ct);
        row.Date = date; row.Name = name;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.holiday.save", nameof(Holiday), row.Id.ToString(), null, ct);
        return new HolidayDto(row.Id, row.Date, row.Name);
    }

    public Task DeleteHolidayAsync(ClientAccess access, Guid id, CancellationToken ct = default)
        => DeleteAsync(db.Holidays, access, id, ControlPanelSection.Holidays, "control_panel.holiday.delete", nameof(Holiday), ct);

    // ---- Devices ----

    public async Task<IReadOnlyList<DeviceDto>> ListDevicesAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Accounts, ct);
        var devices = await Scoped(db.Devices, access).AsNoTracking()
            .OrderByDescending(d => d.IsActive).ThenBy(d => d.Name)
            .ToListAsync(ct);
        var counts = await DeviceTickets(access)
            .Where(t => t.DeviceId != null)
            .GroupBy(t => t.DeviceId!.Value)
            .Select(g => new { DeviceId = g.Key, Total = g.Count(), Open = g.Count(t => t.ClosedAt == null && t.ResolvedAt == null) })
            .ToListAsync(ct);
        var byDevice = counts.ToDictionary(c => c.DeviceId);
        return [.. devices.Select(d => Dto(d, byDevice.GetValueOrDefault(d.Id)?.Open ?? 0, byDevice.GetValueOrDefault(d.Id)?.Total ?? 0))];
    }

    public async Task<DeviceDetailDto> GetDeviceAsync(ClientAccess access, Guid id, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Accounts, ct);
        var device = await Scoped(db.Devices, access).AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Device");
        var tickets = (await DeviceTickets(access)
                .Where(t => t.DeviceId == id)
                .OrderByDescending(t => t.CreatedAt)
                .Take(200)
                .Select(t => new { t.Id, t.Number, t.ExternalTicketId, t.Title, t.PortalStatus, t.ClosedAt, t.ResolvedAt, t.PsaCreatedAt, t.CreatedAt })
                .ToListAsync(ct))
            .Select(t => new DeviceTicketDto(t.Id, t.Number ?? t.ExternalTicketId ?? "—", t.Title, t.PortalStatus,
                t.ClosedAt is null && t.ResolvedAt is null, t.PsaCreatedAt ?? t.CreatedAt))
            .ToList();
        return new DeviceDetailDto(Dto(device, tickets.Count(t => t.IsOpen), tickets.Count), tickets);
    }

    public async Task<DeviceDto> SaveDeviceAsync(ClientAccess access, DeviceInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Accounts, ct);
        var name = Required(input.Name, "Name");
        var row = await FindOrNew(db.Devices, access, input.Id, () => new Device { ClientCompanyId = access.ClientCompanyId, Name = name }, ct);
        // A synced device's details are the PSA's: an edit here would be overwritten tonight, which is
        // worse than refusing it. The notes are the client's own and stay editable.
        if (!row.FromPsa)
        {
            row.Name = name; row.Type = Trim(input.Type); row.Identifier = Trim(input.Identifier);
        }
        row.Notes = Trim(input.Notes);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.device.save", nameof(Device), row.Id.ToString(), null, ct);
        return Dto(row, 0, 0);
    }

    public async Task DeleteDeviceAsync(ClientAccess access, Guid id, CancellationToken ct = default)
    {
        var synced = await Scoped(db.Devices, access).AnyAsync(d => d.Id == id && d.ExternalId != null, ct);
        if (synced)
            throw new ValidationFailedException("This device comes from your PSA and would come straight back. Retire it there instead.");
        await DeleteAsync(db.Devices, access, id, ControlPanelSection.Accounts, "control_panel.device.delete", nameof(Device), ct);
    }

    /// <summary>The account's tickets a device page may list: the ones the client can see at all -
    /// provider tickets and monitoring boards shown to them, never the team's internal work.</summary>
    private IQueryable<Desk.Domain.Tickets.Ticket> DeviceTickets(ClientAccess access)
        => Desk.Infrastructure.Tickets.TicketReadService.ClientVisible(db, access with { IsCompanyAdministrator = true, GrantSeesAllTickets = null });

    private static DeviceDto Dto(Device d, int open, int total) => new(
        d.Id, d.Name, d.Type, d.Identifier, d.Notes, d.FromPsa, d.IsActive, d.WarrantyExpiresAt, d.LastSyncedAt, open, total);

    // ---- Business hours (single row per account) ----

    public async Task<BusinessHoursDto> GetBusinessHoursAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.BusinessHours, ct);
        var row = await Scoped(db.BusinessHours, access).AsNoTracking().FirstOrDefaultAsync(ct);
        return new BusinessHoursDto(row?.TimeZone, row?.ScheduleJson ?? "[]", row?.Notes);
    }

    public async Task<BusinessHoursDto> SaveBusinessHoursAsync(ClientAccess access, BusinessHoursInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.BusinessHours, ct);
        var row = await Scoped(db.BusinessHours, access).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new BusinessHours { ClientCompanyId = access.ClientCompanyId };
            db.BusinessHours.Add(row);
        }
        row.TimeZone = Trim(input.TimeZone);
        row.ScheduleJson = string.IsNullOrWhiteSpace(input.ScheduleJson) ? "[]" : input.ScheduleJson;
        row.Notes = Trim(input.Notes);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.business_hours.save", nameof(BusinessHours), row.Id.ToString(), null, ct);
        return new BusinessHoursDto(row.TimeZone, row.ScheduleJson, row.Notes);
    }

    // ---- Helpers ----

    private static IQueryable<T> Scoped<T>(DbSet<T> set, ClientAccess access) where T : BaseEntity, IAccountScoped
        => set.Where(x => x.ClientCompanyId == access.ClientCompanyId);

    private static async Task<T> FindOrNew<T>(DbSet<T> set, ClientAccess access, Guid? id, Func<T> factory, CancellationToken ct)
        where T : BaseEntity, IAccountScoped
    {
        if (id is null)
        {
            var created = factory();
            set.Add(created);
            return created;
        }
        return await Scoped(set, access).FirstOrDefaultAsync(x => x.Id == id, ct)
               ?? throw new NotFoundException(typeof(T).Name);
    }

    private async Task DeleteAsync<T>(DbSet<T> set, ClientAccess access, Guid id, ControlPanelSection section,
        string action, string entityType, CancellationToken ct) where T : BaseEntity, IAccountScoped
    {
        await EnsureSectionAsync(access, section, ct);
        var row = await Scoped(set, access).FirstOrDefaultAsync(x => x.Id == id, ct)
                  ?? throw new NotFoundException(entityType);
        set.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(action, entityType, id.ToString(), null, ct);
    }

    private async Task EnsureSectionAsync(ClientAccess access, ControlPanelSection section, CancellationToken ct)
    {
        // A company the person was only GIVEN is not theirs to run. The sections they may manage
        // were granted for their own company and are not kept company by company, so without this
        // a grant to look at a second company would have let them edit its control panel too.
        if (access.IsGranted) throw new ForbiddenException("The control panel is for your own company. Switch back to it to manage these settings.");
        if (access.IsCompanyAdministrator) return;
        var granted = await db.ClientAccessGrants.AsNoTracking()
            .AnyAsync(g => g.ClientUserId == access.ClientUserId && g.Section == section, ct);
        if (!granted) throw new ForbiddenException($"You do not have access to this section.");
    }

    private static string Required(string? v, string field)
    {
        var t = (v ?? "").Trim();
        if (t.Length == 0) throw new ValidationFailedException($"{field} is required.");
        return t;
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
