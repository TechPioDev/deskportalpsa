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

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; return false; }
    }
}
