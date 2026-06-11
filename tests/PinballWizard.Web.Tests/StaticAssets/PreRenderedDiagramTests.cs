using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Pins the agreement between the About-page architecture diagram source
// (docs/diagrams/about-architecture.mmd) and the pre-rendered SVG the page
// actually serves (wwwroot/img/about-architecture.svg).
//
// The SVG is generated offline by mermaid-cli (regeneration command in the
// .mmd header) and committed — there is no build step that re-renders it,
// so nothing else stops the two files drifting apart. The contract: the
// SVG's second line carries an HTML comment embedding the SHA-256 of the
// .mmd source bytes. Editing the .mmd without re-rendering fails the hash
// test and the failure message prints the expected value.
//
// Sibling posture: CspPolicySyncTests (same folder) pins the CSP <-> source
// contract this diagram used to participate in (the client-side Mermaid
// render needed a CDN script + inline-init hash; the pre-rendered SVG
// needs neither — see the 2026-06-11 decision-log entry "Pre-rendered SVG
// replaces client-side Mermaid").
public sealed class PreRenderedDiagramTests
{
    private static readonly Regex SourceCommentRegex = new(
        @"<!--\s*source:\s*(?<path>\S+)\s+sha256:(?<hash>[0-9a-f]{64})\s*-->",
        RegexOptions.Compiled);

    [Fact]
    public void AboutArchitectureSvg_Exists_AndIsNonTrivial()
    {
        var svgPath = SvgPath();

        Assert.True(File.Exists(svgPath), $"Pre-rendered diagram missing: {svgPath} — regenerate per the .mmd header.");
        Assert.True(new FileInfo(svgPath).Length > 1_000, "SVG is implausibly small — the render likely failed.");
    }

    [Fact]
    public void AboutArchitectureSvg_EmbedsTheSourceHash_AndItMatchesTheMmdFile()
    {
        var svg = File.ReadAllText(SvgPath());

        var comment = SourceCommentRegex.Match(svg);
        Assert.True(
            comment.Success,
            "SVG must embed '<!-- source: docs/diagrams/about-architecture.mmd sha256:<hash> -->' " +
            "on its second line — see the regeneration instructions in the .mmd header.");
        Assert.Equal("docs/diagrams/about-architecture.mmd", comment.Groups["path"].Value);

        // LF-normalized to match the .gitattributes checkout policy, same
        // convention as CspPolicySyncTests' inline-script hashing.
        var mmdBytes = File.ReadAllText(MmdPath()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var expected = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mmdBytes)))
            .ToLowerInvariant();

        Assert.True(
            expected == comment.Groups["hash"].Value,
            $"Diagram source and rendered SVG have drifted apart. The .mmd hashes to {expected} but the SVG " +
            $"embeds {comment.Groups["hash"].Value} — re-render per the .mmd header and refresh the comment.");
    }

    [Fact]
    public void AboutRazor_ServesTheSvg_NotClientSideMermaid()
    {
        var aboutRazor = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "PinballWizard.Web", "Components", "Pages", "About.razor"));

        Assert.Contains("img/about-architecture.svg", aboutRazor, StringComparison.Ordinal);

        // The whole point of pre-rendering: no CDN script, no client-side
        // render hook, no InteractiveServer requirement on a static content
        // page. (Prose mentions of "Mermaid" in comments are fine — these
        // are the executable artifacts.)
        Assert.DoesNotContain("cdn.jsdelivr.net", aboutRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=\"mermaid\"", aboutRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mermaid.initialize", aboutRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@rendermode", aboutRazor, StringComparison.Ordinal);
    }

    private static string SvgPath() =>
        Path.Combine(RepoRoot(), "src", "PinballWizard.Web", "wwwroot", "img", "about-architecture.svg");

    private static string MmdPath() =>
        Path.Combine(RepoRoot(), "docs", "diagrams", "about-architecture.mmd");

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
