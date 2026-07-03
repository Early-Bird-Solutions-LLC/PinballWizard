using System.Globalization;

namespace PinballWizard.Web.Components.Pages.Admin;

// Shared time formatting for admin pages that surface execution timestamps.
// Pure functions extracted from AdminJobDetail and AdminJobExecutionDetail,
// which previously carried byte-identical private copies — a drift risk on any
// future format change. Behaviour is preserved: same format strings, same
// "—" / " UTC" fallbacks. CurrentCulture is passed explicitly (CA1305) — these
// are user-facing local timestamps, so the viewer's locale is the right formatter
// and this makes the previously-implicit ambient culture explicit.
public static class AdminTimeFormat
{
    // A UTC instant rendered in the viewer's resolved timezone. Falls back to a
    // UTC-suffixed string when the timezone has not resolved yet (JS interop
    // pending or unavailable). Returns "—" when the instant is null.
    public static string LocalTime(TimeZoneInfo? timeZone, DateTimeOffset? utc)
    {
        if (utc is null) return "—";
        if (timeZone is null)
            return utc.Value.UtcDateTime.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture) + " UTC";
        var local = TimeZoneInfo.ConvertTime(utc.Value, timeZone);
        return local.ToString("MMM d, yyyy h:mm tt zzz", CultureInfo.CurrentCulture);
    }

    // Human-readable elapsed time between two instants: "2h 5m" / "3m 20s" /
    // "45s". Returns "—" when either bound is null.
    public static string Duration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null || end is null) return "—";
        var d = end.Value - start.Value;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }
}
