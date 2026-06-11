using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Pins the agreement between the project's scoped CSS (*.razor.css) and the
// document head in App.razor.
//
// Scoped CSS only ships if the host page links the build-generated bundle
// (PinballWizard.Web.styles.css) — the build emits it, but nothing wires it
// into the document automatically. The failure mode is silent and invisible
// to every other test layer: bUnit renders markup without stylesheets, the
// E2E suite asserts element presence rather than layout, and the app renders
// "fine" structurally. Discovered 2026-06-11: fourteen *.razor.css files
// (citation strip, landing surfaces, refusal panels, token animations) had
// never been applied in production because App.razor predated the first
// scoped-CSS file and was never updated.
//
// Sibling posture: CspPolicySyncTests pins the inline-script <-> edge-policy
// contract for the same document.
public sealed class ScopedCssBundleTests
{
    [Fact]
    public void AppRazor_LinksTheScopedCssBundle_WhileScopedCssExists()
    {
        var scopedCssFiles = Directory
            .EnumerateFiles(WebProjectRoot(), "*.razor.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // If the project ever drops scoped CSS entirely, the link (and this
        // test) can go with it — that removal should be deliberate.
        Assert.NotEmpty(scopedCssFiles);

        var appRazor = File.ReadAllText(Path.Combine(WebProjectRoot(), "Components", "App.razor"));

        Assert.Contains("PinballWizard.Web.styles.css", appRazor, StringComparison.Ordinal);
    }

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
