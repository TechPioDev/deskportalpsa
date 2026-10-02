using System.Globalization;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Common;
using Desk.Domain.Identity;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Capacity exceptions: time someone is unavailable for planned work, or extra time they are
/// available. Seeing them follows schedule.view; recording them needs availability.manage reaching
/// that person. Every change is audited. Capacity planning only - no leave balances, approvals or
/// payroll hang off any of it.
/// </summary>
public sealed class CapacityExceptionService(DeskDbContext db, WorkforceAccess access, IAuditWriter audit, TimeProvider clock)
    : ICapacityExceptionService
{
    public const int NoteMax = 200;
    /// <summary>How far back an exception may be recorded (to correct a recent day).</summary>
    public const int MaxDaysBack = 31;
    /// <summary>How far ahead an exception may start, and the longest an all-day one may run.</summary>
    public const int MaxDaysAhead = 366;
    private const int MaxListDays = 800;

    public async Task<IReadOnlyList<CapacityExceptionDto>> ListAsync(Guid callerId, Guid appUserId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var person = await access.VisiblePersonAsync(callerId, appUserId, ct);
        var (_, zone) = await ZoneAsync(person.Id, null, ct);
        var today = WorkforceCalendar.LocalDate(clock.GetUtcNow(), zone);
        var start = from ?? today;
        var end = to ?? start.AddDays(MaxDaysAhead);
        if (end < start) throw new ValidationFailedException("The last date is before the first.");
        if (end.DayNumber - start.DayNumber > MaxListDays) throw new ValidationFailedException("Ask for at most two years at a time.");

        var rows = await db.CapacityExceptions.AsNoTracking()
            .Where(e => e.AppUserId == person.Id && e.FromDate <= end && e.ToDate >= start)
            .OrderBy(e => e.FromDate).ThenBy(e => e.StartsAt).ThenBy(e => e.Id).ToListAsync(ct);
        var names = await NamesAsync(rows, ct);
        return rows.Select(e => Dto(e, TimeZones.Resolve(e.TimeZone), names)).ToList();
    }

    public async Task<CapacityExceptionDto> AddAsync(Guid callerId, Guid appUserId, CapacityExceptionInput input, CancellationToken ct = default)
    {
        var person = await access.AvailabilityManagedPersonAsync(callerId, appUserId, ct);
        var (zoneId, zone) = await ZoneAsync(person.Id, input.FromDate, ct);
        var v = Validate(input, zone);
        await RefuseDuplicateAsync(person.Id, null, input.Kind, v, ct);

        var row = new CapacityException
        {
            MspOrganizationId = person.MspOrganizationId ?? Guid.Empty, AppUserId = person.Id, TimeZone = zoneId,
            CreatedByUserId = callerId, UpdatedByUserId = callerId,
        };
        Apply(row, input, v);
        db.CapacityExceptions.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.exception.added", "AppUser", person.Id.ToString(),
            new { person = person.DisplayName, exceptionId = row.Id, exception = Describe(row, zone), note = row.Note }, ct);
        return Dto(row, zone, await NamesAsync([row], ct));
    }

    public async Task<CapacityExceptionDto> UpdateAsync(Guid callerId, Guid appUserId, Guid exceptionId, CapacityExceptionInput input, CancellationToken ct = default)
    {
        var person = await access.AvailabilityManagedPersonAsync(callerId, appUserId, ct);
        // Looked up for THIS person: an id from someone else's exception is "not found" here.
        var row = await db.CapacityExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId && e.AppUserId == person.Id, ct)
                  ?? throw new NotFoundException("Exception");
        var (zoneId, zone) = await ZoneAsync(person.Id, input.FromDate, ct);
        var v = Validate(input, zone);
        await RefuseDuplicateAsync(person.Id, row.Id, input.Kind, v, ct);

        var before = Describe(row, TimeZones.Resolve(row.TimeZone));
        row.TimeZone = zoneId;
        row.UpdatedByUserId = callerId;
        Apply(row, input, v);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.exception.updated", "AppUser", person.Id.ToString(),
            new { person = person.DisplayName, exceptionId = row.Id, before, after = Describe(row, zone), note = row.Note }, ct);
        return Dto(row, zone, await NamesAsync([row], ct));
    }

    public async Task RemoveAsync(Guid callerId, Guid appUserId, Guid exceptionId, CancellationToken ct = default)
    {
        var person = await access.AvailabilityManagedPersonAsync(callerId, appUserId, ct);
        var row = await db.CapacityExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId && e.AppUserId == person.Id, ct)
                  ?? throw new NotFoundException("Exception");
        db.CapacityExceptions.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.exception.removed", "AppUser", person.Id.ToString(),
            new { person = person.DisplayName, exceptionId = row.Id, removed = Describe(row, TimeZones.Resolve(row.TimeZone)) }, ct);
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private sealed record Checked(bool AllDay, DateOnly From, DateOnly To, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Note);

    /// <summary>Checks a submitted exception completely and reports every problem at once.</summary>
    private Checked Validate(CapacityExceptionInput input, TimeZoneInfo zone)
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(input.Kind)) problems.Add("Choose whether this is time unavailable or extra availability.");
        if (!Enum.IsDefined(input.Reason)) problems.Add("Choose a reason from the list.");

        var today = WorkforceCalendar.LocalDate(clock.GetUtcNow(), zone);
        if (input.FromDate < today.AddDays(-MaxDaysBack))
            problems.Add($"The date can be at most {MaxDaysBack} days in the past.");
        if (input.FromDate > today.AddDays(MaxDaysAhead))
            problems.Add("The date can be at most a year ahead.");

        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note is { Length: > NoteMax }) problems.Add($"Keep the note to {NoteMax} characters.");
        if (note is not null && note.Any(char.IsControl)) problems.Add("The note contains characters that can't be shown.");

        var to = input.FromDate;
        DateTimeOffset? startsAt = null, endsAt = null;
        if (input.AllDay)
        {
            if (input.Kind == CapacityExceptionKind.AdditionalAvailability)
                problems.Add("Extra availability needs a start and an end time.");
            to = input.ToDate ?? input.FromDate;
            if (to < input.FromDate) problems.Add("The last day is before the first.");
            else if (to.DayNumber - input.FromDate.DayNumber >= MaxDaysAhead) problems.Add("One period can cover at most a year.");
        }
        else
        {
            var start = ParseTime(input.StartTime, "The start time", problems);
            var end = ParseTime(input.EndTime, "The end time", problems);
            if (start is not null && end is not null)
            {
                if (start == end) problems.Add("The start and end times are the same. For a whole day, choose \"All day\".");
                else
                {
                    // An end at or before the start runs into the next day, as a night shift does.
                    var endDate = end > start ? input.FromDate : input.FromDate.AddDays(1);
                    var startWall = input.FromDate.ToDateTime(start.Value);
                    var endWall = endDate.ToDateTime(end.Value);
                    startsAt = TimeZones.WallToUtc(startWall, zone, earlierIfAmbiguous: true);
                    endsAt = TimeZones.WallToUtc(endWall, zone, earlierIfAmbiguous: false);
                    // Both times inside the hour a clock change repeats (01:15-01:45 on a fall-back
                    // night): they mean thirty minutes, not the ninety between the first 01:15 and the
                    // second 01:45.
                    if (zone.IsAmbiguousTime(startWall) && zone.IsAmbiguousTime(endWall) && endWall > startWall && endWall - startWall < TimeSpan.FromHours(2))
                        endsAt = startsAt.Value.Add(endWall - startWall);
                    to = WorkforceCalendar.LocalDate(endsAt.Value, zone);
                    // Both times inside the hour a clock change skips: no real time lies between them.
                    if (endsAt <= startsAt) problems.Add("Those times do not exist on that date: the clocks go forward then.");
                }
            }
        }
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems));
        return new Checked(input.AllDay, input.FromDate, to, startsAt, endsAt, note);
    }

    private static void Apply(CapacityException row, CapacityExceptionInput input, Checked v)
    {
        row.Kind = input.Kind;
        row.Reason = input.Reason;
        row.AllDay = v.AllDay;
        row.FromDate = v.From;
        row.ToDate = v.To;
        row.StartsAt = v.StartsAt;
        row.EndsAt = v.EndsAt;
        row.Note = v.Note;
    }

    /// <summary>The same exception entered twice (a double click, a second tab) is refused rather than stored twice.</summary>
    private async Task RefuseDuplicateAsync(Guid appUserId, Guid? except, CapacityExceptionKind kind, Checked v, CancellationToken ct)
    {
        var same = await db.CapacityExceptions.AsNoTracking().AnyAsync(e =>
            e.AppUserId == appUserId && e.Id != except && e.Kind == kind && e.AllDay == v.AllDay
            && e.FromDate == v.From && e.ToDate == v.To && e.StartsAt == v.StartsAt && e.EndsAt == v.EndsAt, ct);
        if (same) throw new ValidationFailedException("That is already recorded for this person.");
    }

    /// <summary>The zone a person's dates and times are entered in on a date: their schedule's, else the organization's.</summary>
    private async Task<(string Id, TimeZoneInfo Zone)> ZoneAsync(Guid appUserId, DateOnly? on, CancellationToken ct)
    {
        var date = on ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var zoneId = await db.WorkSchedules.AsNoTracking()
            .Where(s => s.AppUserId == appUserId && s.EffectiveFrom <= date)
            .OrderByDescending(s => s.EffectiveFrom).Select(s => s.TimeZone).FirstOrDefaultAsync(ct)
            ?? access.OrganizationTimeZone();
        return (zoneId, TimeZones.Resolve(zoneId));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<CapacityException> rows, CancellationToken ct)
    {
        var ids = rows.Select(e => e.UpdatedByUserId ?? e.CreatedByUserId).Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>()
            : await db.AppUsers.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    internal static CapacityExceptionDto Dto(CapacityException e, TimeZoneInfo zone, IReadOnlyDictionary<Guid, string> names)
    {
        string? Wall(DateTimeOffset? at) => at is null ? null
            : TimeZoneInfo.ConvertTime(at.Value, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var by = e.UpdatedByUserId ?? e.CreatedByUserId;
        return new CapacityExceptionDto(e.Id, e.AppUserId, e.Kind, e.AllDay, e.FromDate, e.ToDate,
            e.AllDay ? null : Wall(e.StartsAt), e.AllDay ? null : Wall(e.EndsAt), e.StartsAt, e.EndsAt, e.TimeZone,
            e.Reason, e.Note, by is { } id ? names.GetValueOrDefault(id) : null, e.UpdatedAt);
    }

    /// <summary>"Unavailable · 12 Oct 2026, all day · Time off" - for the audit trail.</summary>
    public static string Describe(CapacityException e, TimeZoneInfo zone)
    {
        static string Day(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var kind = e.Kind == CapacityExceptionKind.Unavailable ? "Unavailable" : "Extra availability";
        string when;
        if (e.AllDay) when = e.ToDate == e.FromDate ? $"{Day(e.FromDate)}, all day" : $"{Day(e.FromDate)} to {Day(e.ToDate)}, all day";
        else
        {
            var start = TimeZoneInfo.ConvertTime(e.StartsAt!.Value, zone);
            var end = TimeZoneInfo.ConvertTime(e.EndsAt!.Value, zone);
            when = $"{Day(e.FromDate)} {start:HH\\:mm}–{end:HH\\:mm} ({e.TimeZone})";
        }
        return $"{kind} · {when} · {ReasonLabel(e.Reason)}";
    }

    public static string ReasonLabel(CapacityExceptionReason reason) => reason switch
    {
        CapacityExceptionReason.TimeOff => "Time off",
        CapacityExceptionReason.InternalEvent => "Internal event",
        _ => reason.ToString(),
    };

    private static TimeOnly? ParseTime(string? value, string what, List<string> problems)
    {
        if (TimeOnly.TryParseExact(value?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        problems.Add(string.IsNullOrWhiteSpace(value) ? $"{what} is missing." : $"{what} \"{value}\" is not a time like 15:00.");
        return null;
    }
}
