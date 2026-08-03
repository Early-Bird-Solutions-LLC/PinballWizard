using PinballWizard.Web.Components.Theming;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Theming;

// Pins the Modern LCD spec palette + typography to the values in
// docs/ui/themes/modern-lcd.md § Visual system. The audit at
// docs/PHASE5-DRIFT-AUDIT.md § 1 lists nine 🔴 token findings that
// PR-T1 closes; these tests mechanically prevent the palette from
// drifting back to Material defaults the next time the theme is
// touched. Any spec change must update both modern-lcd.md AND this
// test together.
public sealed class PinballThemeContractTests
{
    [Theory]
    [InlineData("Primary", "#ff9a1f")]            // accent-primary — arcade amber
    [InlineData("Background", "#0c0b0e")]         // bg-base — warm near-black
    [InlineData("Surface", "#161519")]            // bg-surface — panel interiors
    [InlineData("AppbarBackground", "#08070a")]   // recessed header
    [InlineData("DrawerBackground", "#101015")]
    [InlineData("TextPrimary", "#f4f1ea")]        // warm off-white
    [InlineData("TextSecondary", "#9a9590")]
    [InlineData("Success", "#34d96a")]            // accent-grounded — atomic green
    [InlineData("Error", "#ff3b30")]              // accent-refusal — saturated red
    [InlineData("Divider", "#2a282d")]            // border-quiet
    public void PaletteLight_Pins_ModernLcdSpecValue(string slot, string expectedHex)
    {
        var theme = PinballTheme.Create();
        var actual = slot switch
        {
            "Primary" => theme.PaletteLight.Primary.Value,
            "Background" => theme.PaletteLight.Background.Value,
            "Surface" => theme.PaletteLight.Surface.Value,
            "AppbarBackground" => theme.PaletteLight.AppbarBackground.Value,
            "DrawerBackground" => theme.PaletteLight.DrawerBackground.Value,
            "TextPrimary" => theme.PaletteLight.TextPrimary.Value,
            "TextSecondary" => theme.PaletteLight.TextSecondary.Value,
            "Success" => theme.PaletteLight.Success.Value,
            "Error" => theme.PaletteLight.Error.Value,
            "Divider" => theme.PaletteLight.Divider.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

        // MudColor normalizes to #RRGGBBAA (9 chars, ff alpha when opaque).
        // The Modern LCD spec is RGB-only (#RRGGBB); compare on RGB.
        Assert.Equal(expectedHex, actual[..7], ignoreCase: true);
    }

    [Theory]
    [InlineData("H1", "700")]   // Spec § Typography: 700 primary for H1–H4 (announcement scales).
    [InlineData("H2", "700")]
    [InlineData("H3", "700")]
    [InlineData("H4", "700")]
    [InlineData("H5", "500")]   // 500 secondary for H5–H6 (smaller headers).
    [InlineData("H6", "500")]
    public void Typography_Display_UsesBarlowCondensed(string scale, string expectedWeight)
    {
        var theme = PinballTheme.Create();
        var family = FontFamilyFor(theme, scale);
        var weight = FontWeightFor(theme, scale);

        Assert.NotNull(family);
        Assert.Contains("Barlow Condensed", family);
        Assert.Contains("Roboto", family);
        Assert.Equal(expectedWeight, weight);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Body1")]
    [InlineData("Body2")]
    public void Typography_Body_UsesInter(string scale)
    {
        var theme = PinballTheme.Create();
        var family = FontFamilyFor(theme, scale);

        Assert.NotNull(family);
        Assert.Contains("Inter", family);
        Assert.Contains("Roboto", family);
    }

    private static string[]? FontFamilyFor(MudBlazor.MudTheme theme, string scale) => scale switch
    {
        "Default" => theme.Typography.Default.FontFamily,
        "H1" => theme.Typography.H1.FontFamily,
        "H2" => theme.Typography.H2.FontFamily,
        "H3" => theme.Typography.H3.FontFamily,
        "H4" => theme.Typography.H4.FontFamily,
        "H5" => theme.Typography.H5.FontFamily,
        "H6" => theme.Typography.H6.FontFamily,
        "Body1" => theme.Typography.Body1.FontFamily,
        "Body2" => theme.Typography.Body2.FontFamily,
        _ => throw new ArgumentOutOfRangeException(nameof(scale)),
    };

    private static string? FontWeightFor(MudBlazor.MudTheme theme, string scale) => scale switch
    {
        "H1" => theme.Typography.H1.FontWeight,
        "H2" => theme.Typography.H2.FontWeight,
        "H3" => theme.Typography.H3.FontWeight,
        "H4" => theme.Typography.H4.FontWeight,
        "H5" => theme.Typography.H5.FontWeight,
        "H6" => theme.Typography.H6.FontWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(scale)),
    };
}

/// <summary>
/// Pins the Daytime Route sibling-theme token values defined in app.css
/// (html.theme-daytime-route block). CSS-variable-only for v1 — no MudTheme
/// companion. Spec authority: docs/ui/themes/sibling-themes.md § Daytime Route.
///
/// Any palette change must update both app.css AND these constants together.
/// The five tokens chosen are the most visually distinctive (inverted light
/// direction vs Modern LCD) and are the ones most likely to regress via
/// copy-paste from the dark theme.
/// </summary>
public static class DaytimeRouteTheme
{
    // CSS class name applied by ThemeService to the <html> element.
    public const string CssClass = "theme-daytime-route";

