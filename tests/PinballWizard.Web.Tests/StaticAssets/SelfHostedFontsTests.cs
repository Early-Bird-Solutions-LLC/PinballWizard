using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Regression test for the self-hosted-fonts conversion.
//
// Why: PinballWizard.Web previously loaded Roboto from `fonts.googleapis.com`,
// which leaks visitor IPs to Google on every page load. The German court ruling
// LG München I, Az. 3 O 17493/20 (January 2022) classified that as a GDPR
// violation. Per CLAUDE.md § Showcase obligations, the demo must not exhibit
// patterns a privacy-aware prospect would flag — so the fonts now ship as
// self-hosted woff2 files under wwwroot/fonts/ + @font-face in app.css.
//
// These tests pin the conversion so a future drive-by edit can't silently
// re-introduce the CDN dependency.
public sealed class SelfHostedFontsTests
{
    private static readonly (string Family, int Weight, string FileSlug, string DirSlug)[] ExpectedWeights =
    [
        ("Inter", 400, "inter-latin-400-normal.woff2", "inter"),
        ("Inter", 500, "inter-latin-500-normal.woff2", "inter"),
        ("Inter", 600, "inter-latin-600-normal.woff2", "inter"),
        ("Inter", 700, "inter-latin-700-normal.woff2", "inter"),
        ("Barlow Condensed", 500, "barlow-condensed-latin-500-normal.woff2", "barlow-condensed"),
        ("Barlow Condensed", 700, "barlow-condensed-latin-700-normal.woff2", "barlow-condensed"),
        ("JetBrains Mono", 400, "jetbrains-mono-latin-400-normal.woff2", "jetbrains-mono"),
        ("JetBrains Mono", 500, "jetbrains-mono-latin-500-normal.woff2", "jetbrains-mono"),
        ("Roboto", 300, "roboto-latin-300-normal.woff2", "roboto"),
        ("Roboto", 400, "roboto-latin-400-normal.woff2", "roboto"),
        ("Roboto", 500, "roboto-latin-500-normal.woff2", "roboto"),
        ("Roboto", 700, "roboto-latin-700-normal.woff2", "roboto"),
    ];

    [Fact]
    public void AppRazor_LoadsNoFontsFromGoogleCdn()
    {
        var appRazor = File.ReadAllText(Path.Combine(WebProjectRoot(), "Components", "App.razor"));

        // Match scheme-prefixed URLs only — prose mentions in comments are
        // legitimate (e.g., "we self-host to avoid leaking IPs to Google").
        // What we forbid is an actual <link>/url() to either Google CDN.
        Assert.DoesNotContain("https://fonts.googleapis.com", appRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://fonts.gstatic.com", appRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//fonts.googleapis.com", appRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//fonts.gstatic.com", appRazor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppCss_LoadsNoFontsFromGoogleCdn()
    {
        var appCss = File.ReadAllText(Path.Combine(WebProjectRoot(), "wwwroot", "app.css"));

        Assert.DoesNotContain("https://fonts.googleapis.com", appCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://fonts.gstatic.com", appCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//fonts.googleapis.com", appCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//fonts.gstatic.com", appCss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppCss_DeclaresEveryExpectedFontFace()
    {
        var appCss = File.ReadAllText(Path.Combine(WebProjectRoot(), "wwwroot", "app.css"));

        foreach (var (family, weight, fileSlug, dirSlug) in ExpectedWeights)
        {
            // Match the family + weight pair within a single @font-face block,
            // and require `font-display: swap` to co-occur in that same block.
            // `font-display: swap` is the FOIT/FOUT mitigation that makes
            // self-hosting acceptable — pinning it per-block prevents a future
            // edit from adding an unswapped block while the global Contains
            // check below still passes.
            var blockPattern = new Regex(
                $@"@font-face\s*\{{(?=[^}}]*font-family:\s*'{Regex.Escape(family)}')(?=[^}}]*font-weight:\s*{weight}\b)(?=[^}}]*font-display:\s*swap)[^}}]*\}}",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            Assert.True(
                blockPattern.IsMatch(appCss),
                $"app.css missing @font-face for {family} {weight} (with font-display: swap)");

            Assert.Contains(
                $"fonts/{dirSlug}/{fileSlug}",
                appCss,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryReferencedFontFileExistsOnDisk()
    {
        var fontsRoot = Path.Combine(WebProjectRoot(), "wwwroot", "fonts");

        foreach (var (_, _, fileSlug, dirSlug) in ExpectedWeights)
        {
            var path = Path.Combine(fontsRoot, dirSlug, fileSlug);
            Assert.True(File.Exists(path), $"Missing font file: {path}");

            // woff2 magic bytes are 'wOF2' (0x77 0x4F 0x46 0x32). Reject anything
            // that isn't a real woff2 — git LFS pointer files, accidental ttf/otf
            // copies, empty placeholders all fail this check.
            var magic = new byte[4];
            using (var stream = File.OpenRead(path))
            {
                var read = stream.Read(magic, 0, magic.Length);
                Assert.Equal(4, read);
            }
            Assert.Equal((byte)'w', magic[0]);
            Assert.Equal((byte)'O', magic[1]);
            Assert.Equal((byte)'F', magic[2]);
            Assert.Equal((byte)'2', magic[3]);
        }
    }

    [Fact]
    public void EveryFontFamilyDirectoryHasLicenseFile()
    {
        var fontsRoot = Path.Combine(WebProjectRoot(), "wwwroot", "fonts");

        foreach (var dirSlug in ExpectedWeights.Select(w => w.DirSlug).Distinct(StringComparer.Ordinal))
        {
            var licensePath = Path.Combine(fontsRoot, dirSlug, "LICENSE.txt");
            Assert.True(File.Exists(licensePath), $"Missing LICENSE.txt for {dirSlug}");

            var contents = File.ReadAllText(licensePath);
            Assert.Contains("SIL Open Font License", contents, StringComparison.Ordinal);
        }
    }

    private static string WebProjectRoot()
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
        return Path.Combine(dir.FullName, "src", "PinballWizard.Web");
    }
}
