namespace PinballWizard.Web.Components.Layout;

/// One entry in an <see cref="AppNavRail"/>. Href is the route; Icon is a
/// MudBlazor Icons.Material.* value; MatchAll selects NavLinkMatch.All (used
/// for "/" so it does not stay highlighted on every child route).
public sealed record NavRailItem(string Href, string Label, string Icon, bool MatchAll = false);
