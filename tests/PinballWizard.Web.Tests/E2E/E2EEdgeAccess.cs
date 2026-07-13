using Microsoft.Playwright;

namespace PinballWizard.Web.Tests.E2E;

// Lets the E2E suite drive the REAL edge (https://pinwiz.ai — through Cloudflare's
// WAF, cache and security headers) rather than only the ACA origin FQDN, which is
// all the CI canary reaches. Several shipped defects lived exactly in that seam and
// were invisible to an origin-only canary: the enforced CSP, Cloudflare email
// obfuscation breaking admin identity, and enhanced-nav swallowing auth anchors.
//
// pinwiz.ai sits behind TWO independent gates, and they need different answers:
//
//   1. Cloudflare Access (One-time PIN). Answered by an Access SERVICE TOKEN —
//      CF-Access-Client-Id / CF-Access-Client-Secret on every request. Cloudflare
//      validates these itself and never redirects to the OTP screen.
//
//   2. Super Bot Fight Mode (sbfm_definitely_automated = "block", waf.tf). This runs
//      in a SEPARATE pipeline that an Access token does not exempt, so a headless
//      browser can still be blocked while holding a perfectly valid token. Hence
//      Headed — we do not punch a WAF hole in a public showcase site to save a window.
//
// All settings are opt-in via environment. With none set, this type is inert and the
// suite behaves exactly as it does in CI today: headless, no extra headers, origin FQDN.
internal static class E2EEdgeAccess
{
    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ClientId => Env("E2E__CfAccessClientId");
    private static string? ClientSecret => Env("E2E__CfAccessClientSecret");

    // True when we are driving the Cloudflare edge rather than the ACA origin — i.e. a service
    // token was supplied. Everything gated on this is inert for the CI canary.
    public static bool IsEdgeTarget => Headers is not null;

    // Present only when BOTH halves of the service token are supplied — half a
    // credential is a misconfiguration, and sending one header alone would fail at
    // the edge with a confusing OTP redirect rather than an obvious error.
    public static IReadOnlyDictionary<string, string>? Headers =>
        ClientId is { } id && ClientSecret is { } secret
            ? new Dictionary<string, string>
            {
                ["CF-Access-Client-Id"] = id,
                ["CF-Access-Client-Secret"] = secret,
            }
            : null;

    // Headed is required to clear Super Bot Fight Mode when driving the edge (see
    // above). Defaults to headless so CI — which targets the origin and never passes
    // through Cloudflare — is completely unaffected.
    public static bool Headed =>
        Env("E2E__Headed") is { } v &&
        (v.Equals("1", StringComparison.Ordinal) ||
         v.Equals("true", StringComparison.OrdinalIgnoreCase));

    public static BrowserTypeLaunchOptions LaunchOptions() => new() { Headless = !Headed };

    // Layers the Access headers onto a caller's context options without discarding
    // the per-test settings (viewport, colour scheme, …) each suite already relies on.
    public static BrowserNewContextOptions ContextOptions(BrowserNewContextOptions? options = null)
    {
        options ??= new BrowserNewContextOptions();
        if (Headers is { } headers)
        {
            options.ExtraHTTPHeaders = headers;
        }

        return options;
    }
}