    // Light/outdoor palette tokens (hex, no alpha suffix).
    public const string BgBase        = "#faf6ef";   // warm cream — base background
    public const string TextPrimary   = "#1f1a14";   // near-black — readable on cream
    public const string AccentPrimary = "#cc5500";   // burnt orange — most visually distinctive
    public const string AccentGrounded = "#0d6e2d";  // deep green — citations / grounded signal
    public const string BorderQuiet   = "#d8cdb5";   // warm sand — inverted vs Modern LCD #2a282d
}

public sealed class DaytimeRouteThemeContractTests
{
    [Fact]
    public void CssClass_IsDaytimeRouteSelector()
    {
        Assert.Equal("theme-daytime-route", DaytimeRouteTheme.CssClass);
    }

    [Theory]
    [InlineData(DaytimeRouteTheme.BgBase,         "#faf6ef")]   // warm cream base
    [InlineData(DaytimeRouteTheme.TextPrimary,    "#1f1a14")]   // near-black on cream
    [InlineData(DaytimeRouteTheme.AccentPrimary,  "#cc5500")]   // burnt orange
    [InlineData(DaytimeRouteTheme.AccentGrounded, "#0d6e2d")]   // deep green
    [InlineData(DaytimeRouteTheme.BorderQuiet,    "#d8cdb5")]   // warm sand
    public void Token_MatchesSpec(string actual, string expected)
    {
        Assert.Equal(expected, actual, ignoreCase: true);
    }
}

// Pins the ThemeNames and PreferenceKeys constants so any rename is a
// compile-level breaking change visible across Web + Web.Tests simultaneously.
public sealed class UserPreferencesContractTests
{
    [Theory]
    [InlineData(ThemeNames.Backbox,      "backbox")]
    [InlineData(ThemeNames.Cabinet,      "cabinet")]
    [InlineData(ThemeNames.DaytimeRoute, "daytime-route")]
    [InlineData(ThemeNames.DmdClassic,   "dmd-classic")]
    [InlineData(ThemeNames.ModernLcd,    "modern-lcd")]
    [InlineData(ThemeNames.Paper,        "paper")]
    public void ThemeName_MatchesExpected(string actual, string expected)
        => Assert.Equal(expected, actual);

    [Theory]
    [InlineData(PreferenceKeys.Theme,  "pinwiz.theme")]
    [InlineData(PreferenceKeys.Motion, "pinwiz.motion")]
    [InlineData(PreferenceKeys.Sound,  "pinwiz.sound")]
    public void PreferenceKey_MatchesExpected(string actual, string expected)
        => Assert.Equal(expected, actual);
}

/// <summary>
/// Pins the Backbox sibling-theme token values defined in app.css
/// (html.theme-backbox block). CSS-variable-only for v1 — no MudTheme
/// companion. Spec authority: docs/ui/themes/sibling-themes-overview.md § Backbox.
///
/// Any palette change must update both app.css AND these constants together.
/// The five tokens chosen are the most visually distinctive (magenta primary,
/// cyan grounded — inverted from Modern LCD's amber/green family).
/// </summary>
public static class BackboxTheme
{
    public const string CssClass       = "theme-backbox";
    public const string BgBase         = "#0a0e1a";
    public const string TextPrimary    = "#e8edf5";
    public const string AccentPrimary  = "#ff3399";
    public const string AccentGrounded = "#00e5cc";
    public const string BorderQuiet    = "#1f2740";
}

public sealed class BackboxThemeContractTests
{
    [Fact]
    public void CssClass_IsBackboxSelector()
        => Assert.Equal("theme-backbox", BackboxTheme.CssClass);

