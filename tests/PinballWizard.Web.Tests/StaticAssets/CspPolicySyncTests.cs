using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Pins the agreement between the app's inline scripts and the CSP injected
// at the Cloudflare edge (infra/cloudflare/headers.tf, issue #356).
//
// The edge policy allows the app's two inline scripts by SHA-256 hash and
// the version-pinned Mermaid CDN bundle by exact URL + SRI. None of those
// values are visible to the compiler — editing an inline script (even
// whitespace), bumping the Mermaid version, or dropping the integrity
// attribute silently breaks the agreement, and the failure mode is
// report-only violations today / a broken page once the policy is promoted
// to enforced. These tests recompute the hashes from the .razor sources the
// same way the browser does (exact bytes between <script> and </script>,
// LF line endings per .gitattributes `eol=lf`) and assert headers.tf
// carries them.
//
// Sibling posture: SelfHostedFontsTests (same folder) pins the no-CDN-fonts
// decision; this class pins the CSP <-> source contract.
public sealed class CspPolicySyncTests
{
    private static readonly Regex InlineScriptRegex = new(
        @"<script(?![^>]*\ssrc=)[^>]*>(?<body>[\s\S]*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MermaidScriptTagRegex = new(
        @"<script\s[^>]*src=""(?<url>https://cdn\.jsdelivr\.net/npm/mermaid@[^""]+)""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void AppRazor_InlineBootstrapScript_HashIsAllowedByEdgePolicy()
    {
        var hashes = InlineScriptHashes(WebFile("Components", "App.razor"));

        var single = Assert.Single(hashes);
        Assert.Contains($"'{single}'", HeadersTf(), StringComparison.Ordinal);
    }

    [Fact]
    public void AboutRazor_InlineMermaidInitScript_HashIsAllowedByEdgePolicy()
    {
        var hashes = InlineScriptHashes(WebFile("Components", "Pages", "About.razor"));

        var single = Assert.Single(hashes);
        Assert.Contains($"'{single}'", HeadersTf(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoOtherRazorFile_IntroducesAnInlineScript()
    {
        // Every inline script needs a hash in the edge policy, so the set of
        // files allowed to carry one is closed. A new inline <script> in any
        // other component would render fine today (policy is report-only)
        // and break silently at §7.2 enforcement promotion — fail it here
        // instead, at authoring time.
        var allowed = new[]
        {
            WebFile("Components", "App.razor"),
            WebFile("Components", "Pages", "About.razor"),
        };

        var offenders = Directory
            .EnumerateFiles(WebProjectRoot(), "*.razor", SearchOption.AllDirectories)
            .Where(f => !allowed.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Where(f => InlineScriptRegex.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(WebProjectRoot(), f))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Inline <script> found outside the CSP-hashed set — add a SHA-256 hash to " +
            $"infra/cloudflare/headers.tf and extend CspPolicySyncTests, or externalize it: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void MermaidCdnScript_IsVersionPinned_WithSriIntegrity_AndAllowedByEdgePolicy()
    {
        var aboutRazor = File.ReadAllText(WebFile("Components", "Pages", "About.razor"));

        var tag = MermaidScriptTagRegex.Match(aboutRazor);
        Assert.True(tag.Success, "About.razor must load Mermaid via a version-pinned jsDelivr URL (mermaid@<version>).");

        // SRI + anonymous CORS make a tampered CDN response fail closed
        // instead of executing (the CSP URL allowance alone doesn't verify
        // content). crossorigin is required for integrity checks on
        // cross-origin scripts — without it browsers fail open.
        //
        // The full hash literal is asserted (not just the sha384- prefix):
        // the bundle isn't local, so the test can't recompute it, and a
        // wrong value would pass a prefix check while silently killing the
        // diagram in the browser. When bumping the Mermaid pin, recompute
        // with:  curl -s <pinned-url> | openssl dgst -sha384 -binary | base64
        // and update About.razor, headers.tf, and this literal together.
        Assert.Contains(
            "integrity=\"sha384-yQ4mmBBT+vhTAwjFH0toJXNYJ6O4usWnt6EPIdWwrRvx2V/n5lXuDZQwQFeSFydF\"",
            tag.Value,
            StringComparison.Ordinal);
        Assert.Contains("crossorigin=\"anonymous\"", tag.Value, StringComparison.Ordinal);

        // The exact pinned URL must be a script-src source in the edge policy.
        Assert.Contains(tag.Groups["url"].Value, HeadersTf(), StringComparison.Ordinal);
    }

    [Fact]
    public void EdgePolicy_ScriptSrc_StaysStrict()
    {
        // The XSS-load-bearing directive: hashes and the pinned CDN URL only.
        // 'unsafe-inline' / 'unsafe-eval' in script-src would gut the policy;
        // style-src 'unsafe-inline' is the deliberate, documented MudBlazor
        // concession and is asserted separately below.
        var scriptSrc = DirectiveValue("script-src");

        Assert.DoesNotContain("'unsafe-inline'", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-hashes'", scriptSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void EdgePolicy_CarriesTheBlazorBaselineDirectives()
    {
        // object-src 'none' is in every Microsoft-recommended Blazor policy;
        // explicit wss://pinwiz.ai keeps the SignalR circuit alive on engines
        // that don't extend 'self' to WebSocket schemes; style-src
        // 'unsafe-inline' is the locked MudBlazor posture (see headers.tf
        // rationale comments).
        Assert.Contains("'none'", DirectiveValue("object-src"), StringComparison.Ordinal);
        Assert.Contains("wss://pinwiz.ai", DirectiveValue("connect-src"), StringComparison.Ordinal);
        Assert.Contains("'unsafe-inline'", DirectiveValue("style-src"), StringComparison.Ordinal);
    }

    // Computes the CSP source-expression hash for every inline script in the
    // file: SHA-256 over the exact bytes between <script> and </script>,
    // CRLF-normalized to LF to match both the .gitattributes checkout policy
    // and the bytes the Linux-built container serves.
    private static List<string> InlineScriptHashes(string razorPath)
    {
        var content = File.ReadAllText(razorPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        return InlineScriptRegex
            .Matches(content)
            .Select(m => m.Groups["body"].Value)
            .Select(body => $"sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}")
            .ToList();
    }

    private static string DirectiveValue(string directive)
    {
        var match = Regex.Match(HeadersTf(), $@"""{Regex.Escape(directive)}\s+([^""]+)""");
        Assert.True(match.Success, $"headers.tf CSP is missing the {directive} directive.");
        return match.Groups[1].Value;
    }

    private static string HeadersTf() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "cloudflare", "headers.tf"));

    private static string WebFile(params string[] segments) =>
        Path.Combine([WebProjectRoot(), .. segments]);

    private static string WebProjectRoot() =>
        Path.Combine(RepoRoot(), "src", "PinballWizard.Web");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
        }
        return dir.FullName;
    }
}
