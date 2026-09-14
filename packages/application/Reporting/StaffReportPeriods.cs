using System.Globalization;
using Desk.Domain.Reporting;

namespace Desk.Application.Reporting;

/// <summary>
/// The calendar arithmetic behind scheduled staff reports, kept pure so it can be tested against
/// month ends, leap years and time zones without a database.
///
/// Periods are CALENDAR periods in the organization's time zone: "yesterday" for a desk in Mohali
/// is Mohali's yesterday. Weeks run Monday to Sunday. A run is due at <see cref="SendHour"/> local
/// time on the first day after the period closes, which is when its figures stop changing.
/// </summary>
public static class StaffReportPeriods
{
    public const int SendHour = 7;

    /// <summary>The last complete period before <paramref name="now"/>.</summary>
    public static (DateOnly Start, DateOnly End) LastComplete(StaffReportFrequency frequency, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        switch (frequency)
        {
            case StaffReportFrequency.Daily:
                return (today.AddDays(-1), today.AddDays(-1));
            case StaffReportFrequency.Weekly:
            {
                var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                return (thisMonday.AddDays(-7), thisMonday.AddDays(-1));
            }
            case StaffReportFrequency.Monthly:
            {
                var first = new DateOnly(today.Year, today.Month, 1);
                return (first.AddMonths(-1), first.AddDays(-1));
            }
            default:
            {
                var first = new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
                return (first.AddMonths(-3), first.AddDays(-1));
            }
        }
    }

    /// <summary>The next send time strictly after <paramref name="now"/>, as a UTC instant.</summary>
    public static DateTimeOffset NextRun(StaffReportFrequency frequency, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);

        DateOnly candidate = frequency switch
        {
            StaffReportFrequency.Daily => today,
            StaffReportFrequency.Weekly => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            StaffReportFrequency.Monthly => new DateOnly(today.Year, today.Month, 1),
            _ => new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1),
        };

        while (true)
        {
            var at = AtSendHour(candidate, zone);
            if (at > now) return at;
            candidate = frequency switch
            {
                StaffReportFrequency.Daily => candidate.AddDays(1),
                StaffReportFrequency.Weekly => candidate.AddDays(7),
                StaffReportFrequency.Monthly => candidate.AddMonths(1),
                _ => candidate.AddMonths(3),
            };
        }
    }

    /// <summary>The period of the same length immediately before [start, end].</summary>
    public static (DateOnly Start, DateOnly End) Previous(StaffReportFrequency frequency, DateOnly start) => frequency switch
    {
        StaffReportFrequency.Daily => (start.AddDays(-1), start.AddDays(-1)),
        StaffReportFrequency.Weekly => (start.AddDays(-7), start.AddDays(-1)),
        StaffReportFrequency.Monthly => (start.AddMonths(-1), start.AddDays(-1)),
        _ => (start.AddMonths(-3), start.AddDays(-1)),
    };

    /// <summary>The frequency whose period is [start, end], so an ad-hoc range can still be labelled and compared.</summary>
    public static StaffReportFrequency Infer(DateOnly start, DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        return days <= 1 ? StaffReportFrequency.Daily
            : days <= 7 ? StaffReportFrequency.Weekly
            : start.Day == 1 && end == start.AddMonths(1).AddDays(-1) ? StaffReportFrequency.Monthly
            : StaffReportFrequency.Quarterly;
    }

    /// <summary>UTC bounds of a local-date period: from local midnight of the first day to the end of the last.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) UtcBounds(DateOnly start, DateOnly end, TimeZoneInfo zone)
        => (LocalMidnight(start, zone), LocalMidnight(end.AddDays(1), zone).AddTicks(-1));

    public static string Describe(StaffReportFrequency frequency, DateOnly start, DateOnly end) => frequency switch
    {
        StaffReportFrequency.Daily => start.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture),
        StaffReportFrequency.Monthly => start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        StaffReportFrequency.Quarterly => $"Q{(start.Month - 1) / 3 + 1} {start.Year}",
        _ => string.Create(CultureInfo.InvariantCulture, $"{start:d MMM} – {end:d MMM yyyy}"),
    };

    /// <summary>True when the id names a real zone on this host (so it will not silently fall back to UTC).</summary>
    public static bool IsKnown(string? id)
        => !string.IsNullOrWhiteSpace(id) && (TryFind(id, out _)
            || (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var w) && TryFind(w, out _))
            || (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var i) && TryFind(i, out _)));

    /// <summary>Resolves an IANA or Windows zone id, falling back to UTC for anything unknown.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        if (TryFind(id, out var zone)) return zone;
        // Linux containers know IANA ids ("Asia/Kolkata"), Windows hosts may only know Windows ids
        // ("India Standard Time"); an organization saved on one must still resolve on the other.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TryFind(windowsId, out zone)) return zone;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && TryFind(ianaId, out zone)) return zone;
        return TimeZoneInfo.Utc;
    }

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; return false; }
    }

    private static DateTimeOffset AtSendHour(DateOnly day, TimeZoneInfo zone)
        => LocalToUtc(day.ToDateTime(new TimeOnly(SendHour, 0)), zone);

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
        => LocalToUtc(day.ToDateTime(TimeOnly.MinValue), zone);

    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A local time skipped by a DST change does not exist; move past the gap rather than throw.
        while (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }
}
