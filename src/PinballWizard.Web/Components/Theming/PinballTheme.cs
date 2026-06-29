// PinballWizard brand theme tokens for MudBlazor.
//
// Defines the palette, typography, and shape used across the entire
// Blazor Web App. All layout and chrome components reference this theme
// via MudThemeProvider.
//
// Spec authority: docs/ui/themes/modern-lcd.md § Visual system.
// ADR-0008 — MudBlazor strict for all chrome (single theming system)
// ADR-0026 § 6 — Component strategy: custom components for delight
//                surfaces only; chrome uses MudBlazor primitives + tokens.
//
// Tokens MudPalette can't model (surface-hi, accent-mode, border-glow
// alpha tints, font families) live as :root custom properties in
// wwwroot/app.css. This is the chrome-strict / custom-for-delight split
// from ADR-0026 § 6 expressed at the token layer.
//
// IsDarkMode="true" is set in MainLayout — MudBlazor reads PaletteDark when
// dark mode is active. PaletteDark MUST be explicitly set; without it MudBlazor
// falls back to its own built-in defaults (Primary ≈ #776be7 indigo/violet),
// which was the root cause of the magenta-looking submit adornment and other
// Primary-colored chrome. The Modern LCD theme is dark-only; PaletteDark carries
// the canonical Modern LCD token values (identical to PaletteLight since this
// theme has one visual register).

using System.Linq;
using MudBlazor;

namespace PinballWizard.Web.Components.Theming;

