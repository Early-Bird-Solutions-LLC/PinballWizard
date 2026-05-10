using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinballWizard.Web.Tests.A11y;

// No-op authentication handler for Playwright accessibility tests.
// Replaces OpenIdConnect (Microsoft.Identity.Web) in the test host so the
// OIDC middleware doesn't intercept the Blazor SignalR circuit upgrade
// request and return its XHTML challenge page (lang="iv" —
// InvariantCulture). Public pages are [AllowAnonymous]; this handler
// simply returns NoResult so authorization passes through without
// issuing any challenge redirect.
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
