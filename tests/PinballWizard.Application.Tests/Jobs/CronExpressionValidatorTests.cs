using PinballWizard.Application.Jobs;
using Xunit;

namespace PinballWizard.Application.Tests.Jobs;

public sealed class CronExpressionValidatorTests
{
    // ── Valid expressions — must NOT throw ────────────────────────────────────

    [Theory]
    [InlineData("* * * * *")]           // all wildcards
    [InlineData("0 4 * * 0")]           // typical weekly cron
    [InlineData("*/15 * * * *")]        // step notation
    [InlineData("0 9-17 * * *")]        // range notation
    [InlineData("0 0,12 * * *")]        // list notation
    [InlineData("30 3 1 1 0")]          // all concrete values at boundaries
    public void Validate_ValidExpression_DoesNotThrow(string expression)
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate(expression));
        Assert.Null(ex);
    }

    // ── Boundary values per field — valid extremes must be accepted ───────────

    [Theory]
    [InlineData("0 0 1 1 0")]   // minute=0, hour=0, dom=1, month=1, dow=0
    [InlineData("59 23 31 12 7")] // minute=59, hour=23, dom=31, month=12, dow=7 (both 0 and 7 = Sunday)
    public void Validate_BoundaryValues_DoesNotThrow(string expression)
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate(expression));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_MinuteMin_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("0 * * * *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_MinuteMax_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("59 * * * *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_HourMin_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* 0 * * *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_HourMax_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* 23 * * *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_DayOfMonthMin_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* * 1 * *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_DayOfMonthMax_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* * 31 * *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_MonthMin_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* * * 1 *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_MonthMax_DoesNotThrow()
    {
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* * * 12 *"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_DayOfWeekZero_DoesNotThrow()
    {
        // 0 = Sunday (valid)
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* * * * 0"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_DayOfWeekSeven_DoesNotThrow()
    {
        // 7 = Sunday (also valid — see "0 and 7 both mean Sunday" in the validator)
        var ex = Record.Exception(() => CronExpressionValidator.Validate("* * * * 7"));
        Assert.Null(ex);
    }

    // ── Out-of-range values per field ─────────────────────────────────────────

    [Fact]
    public void Validate_Minute60_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("60 * * * *"));
        Assert.Contains("minute", ex.Message);
        Assert.Contains("60", ex.Message);
    }

    [Fact]
    public void Validate_Hour24_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("* 24 * * *"));
        Assert.Contains("hour", ex.Message);
        Assert.Contains("24", ex.Message);
    }

    [Fact]
    public void Validate_DayOfMonthZero_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("* * 0 * *"));
        Assert.Contains("day-of-month", ex.Message);
        Assert.Contains("0", ex.Message);
    }

    [Fact]
    public void Validate_DayOfMonth32_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("* * 32 * *"));
        Assert.Contains("day-of-month", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Validate_MonthZero_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("* * * 0 *"));
        Assert.Contains("month", ex.Message);
        Assert.Contains("0", ex.Message);
    }

    [Fact]
    public void Validate_Month13_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("* * * 13 *"));
        Assert.Contains("month", ex.Message);
        Assert.Contains("13", ex.Message);
    }

    [Fact]
    public void Validate_DayOfWeek8_ThrowsArgumentException_MentioningFieldAndValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("* * * * 8"));
        Assert.Contains("day-of-week", ex.Message);
        Assert.Contains("8", ex.Message);
    }

    // ── Wrong field count ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_FourFields_ThrowsArgumentException_Mentioning5FieldsAnd4()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("0 4 * *"));
        Assert.Contains("5 fields", ex.Message);
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public void Validate_SixFields_ThrowsArgumentException_Mentioning5FieldsAnd6()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("0 4 * * * 2026"));
        Assert.Contains("5 fields", ex.Message);
        Assert.Contains("6", ex.Message);
    }

    // ── Null / empty / whitespace ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrEmpty_ThrowsArgumentException_MentioningEmpty(string? expression)
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate(expression));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Malformed step ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_StepZero_ThrowsArgumentException()
    {
        // */0 is invalid — step must be >= 1
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("*/0 * * * *"));
        Assert.Contains("*/0", ex.Message);
    }

    [Fact]
    public void Validate_StepNonNumeric_ThrowsArgumentException()
    {
        // */abc — non-numeric step value
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("*/abc * * * *"));
        Assert.Contains("*/abc", ex.Message);
    }

    // ── Malformed range ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_RangeWithMissingEnd_ThrowsArgumentException()
    {
        // "5-" — malformed range (no end value)
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("5- * * * *"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void Validate_RangeWithNonNumericValues_ThrowsArgumentException()
    {
        // "a-b" — non-numeric range bounds
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("a-b * * * *"));
        Assert.NotNull(ex);
    }

    // ── Malformed list ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ListWithNonNumericItem_ThrowsArgumentException()
    {
        // "1,x,3" — 'x' is not a valid integer in a list
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("1,x,3 * * * *"));
        Assert.Contains("x", ex.Message);
    }

    // ── Out-of-range within list and range ────────────────────────────────────

    [Fact]
    public void Validate_RangeWithOutOfRangeHour_ThrowsArgumentException_MentioningHour()
    {
        // hours 25-26 — out of range (max is 23)
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("0 25-26 * * *"));
        Assert.Contains("hour", ex.Message);
    }

    [Fact]
    public void Validate_ListWithOutOfRangeHour_ThrowsArgumentException_MentioningHour()
    {
        // hour 99 is out of range (max is 23)
        var ex = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate("0 0,99 * * *"));
        Assert.Contains("hour", ex.Message);
    }

    // ── TryValidate mirrors Validate ─────────────────────────────────────────

    [Fact]
    public void TryValidate_ValidExpression_ReturnsTrueWithNullErrorMessage()
    {
        var result = CronExpressionValidator.TryValidate("0 4 * * 0", out var errorMessage);
        Assert.True(result);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void TryValidate_InvalidExpression_ReturnsFalseWithNonNullErrorMessage()
    {
        var result = CronExpressionValidator.TryValidate("60 * * * *", out var errorMessage);
        Assert.False(result);
        Assert.NotNull(errorMessage);
        Assert.NotEmpty(errorMessage);
    }

    [Fact]
    public void TryValidate_InvalidExpression_ErrorMessageMatchesValidateException()
    {
        // The error message returned by TryValidate must match what Validate would throw.
        const string invalid = "* * * 0 *";
        var thrown = Assert.Throws<ArgumentException>(() => CronExpressionValidator.Validate(invalid));
        CronExpressionValidator.TryValidate(invalid, out var errorMessage);
        Assert.Equal(thrown.Message, errorMessage);
    }

    // ── Extra / irregular whitespace ──────────────────────────────────────────

    [Fact]
    public void Validate_IrregularWhitespaceBetweenFields_DoesNotThrow()
    {
        // The implementation does Trim() + Split with RemoveEmptyEntries,
        // so irregular spacing must still parse to exactly 5 fields.
        var ex = Record.Exception(() => CronExpressionValidator.Validate("  0   4  *  *  0  "));
        Assert.Null(ex);
    }
}