public static class PinballTheme
{
    // ── Shared typography (Barlow Condensed display, Inter body) ──
    // Both Create() and CreatePaper() use identical typography settings.
    // Display reserved for announcements (headers, panel titles,
    // refusal-panel category labels). Body is Inter throughout —
    // condensed-sans body is fatiguing.
    // JetBrains Mono is consumed via the --pw-font-mono variable
    // in app.css (citation IDs, machine slugs, URL chains).
    private static Typography PinballTypography => new()
    {
        Default = new DefaultTypography
        {
            FontFamily = ["Inter", "Roboto", "Helvetica", "Arial", "sans-serif"],
            FontSize = "0.875rem",
            FontWeight = "400",
            LineHeight = "1.43",
            LetterSpacing = "0.01071em",
        },
        H1 = new H1Typography { FontFamily = ["Barlow Condensed", "Roboto", "sans-serif"], FontSize = "2.5rem",  FontWeight = "700" },
        H2 = new H2Typography { FontFamily = ["Barlow Condensed", "Roboto", "sans-serif"], FontSize = "2rem",    FontWeight = "700" },
        H3 = new H3Typography { FontFamily = ["Barlow Condensed", "Roboto", "sans-serif"], FontSize = "1.75rem", FontWeight = "700" },
        H4 = new H4Typography { FontFamily = ["Barlow Condensed", "Roboto", "sans-serif"], FontSize = "1.5rem",  FontWeight = "700" },
        H5 = new H5Typography { FontFamily = ["Barlow Condensed", "Roboto", "sans-serif"], FontSize = "1.25rem", FontWeight = "500" },
        H6 = new H6Typography { FontFamily = ["Barlow Condensed", "Roboto", "sans-serif"], FontSize = "1rem",    FontWeight = "500" },
        Subtitle1 = new Subtitle1Typography { FontFamily = ["Inter", "Roboto", "sans-serif"], FontSize = "1rem",   FontWeight = "400" },
        Subtitle2 = new Subtitle2Typography { FontFamily = ["Inter", "Roboto", "sans-serif"], FontSize = "0.875rem", FontWeight = "500" },
        Body1 = new Body1Typography { FontFamily = ["Inter", "Roboto", "sans-serif"], FontSize = "1rem",   FontWeight = "400", LineHeight = "1.5" },
        Body2 = new Body2Typography { FontFamily = ["Inter", "Roboto", "sans-serif"], FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.43" },
        Caption = new CaptionTypography { FontFamily = ["Inter", "Roboto", "sans-serif"], FontSize = "0.75rem", FontWeight = "400" },
        Overline = new OverlineTypography { FontFamily = ["Inter", "Roboto", "sans-serif"], FontSize = "0.75rem", FontWeight = "400", LetterSpacing = "0.08333em" },
    };

    // ── Shared shape and elevation ──
    // Flat-elevation design: all shadow levels resolve to "none" so the
    // MudBlazor chrome doesn't add Material-style drop shadows. Border-radius
    // is kept at 2px for a sharp, technical aesthetic consistent with
    // the Modern LCD and Paper visual systems.
    private static LayoutProperties PinballLayoutProperties => new()
    {
        DefaultBorderRadius = "2px",
    };

    private static Shadow PinballShadows => new()
    {
        // MudBlazor 9.x Shadow.Elevation is a 26-element array (indices 0–25).
        // MudThemeProvider.GenerateTheme accesses all 26 slots; supplying 25
        // causes an IndexOutOfRangeException during render.
        Elevation = Enumerable.Repeat("none", 26).ToArray(),
    };

    public static MudTheme Create()
    {
        return new MudTheme
        {
            // ── Palette (Modern LCD spec — docs/ui/themes/modern-lcd.md) ──
            // Warm near-black base + warm off-white text reads as
            // "machine in a dim arcade", not "phone OLED" / "medical app".
            PaletteLight = new PaletteLight
            {
                Primary = "#ff9a1f",          // accent-primary — arcade amber
                PrimaryContrastText = "#1a1a1a",
                Secondary = "#9e9e9e",        // steel silver
                SecondaryContrastText = "#ffffff",
                Background = "#0c0b0e",       // bg-base — warm near-black
                Surface = "#161519",          // bg-surface — panel interiors
                AppbarBackground = "#08070a", // recessed header — slightly darker than bg-base
                AppbarText = "#f4f1ea",
                DrawerBackground = "#101015",
                DrawerText = "#e0dcd5",
                DrawerIcon = "#ff9a1f",
                TextPrimary = "#f4f1ea",      // warm off-white (NOT clinical)
                TextSecondary = "#9a9590",
                TextDisabled = "#5e5b56",
                ActionDefault = "#ff9a1f",
                ActionDisabled = "#3a3a3a",
                Divider = "#2a282d",          // border-quiet
                Info = "#2196f3",
                Success = "#34d96a",          // accent-grounded — atomic green / GI glow
                Warning = "#ff9800",
                Error = "#ff3b30",            // accent-refusal — saturated red (NOT crimson)
                ErrorContrastText = "#ffffff",
            },
            // ── PaletteDark (REQUIRED — IsDarkMode="true" in MainLayout) ──
            // Without an explicit PaletteDark, MudBlazor falls back to its
            // built-in defaults (Primary ≈ #776be7 indigo/violet), which was
            // the root cause of the magenta submit adornment and other
            // Primary-colored chrome. Modern LCD is dark-only; these values
            // mirror PaletteLight exactly (single visual register).
            PaletteDark = new PaletteDark
            {
                Primary = "#ff9a1f",          // accent-primary — arcade amber
                PrimaryContrastText = "#1a1a1a",
                Secondary = "#9e9e9e",        // steel silver
                SecondaryContrastText = "#ffffff",
                Background = "#0c0b0e",       // bg-base — warm near-black
                Surface = "#161519",          // bg-surface — panel interiors
                AppbarBackground = "#08070a", // recessed header — slightly darker than bg-base
                AppbarText = "#f4f1ea",
                DrawerBackground = "#101015",
                DrawerText = "#e0dcd5",
                DrawerIcon = "#ff9a1f",
                TextPrimary = "#f4f1ea",      // warm off-white (NOT clinical)
                TextSecondary = "#9a9590",
                TextDisabled = "#5e5b56",
                ActionDefault = "#ff9a1f",
                ActionDisabled = "#3a3a3a",
                Divider = "#2a282d",          // border-quiet
                Info = "#2196f3",
                Success = "#34d96a",          // accent-grounded — atomic green / GI glow
                Warning = "#ff9800",
                Error = "#ff3b30",            // accent-refusal — saturated red (NOT crimson)
                ErrorContrastText = "#ffffff",
                OverlayDark = "rgba(12, 11, 14, 0.8)",
                OverlayLight = "rgba(30, 29, 34, 0.6)",
            },
            Typography = PinballTypography,
            LayoutProperties = PinballLayoutProperties,
            Shadows = PinballShadows,
        };
    }

    // ── Paper light palette (docs/ui/themes/sibling-themes-overview.md § Paper) ──
    // Warm off-white / aged-paper base with earthy accent tones.
    // The Paper theme is the default for new visitors (chore(theme) #570).
    // It is the only sibling theme with a full MudTheme companion — all
    // other sibling themes (Backbox, Cabinet, DaytimeRoute, DmdClassic)
    // are CSS-variable-only overrides applied via ThemeService.
    public static MudTheme CreatePaper() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary                = "#b8763e",   // accent-primary — warm copper
            PrimaryContrastText    = "#ffffff",
            Secondary              = "#1f6f54",   // accent-grounded — forest green
            Background             = "#f4f1ea",   // bg-base — aged paper
            Surface                = "#faf8f2",   // bg-surface — lighter paper
            AppbarBackground       = "#1a1410",   // near-black header
            AppbarText             = "#f4f1ea",
            DrawerBackground       = "#ede9e1",
            DrawerText             = "#1a1408",
            TextPrimary            = "#1a1408",   // near-black text
            TextSecondary          = "#5c5042",   // warm mid-tone
            TextDisabled           = "#9a9082",
            ActionDefault          = "#5c5042",
            ActionDisabled         = "#9a9082",
            Divider                = "#d8cdb5",   // border-quiet — warm sand
            DividerLight           = "#e8e3da",
            Success                = "#1a8a45",
            SuccessContrastText    = "#ffffff",
            Error                  = "#c0200e",
            ErrorContrastText      = "#ffffff",
            Warning                = "#b86c00",
            WarningContrastText    = "#ffffff",
            Info                   = "#1a5c8a",
            InfoContrastText       = "#ffffff",
            GrayDefault            = "#5c5042",
            GrayLight              = "#d8cdb5",
            GrayLighter            = "#ede9e1",
            GrayDark               = "#3a2e22",
            GrayDarker             = "#1a1408",
            OverlayLight           = "rgba(244, 241, 234, 0.5)",
            OverlayDark            = "rgba(26, 20, 8, 0.7)",
        },
        PaletteDark = new PaletteDark
        {
            Primary          = "#ff9a1f",
            Background       = "#0c0b0e",
            Surface          = "#161519",
            AppbarBackground = "#08070a",
            DrawerBackground = "#101015",
            TextPrimary      = "#f4f1ea",
            TextSecondary    = "#9a9590",
            Success          = "#34d96a",
            Error            = "#ff3b30",
            Divider          = "#2a282d",
        },
        Typography = PinballTypography,
        LayoutProperties = PinballLayoutProperties,
        Shadows = PinballShadows,
    };
}
