using MudBlazor;
using PinballWizard.Application.Catalog;

namespace PinballWizard.Web.Components.Pages.Admin;

/// <summary>
/// Maps <see cref="CatalogHealthFlag"/> values to MudBlazor <see cref="Color"/> tokens.
/// Shared by AdminMachines and AdminMachineDetail so a new flag only requires one update here.
/// No hex values — MudBlazor theme tokens only (ADR-0008 / FE-08).
/// </summary>
internal static class CatalogHealthColors
{
    /// <summary>Returns the MudBlazor Color for a single <see cref="CatalogHealthFlag"/>.</summary>
    public static Color ForFlag(CatalogHealthFlag flag) => flag switch
    {
        CatalogHealthFlag.Empty      => Color.Error,   // missing catalog → failure
        CatalogHealthFlag.NoManual   => Color.Default, // informational health flag → neutral
        CatalogHealthFlag.EditionGap => Color.Default, // informational health flag → neutral
        CatalogHealthFlag.Ok         => Color.Success,
        _                            => Color.Default,
    };

    /// <summary>
    /// Returns the color for a flags list by inspecting the first (dominant) flag.
    /// An empty list or an Ok-only list returns <see cref="Color.Success"/>.
    /// </summary>
    public static Color ForFlags(IReadOnlyList<CatalogHealthFlag> flags) =>
        ForFlag(flags.Count > 0 ? flags[0] : CatalogHealthFlag.Ok);

    /// <summary>
    /// Human-readable description for the health-badge legend and per-badge tooltips.
    /// Single source of truth — consumed by <c>CatalogHealthLegend</c> and the
    /// AdminMachines Health-column tooltip.
    /// </summary>
    public static string Describe(CatalogHealthFlag flag) => flag switch
    {
        CatalogHealthFlag.Ok         => "Healthy — documents present, including a manual.",
        CatalogHealthFlag.Empty      => "No documents linked to this machine yet.",
        CatalogHealthFlag.NoManual   => "Has documents, but no manual.",
        CatalogHealthFlag.EditionGap => "Another edition of this game has more documents — this edition may be under-covered.",
        _                            => string.Empty,
    };
}
