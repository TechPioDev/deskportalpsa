namespace Desk.Domain.Common;

/// <summary>
/// The organization's time zone, from the id an administrator saved. One implementation, used by the
/// scheduled reports and the SLA clock alike: two resolvers would sooner or later disagree about the
/// same organization's day.
/// </summary>
public static class TimeZones
{
    /// <summary>Resolves an IANA or Windows zone id, falling back to UTC for anything unknown.</summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        id = id.Trim();
        if (TryFind(id, out var zone)) return zone;
        // Linux containers know IANA ids ("Asia/Kolkata"), Windows hosts may only know Windows ids
        // ("India Standard Time"); an organization saved on one must still resolve on the other.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TryFind(windowsId, out zone)) return zone;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && TryFind(ianaId, out zone)) return zone;
        return TimeZoneInfo.Utc;
    }

    /// <summary>True when the id names a real zone on this host (so it will not silently fall back to UTC).</summary>
    public static bool IsKnown(string? id)
        => !string.IsNullOrWhiteSpace(id) && (TryFind(id, out _)
            || (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var w) && TryFind(w, out _))
            || (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var i) && TryFind(i, out _)));

    /// <summary>
    /// The IANA form of a zone id ("Asia/Kolkata" for "India Standard Time"), or null when it names
    /// no real zone. Schedules store IANA ids so they read the same on the Linux servers and in the
    /// browser, whichever form an administrator's machine happened to offer.
    /// </summary>
    public static string? ToIana(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        if (!IsKnown(id))
            // A host that cannot read IANA ids at all (Windows with invariant globalization - the
            // development machines) cannot check one either. A well-formed IANA id is accepted there
            // rather than refused; the Linux servers, which do read them, check every id properly.
            return !HostKnowsIana && LooksLikeIana(id) ? id : null;
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || id.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)) return "UTC";
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return iana;
        // Already IANA, or a zone with no Windows twin: keep it as given.
        return id;
    }

    /// <summary>
    /// A wall-clock time in <paramref name="zone"/> as the real instant it names - the one place
    /// clock changes are decided for schedules.
    ///
    /// A time the clock skips (02:30 on a spring-forward night) is read with the offset in force just
    /// before the gap, which lands it the gap's length later (03:30). A time the clock passes twice
    /// (01:30 on a fall-back night) is the FIRST occurrence when <paramref name="earlierIfAmbiguous"/>
    /// (use it for starts) and the second otherwise (use it for ends), so a window spanning the
    /// change keeps its true elapsed length.
    /// </summary>
    public static DateTimeOffset WallToUtc(DateTime wall, TimeZoneInfo zone, bool earlierIfAmbiguous)
    {
        var local = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            return new DateTimeOffset(local, zone.GetUtcOffset(local.AddHours(-3))).ToUniversalTime();
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            // The larger offset is the earlier instant (still on summer time).
            var offset = earlierIfAmbiguous ? offsets.Max() : offsets.Min();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Whether this host resolves IANA ids. True on the Linux servers; false on Windows in invariant mode.</summary>
    public static bool HostKnowsIana { get; } = TryFind("America/New_York", out _);

    private static bool LooksLikeIana(string id)
        => System.Text.RegularExpressions.Regex.IsMatch(id, @"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+){1,2})$");

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; return false; }
    }
}
