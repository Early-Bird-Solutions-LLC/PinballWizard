using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// Unit tests for the shared admin time formatter extracted from AdminJobDetail /
// AdminJobExecutionDetail (previously byte-identical private copies). These
// exercise the formatting logic directly — before extraction it was only
// covered indirectly through each page's bUnit tests.
public sealed class AdminTimeFormatTests
{
    // ── Duration (culture-independent output) ─────────────────────────────

    [Fact]
    public void Duration_NullBound_ReturnsDash()
    {
        Assert.Equal("—", AdminTimeFormat.Duration(null, DateTimeOffset.UnixEpoch));
        Assert.Equal("—", AdminTimeFormat.Duration(DateTimeOffset.UnixEpoch, null));
    }

    [Fact]
    public void Duration_UnderOneMinute_ShowsSeconds()
    {
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal("45s", AdminTimeFormat.Duration(start, start.AddSeconds(45)));
    }

    [Fact]
    public void Duration_UnderOneHour_ShowsMinutesSeconds()
    {
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal("3m 20s", AdminTimeFormat.Duration(start, start.AddMinutes(3).AddSeconds(20)));
    }

    [Fact]
    public void Duration_OverOneHour_ShowsHoursMinutes()
    {
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal("2h 5m", AdminTimeFormat.Duration(start, start.AddHours(2).AddMinutes(5).AddSeconds(59)));
    }

    // ── LocalTime ─────────────────────────────────────────────────────────

    [Fact]
    public void LocalTime_NullInstant_ReturnsDash() =>
        Assert.Equal("—", AdminTimeFormat.LocalTime(TimeZoneInfo.Utc, null));

    [Fact]
    public void LocalTime_NullTimeZone_FallsBackToUtcSuffix()
    {
        var result = AdminTimeFormat.LocalTime(null, DateTimeOffset.UnixEpoch);
        Assert.EndsWith(" UTC", result);
        Assert.Contains("1970", result);
    }

    [Fact]
    public void LocalTime_WithTimeZone_ConvertsAndShowsOffset_NotUtcSuffix()
    {
        // Fixed-offset custom zone → deterministic regardless of the host TZ database.
        var tz = TimeZoneInfo.CreateCustomTimeZone("t-5", TimeSpan.FromHours(-5), "t-5", "t-5");
        var result = AdminTimeFormat.LocalTime(tz, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(" UTC", result);
        Assert.Contains("-05:00", result); // zzz offset component
    }
}
