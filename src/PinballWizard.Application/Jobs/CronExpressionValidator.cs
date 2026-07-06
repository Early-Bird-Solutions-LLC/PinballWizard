namespace PinballWizard.Application.Jobs;

// Validates standard 5-field cron expressions before they reach ARM.
// Catches obvious user errors (wrong field count, out-of-range values)
// with clear messages rather than letting ARM reject with an opaque 400.
public static class CronExpressionValidator
{
    public static void Validate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Cron expression cannot be empty.", nameof(expression));

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new ArgumentException(
                $"Cron expression must have exactly 5 fields (minute hour day-of-month month day-of-week), got {parts.Length}.",
                nameof(expression));

        ValidateField(parts[0], "minute", 0, 59);
        ValidateField(parts[1], "hour", 0, 23);
        ValidateField(parts[2], "day-of-month", 1, 31);
        ValidateField(parts[3], "month", 1, 12);
        ValidateField(parts[4], "day-of-week", 0, 7); // 0 and 7 both mean Sunday
    }

    public static bool TryValidate(string? expression, out string? errorMessage)
    {
        try
        {
            Validate(expression);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void ValidateField(string field, string fieldName, int min, int max)
    {
        // Wildcard
        if (field == "*") return;

        // Step: */N
        if (field.StartsWith("*/"))
        {
            if (!int.TryParse(field[2..], out var step) || step < 1)
                throw new ArgumentException(
                    $"Invalid step value in {fieldName} field: '{field}'. Expected */N where N >= 1.",
                    nameof(field));
            return;
        }

        // Range: N-M
        if (field.Contains('-'))
        {
            var rangeParts = field.Split('-');
            if (rangeParts.Length != 2
                || !int.TryParse(rangeParts[0], out var rangeStart)
                || !int.TryParse(rangeParts[1], out var rangeEnd))
                throw new ArgumentException(
                    $"Invalid range in {fieldName} field: '{field}'. Expected N-M.",
                    nameof(field));
            ValidateRange(rangeStart, fieldName, min, max);
            ValidateRange(rangeEnd, fieldName, min, max);
            return;
        }

        // List: N,M,...
        if (field.Contains(','))
        {
            foreach (var item in field.Split(','))
            {
                if (!int.TryParse(item, out var listVal))
                    throw new ArgumentException(
                        $"Invalid value in {fieldName} field list: '{item}'. Expected an integer.",
                        nameof(field));
                ValidateRange(listVal, fieldName, min, max);
            }
            return;
        }

        // Single value
        if (!int.TryParse(field, out var value))
            throw new ArgumentException(
                $"Invalid {fieldName} field: '{field}'. Expected *, */N, N-M, N,M, or an integer.",
                nameof(field));
        ValidateRange(value, fieldName, min, max);
    }

    private static void ValidateRange(int value, string fieldName, int min, int max)
    {
        if (value < min || value > max)
            throw new ArgumentException(
                $"{fieldName} value {value} is out of range ({min}–{max}).",
                nameof(value));
    }
}
