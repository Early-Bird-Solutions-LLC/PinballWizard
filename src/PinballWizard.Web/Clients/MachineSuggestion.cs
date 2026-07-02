namespace PinballWizard.Web.Clients;

// Web-side DTO for machine typeahead suggestions returned by
// GET /api/machines/suggest?q={query}&top={n}.
//
// Year is nullable per the backend contract — older catalog entries may not
// carry a year, and newer machines are sometimes listed before release year
// is confirmed.
//
// This is a Web-layer-only DTO (not shared with Application/Core), so it
// lives alongside the client interface that produces it.  It is a record so
// MudAutocomplete<MachineSuggestion?> can use it as T without requiring
// manual GetHashCode/Equals overrides (record provides value-based equality).
public sealed record MachineSuggestion(
    string OpdbId,
    string Title,
    string Manufacturer,
    int? Year);
