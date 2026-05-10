using PinballWizard.Application.Landing;

namespace PinballWizard.Web.Clients;

// Frontend abstraction for the /api/wizard/landing GET endpoint.
//
// Mirrors the IWizardStreamingClient pattern (PR-F2 sibling): a thin
// interface over an HttpClient so bUnit tests substitute a fake without
// spinning up a real HTTP server. Returns null on non-200 so callers can
// render the compiled-in fallback without an exception boundary.
//
// ADR-0026 § Landing surface — the Index page MUST render even when
// this endpoint is unavailable (prospect first impression must never 500).
public interface IWizardLandingClient
{
    // Fetches the landing payload from GET /api/wizard/landing.
    // Returns null on any non-200 response or transport failure — the
    // caller renders a compiled-in fallback in that case.
    Task<LandingResponse?> GetLandingAsync(CancellationToken cancellationToken);
}