    [Theory]
    [InlineData(BackboxTheme.BgBase,         "#0a0e1a")]
    [InlineData(BackboxTheme.TextPrimary,    "#e8edf5")]
    [InlineData(BackboxTheme.AccentPrimary,  "#ff3399")]
    [InlineData(BackboxTheme.AccentGrounded, "#00e5cc")]
    [InlineData(BackboxTheme.BorderQuiet,    "#1f2740")]
    public void Token_MatchesSpec(string actual, string expected)
        => Assert.Equal(expected, actual, ignoreCase: true);
}

/// <summary>
/// Pins the Cabinet sibling-theme token values defined in app.css
/// (html.theme-cabinet block). CSS-variable-only for v1 — no MudTheme
/// companion. Spec authority: docs/ui/themes/sibling-themes-overview.md § Cabinet.
///
/// Any palette change must update both app.css AND these constants together.
/// The five tokens chosen are most visually distinctive: warm wood base,
/// aged ivory text, flipper-button red primary, flipper yellow grounded.
/// </summary>
public static class CabinetTheme
{
    public const string CssClass       = "theme-cabinet";
    public const string BgBase         = "#2a1f15";
    public const string TextPrimary    = "#f4eedd";
    public const string AccentPrimary  = "#d23030";
    public const string AccentGrounded = "#f5c83a";
    public const string BorderQuiet    = "#3d2e1f";
}

public sealed class CabinetThemeContractTests
{
    [Fact]
    public void CssClass_IsCabinetSelector()
        => Assert.Equal("theme-cabinet", CabinetTheme.CssClass);

    [Theory]
    [InlineData(CabinetTheme.BgBase,         "#2a1f15")]
    [InlineData(CabinetTheme.TextPrimary,    "#f4eedd")]
    [InlineData(CabinetTheme.AccentPrimary,  "#d23030")]
    [InlineData(CabinetTheme.AccentGrounded, "#f5c83a")]
    [InlineData(CabinetTheme.BorderQuiet,    "#3d2e1f")]
    public void Token_MatchesSpec(string actual, string expected)
        => Assert.Equal(expected, actual, ignoreCase: true);
}

/// <summary>
/// Pins the DMD Classic sibling-theme token values defined in app.css
/// (html.theme-dmd-classic block). CSS-variable-only for v1 — no MudTheme
/// companion. Spec authority: docs/ui/themes/sibling-themes-overview.md § DMD Classic.
///
/// Any palette change must update both app.css AND these constants together.
/// The five tokens chosen are most visually distinctive: pure black base,
/// warm cream text, THE amber accent, hot amber grounded.
/// </summary>
public static class DmdClassicTheme
{
    public const string CssClass       = "theme-dmd-classic";
    public const string BgBase         = "#000000";
    public const string TextPrimary    = "#f4d090";
    public const string AccentPrimary  = "#ff9900";
    public const string AccentGrounded = "#ffaa00";
    public const string BorderQuiet    = "#2a1500";
}

public sealed class DmdClassicThemeContractTests
{
    [Fact]
    public void CssClass_IsDmdClassicSelector()
        => Assert.Equal("theme-dmd-classic", DmdClassicTheme.CssClass);

    [Theory]
    [InlineData(DmdClassicTheme.BgBase,         "#000000")]
    [InlineData(DmdClassicTheme.TextPrimary,    "#f4d090")]
    [InlineData(DmdClassicTheme.AccentPrimary,  "#ff9900")]
    [InlineData(DmdClassicTheme.AccentGrounded, "#ffaa00")]
    [InlineData(DmdClassicTheme.BorderQuiet,    "#2a1500")]
    public void Token_MatchesSpec(string actual, string expected)
        => Assert.Equal(expected, actual, ignoreCase: true);
}

// Pins the Paper sibling-theme token values. Paper has a full MudTheme companion
// (CreatePaper()) unlike the CSS-variable-only sibling themes above.
// Spec authority: docs/ui/themes/sibling-themes-overview.md § Paper.
//
// Any palette change must update both CreatePaper() AND these constants together.
//
// AccentPrimary and Success were deepened for WCAG 1.4.3 in #790. The original spec
// values failed the 4.5:1 body-text minimum on Paper's own backgrounds — copper
// #b8763e measured 3.26:1 on --pw-bg-base, and #1a8a45 measured 3.9:1 as text and
// 4.4:1 as a fill behind white. They had read as compliant only because the
// accessibility gate was scanning an unstyled DOM and could not evaluate colour at
// all; the numbers above are axe's, taken once that was fixed.
//
// Paper is the DEFAULT theme (UserPreferencesService.CurrentTheme), so this affected
// every user who never opened Settings. The external design handoff
// (design_handoff_pinwiz_themes/README.md) is not in this repo and still carries the
// original values — it needs the same correction applied at source.
public static class PaperTheme
{
    public const string CssClass            = "theme-paper";
    public const string BgBase              = "#f4f1ea";
    public const string BgSurface           = "#faf8f2";
    public const string AppbarBackground    = "#1a1410";
    public const string TextPrimary         = "#1a1408";
    public const string TextSecondary       = "#5c5042";
    public const string AccentPrimary       = "#8e5b30";   // was #b8763e — 3.26:1 -> 5.05:1
    public const string AccentGrounded      = "#1f6f54";
    public const string BorderQuiet         = "#d8cdb5";
    public const string Success             = "#16763b";   // was #1a8a45 — 3.9:1 -> 5.05:1
    public const string Error               = "#c0200e";
}

public sealed class PaperThemeContractTests
{
    [Fact]
    public void CssClass_IsPaperSelector()
        => Assert.Equal("theme-paper", PaperTheme.CssClass);

