namespace PinballWizard.Web.Clients;

// Frontend abstraction for the GET /api/machines/suggest typeahead endpoint.
//
// Mirrors the IWizardLandingClient pattern: a thin interface over HttpClient
// so bUnit tests can substitute a fake without spinning up a real HTTP server.
// Returns an empty list on any non-200 response or transport failure — the
// caller (LandingHero's SearchFunc) returns that empty list to MudAutocomplete,
// which renders no suggestions.  The hero degrades silently: free-text Enter
// still works, so the user is never blocked.
//
// Short queries (<2 chars) return [] per the backend contract.  The
// MudAutocomplete's MinCharacters=2 prevents calls below that threshold, but
// the client enforces it too as a belt-and-suspenders guard.
//
// ADR-0049 Phase 3 — landing hero typeahead.
public interface IMachineSuggestClient
{
    // Fetches machine title suggestions for the given query string.
    // Returns an empty list on any non-200 response, transport failure, or
    // when the query is too short.  Never throws into the UI.
    Task<IReadOnlyList<MachineSuggestion>> GetSuggestionsAsync(
        string query,
        CancellationToken cancellationToken);
}
