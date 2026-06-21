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

using MudBlazor;

namespace PinballWizard.Web.Components.Theming;

public static class PinballTheme
{
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
            // ── Typography (Modern LCD spec — Barlow Condensed display, Inter body) ──
            // Display reserved for announcements (headers, panel titles,
            // refusal-panel category labels). Body is Inter through and
            // through — condensed-sans body is fatiguing.
            // JetBrains Mono is consumed via the --pw-font-mono variable
            // in app.css (citation IDs, machine slugs, URL chains).
            Typography = new Typography
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
            },
        };
    }
}