    [Theory]
    [InlineData(PaperTheme.BgBase,           "#f4f1ea")]
    [InlineData(PaperTheme.BgSurface,        "#faf8f2")]
    [InlineData(PaperTheme.AppbarBackground, "#1a1410")]
    [InlineData(PaperTheme.TextPrimary,      "#1a1408")]
    [InlineData(PaperTheme.AccentPrimary,    "#8e5b30")]
    [InlineData(PaperTheme.AccentGrounded,   "#1f6f54")]
    [InlineData(PaperTheme.BorderQuiet,      "#d8cdb5")]
    [InlineData(PaperTheme.Success,          "#16763b")]
    [InlineData(PaperTheme.Error,            "#c0200e")]
    public void Token_MatchesSpec(string actual, string expected)
        => Assert.Equal(expected, actual, ignoreCase: true);

    [Theory]
    [InlineData("AppbarBackground", "#1a1410")]
    [InlineData("Background",       "#f4f1ea")]
    [InlineData("Surface",          "#faf8f2")]
    [InlineData("Primary",          "#8e5b30")]   // deepened for WCAG 1.4.3 (#790)
    [InlineData("Success",          "#16763b")]   // deepened for WCAG 1.4.3 (#790)
    [InlineData("Error",            "#c0200e")]
    [InlineData("TextPrimary",      "#1a1408")]
    [InlineData("TextSecondary",    "#5c5042")]
    [InlineData("Divider",          "#d8cdb5")]
    public void PaletteLight_Pins_PaperSpecValue(string slot, string expectedHex)
    {
        var theme = PinballTheme.CreatePaper();
        var actual = slot switch
        {
            "AppbarBackground" => theme.PaletteLight.AppbarBackground.Value,
            "Background"       => theme.PaletteLight.Background.Value,
            "Surface"          => theme.PaletteLight.Surface.Value,
            "Primary"          => theme.PaletteLight.Primary.Value,
            "Success"          => theme.PaletteLight.Success.Value,
            "Error"            => theme.PaletteLight.Error.Value,
            "TextPrimary"      => theme.PaletteLight.TextPrimary.Value,
            "TextSecondary"    => theme.PaletteLight.TextSecondary.Value,
            "Divider"          => theme.PaletteLight.Divider.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        Assert.Equal(expectedHex, actual[..7], ignoreCase: true);
    }
}

public sealed class PinballThemeShapeAndElevationTests
{
    [Fact]
    public void Create_DefaultBorderRadius_Is2px()
    {
        var theme = PinballTheme.Create();
        Assert.Equal("2px", theme.LayoutProperties.DefaultBorderRadius);
    }

    [Fact]
    public void Create_Elevation1_IsNone()
    {
        var theme = PinballTheme.Create();
        Assert.Equal("none", theme.Shadows.Elevation[1]);
    }

    [Fact]
    public void Create_Elevation8_IsNone()
    {
        var theme = PinballTheme.Create();
        Assert.Equal("none", theme.Shadows.Elevation[8]);
    }

    [Fact]
    public void CreatePaper_DefaultBorderRadius_Is2px()
    {
        var theme = PinballTheme.CreatePaper();
        Assert.Equal("2px", theme.LayoutProperties.DefaultBorderRadius);
    }

    [Fact]
    public void CreatePaper_Elevation1_IsNone()
    {
        var theme = PinballTheme.CreatePaper();
        Assert.Equal("none", theme.Shadows.Elevation[1]);
    }
}
