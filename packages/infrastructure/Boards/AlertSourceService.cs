using System.Security.Cryptography;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Boards;

/// <summary>
/// Which monitoring tools may open tickets here. Every change is audited, and the key itself is
/// generated here, shown once to the person setting the tool up, and never stored in a form anyone
/// can read back.
/// </summary>
public sealed class AlertSourceService(DeskDbContext db, ITenantContext tenant, ICurrentUser user, IAuditWriter audit) : IAlertSourceService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<AlertSourceDto>> ListAsync(CancellationToken ct = default)
        => await db.AlertSources.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new AlertSourceDto(
                s.Id, s.Name, s.BoardId, s.Board!.Name, s.Vendor, s.KeyHint, s.IsActive, s.CloseOnClear,
                s.LastReceivedAt, s.ReceivedCount, s.LastError))
            .ToListAsync(ct);

    public async Task<AlertSourceCreatedDto> CreateAsync(AlertSourceInput input, CancellationToken ct = default)
    {
        var name = Validate(input);
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == input.BoardId, ct)
            ?? throw new NotFoundException("Board");
        // Alerts belong on a monitoring board: that is the one kind a client may be shown, and the
        // one kind whose tickets nobody on the team raised by hand.
        if (board.Kind != BoardKind.Rmm)
            throw new ValidationFailedException("Choose a monitoring board. Alerts do not belong on a board the team raises work on.");

        var (key, hash, hint) = NewKey();
        var source = new AlertSource
        {
            MspOrganizationId = Org,
            Name = name,
            BoardId = board.Id,
            Vendor = input.Vendor,
            KeyHash = hash,
            KeyHint = hint,
            CloseOnClear = input.CloseOnClear,
            CreatedByUserId = user.UserId,
        };
        db.AlertSources.Add(source);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("alertsource.created", "AlertSource", source.Id.ToString(),
            new { source.Name, board = board.Name, source.Vendor, source.KeyHint }, ct);
        return new AlertSourceCreatedDto(await OneAsync(source.Id, ct), key);
    }

    public async Task<AlertSourceDto> UpdateAsync(Guid id, AlertSourceInput input, CancellationToken ct = default)
    {
        var source = await db.AlertSources.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Alert source");
        var name = Validate(input);
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == input.BoardId, ct) ?? throw new NotFoundException("Board");
        if (board.Kind != BoardKind.Rmm)
            throw new ValidationFailedException("Choose a monitoring board. Alerts do not belong on a board the team raises work on.");

        source.Name = name;
        source.BoardId = board.Id;
        source.Vendor = input.Vendor;
        source.CloseOnClear = input.CloseOnClear;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("alertsource.updated", "AlertSource", source.Id.ToString(),
            new { source.Name, board = board.Name, source.Vendor, source.CloseOnClear }, ct);
        return await OneAsync(source.Id, ct);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var source = await db.AlertSources.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Alert source");
        source.IsActive = active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(active ? "alertsource.activated" : "alertsource.deactivated", "AlertSource", id.ToString(),
            new { source.Name }, ct);
    }

    public async Task<AlertSourceCreatedDto> RegenerateKeyAsync(Guid id, CancellationToken ct = default)
    {
        var source = await db.AlertSources.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Alert source");
        var (key, hash, hint) = NewKey();
        source.KeyHash = hash;
        source.KeyHint = hint;
        await db.SaveChangesAsync(ct);
        // The old key stops working the moment this is saved; whoever is holding it must be told.
        await audit.WriteAsync("alertsource.key.regenerated", "AlertSource", id.ToString(), new { source.Name, source.KeyHint }, ct);
        return new AlertSourceCreatedDto(await OneAsync(id, ct), key);
    }

    private async Task<AlertSourceDto> OneAsync(Guid id, CancellationToken ct)
        => (await ListAsync(ct)).First(s => s.Id == id);

    /// <summary>
    /// A 256-bit key, URL-safe so it can sit in a header a vendor's webhook form accepts. Only the
    /// hash is stored: a copy of the database gives nobody the ability to raise tickets here.
    /// </summary>
    private static (string Key, string Hash, string Hint) NewKey()
    {
        var key = "dsk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (key, AlertIntakeService.HashKey(key), key[..12]);
    }

    private static string Validate(AlertSourceInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 120) throw new ValidationFailedException("Give the source a name of up to 120 characters.");
        return name;
    }
}
