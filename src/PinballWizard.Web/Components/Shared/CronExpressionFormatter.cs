namespace PinballWizard.Web.Components.Shared;

internal static class CronExpressionFormatter
{
    private static readonly string[] DayNames =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public static string Format(string? expression)
    {
        if (expression is null) return string.Empty;

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return expression;

        var (min, hr, dom, month, dow) = (parts[0], parts[1], parts[2], parts[3], parts[4]);

        // Every minute
        if (min == "*" && hr == "*" && dom == "*" && month == "*" && dow == "*")
            return "Every minute";

        // Every N minutes: */N * * * *
        if (min.StartsWith("*/") && hr == "*" && dom == "*" && month == "*" && dow == "*"
            && int.TryParse(min[2..], out var intervalMins))
            return $"Every {intervalMins} minutes";

        // Every N hours: 0 */N * * *
        if (min == "0" && hr.StartsWith("*/") && dom == "*" && month == "*" && dow == "*"
            && int.TryParse(hr[2..], out var intervalHrs))
            return $"Every {intervalHrs} hours";

        // Fixed time patterns require parseable hour + minute
        if (!int.TryParse(min, out var mins) || !int.TryParse(hr, out var hrs))
            return expression;

        var timeStr = FormatTime(hrs, mins);

        // Monthly: M H D * *
        if (dom != "*" && month == "*" && dow == "*" && int.TryParse(dom, out var day))
            return $"Monthly on day {day} at {timeStr}";

        // Daily: M H * * *
        if (dom == "*" && month == "*" && dow == "*")
            return $"Daily at {timeStr}";

        // Weekly: M H * * D (single numeric day)
        if (dom == "*" && month == "*" && int.TryParse(dow, out var dayOfWeek))
            return $"{DayNames[dayOfWeek % 7]}s at {timeStr}";

        return expression;
    }

    private static string FormatTime(int hour, int minute)
    {
        var suffix = hour >= 12 ? "PM" : "AM";
        var displayHour = hour switch { 0 => 12, > 12 => hour - 12, _ => hour };
        return $"{displayHour}:{minute:D2} {suffix} UTC";
    }
}
