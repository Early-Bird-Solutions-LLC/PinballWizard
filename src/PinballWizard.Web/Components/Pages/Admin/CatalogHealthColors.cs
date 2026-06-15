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
        CatalogHealthFlag.Empty      => Color.Error,
        CatalogHealthFlag.NoManual   => Color.Warning,
        CatalogHealthFlag.EditionGap => Color.Warning,
        CatalogHealthFlag.Ok         => Color.Success,
        _                            => Color.Default,
    };

    /// <summary>
    /// Returns the color for a flags list by inspecting the first (dominant) flag.
    /// An empty list or an Ok-only list returns <see cref="Color.Success"/>.
    /// </summary>
    public static Color ForFlags(IReadOnlyList<CatalogHealthFlag> flags) =>
        ForFlag(flags.Count > 0 ? flags[0] : CatalogHealthFlag.Ok);
}
