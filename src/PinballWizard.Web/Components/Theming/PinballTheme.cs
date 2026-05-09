// PinballWizard brand theme tokens for MudBlazor.
//
// Defines the palette, typography, and shape used across the entire
// Blazor Web App. All layout and chrome components reference this theme
// via MudThemeProvider — Wave 1 ships the baseline; Wave 2 PR-D-degraded
// layers prefers-reduced-motion handling for pinball micro-interactions.
//
// ADR-0008 — MudBlazor strict for all chrome (single theming system)
// ADR-0026 § 6 — Component strategy: custom components for delight
//                surfaces only; chrome uses MudBlazor primitives + tokens.

using MudBlazor;

namespace PinballWizard.Web.Components.Theming;

/// <summary>
/// Provides the PinballWizard brand <see cref="MudTheme"/> instance.
/// Inject this class or call <see cref="Create"/> directly from the
/// layout component that mounts <see cref="MudThemeProvider"/>.
/// </summary>
public static class PinballTheme
{
    /// <summary>
    /// Creates the Wave 1 baseline MudTheme instance.
    /// </summary>
    public static MudTheme Create()
    {
        return new MudTheme
        {
            // ── Palette (light mode baseline) ─────────────────────────────
            // Primary: arcade amber — confident, warm, energetic.
            // Secondary: steel silver — precision engineering complement.
            // Background: near-black for the playfield feel; surfaces lift
            // to charcoal so elevation is visible on dark backgrounds.
            PaletteLight = new PaletteLight
            {
                Primary = "#F5A623",          // arcade amber
                PrimaryContrastText = "#1A1A1A",
                Secondary = "#9E9E9E",        // steel silver
                SecondaryContrastText = "#FFFFFF",
                Background = "#121212",       // deep playfield
                Surface = "#1E1E1E",          // component surface
                AppbarBackground = "#0D0D0D", // darker header
                AppbarText = "#F5F5F5",
                DrawerBackground = "#161616",
                DrawerText = "#E0E0E0",
                DrawerIcon = "#F5A623",
                TextPrimary = "#F0F0F0",
                TextSecondary = "#BDBDBD",
                TextDisabled = "#616161",
                ActionDefault = "#F5A623",
                ActionDisabled = "#424242",
                Divider = "#2A2A2A",
                Info = "#2196F3",
                Success = "#4CAF50",
                Warning = "#FF9800",
                Error = "#F44336",
                ErrorContrastText = "#FFFFFF",
            },
            // ── Typography ────────────────────────────────────────────────
            // Roboto from App.razor head (already loaded).
            // H4/H5 are the primary question/answer typescales.
            Typography = new Typography
            {
                Default = new DefaultTypography
                {
                    FontFamily = ["Roboto", "Helvetica", "Arial", "sans-serif"],
                    FontSize = "0.875rem",
                    FontWeight = "400",
                    LineHeight = "1.43",
                    LetterSpacing = "0.01071em",
                },
                H1 = new H1Typography { FontSize = "2.5rem", FontWeight = "700" },
                H2 = new H2Typography { FontSize = "2rem",   FontWeight = "700" },
                H3 = new H3Typography { FontSize = "1.75rem", FontWeight = "600" },
                H4 = new H4Typography { FontSize = "1.5rem", FontWeight = "600" },
                H5 = new H5Typography { FontSize = "1.25rem", FontWeight = "500" },
                H6 = new H6Typography { FontSize = "1rem",   FontWeight = "500" },
                Subtitle1 = new Subtitle1Typography { FontSize = "1rem",   FontWeight = "400" },
                Subtitle2 = new Subtitle2Typography { FontSize = "0.875rem", FontWeight = "500" },
                Body1 = new Body1Typography { FontSize = "1rem",   FontWeight = "400", LineHeight = "1.5" },
                Body2 = new Body2Typography { FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.43" },
                Caption = new CaptionTypography { FontSize = "0.75rem", FontWeight = "400" },
                Overline = new OverlineTypography { FontSize = "0.75rem", FontWeight = "400", LetterSpacing = "0.08333em" },
            },
            // ── Shape ─────────────────────────────────────────────────────
            // Slightly rounded corners throughout — softer than default
            // (4px) but less rounded than card-heavy UIs. Matches the
            // pinball machine cabinet aesthetic (clean lines, soft edges).
        };
    }
}
