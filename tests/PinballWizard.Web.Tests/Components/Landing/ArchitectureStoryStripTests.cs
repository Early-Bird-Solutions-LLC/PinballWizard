using System.Text.RegularExpressions;
using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit smoke tests for ArchitectureStoryStrip.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. Tests assert behavior (cards render, each card has a
// link) — not CSS class names or internal MudBlazor markup.
public sealed class ArchitectureStoryStripTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 1. Renders at least 3 cards
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureStoryStrip_RendersAtLeastThreeCards()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();

        var cards = cut.FindAll("[data-testid^='arch-card-']");
        Assert.True(cards.Count >= 3,
            $"ArchitectureStoryStrip must render at least 3 cards. Got {cards.Count}.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Each card contains a link to a doc or ADR
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureStoryStrip_EachCard_HasLink()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();

        var cards = cut.FindAll("[data-testid^='arch-card-']");
        Assert.True(cards.Count >= 3, "Precondition: at least 3 cards.");

        foreach (var card in cards)
        {
            var links = card.QuerySelectorAll("a[href]");
            Assert.True(links.Length >= 1,
                $"Each architecture card must contain at least one link. Card id: {card.GetAttribute("data-testid")}");

            // Each link's href must be non-empty.
            foreach (var link in links)
            {
                var href = link.GetAttribute("href");
                Assert.False(string.IsNullOrWhiteSpace(href),
                    "Architecture card link href must not be empty.");
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2b. Every GitHub repo link resolves to a real, on-disk ADR artifact.
    //
    // The strip's links shipped 404ing (observed live 2026-06-15): the hrefs
    // hardcoded the wrong repo owner (jkeeley2073 instead of the real
    // Early-Bird-Solutions-LLC), used /blob/ for a directory (GitHub serves
    // directories under /tree/), and named a stale ADR-0022 filename. The
    // pre-existing tests only checked href != empty, so all three sailed
    // through. This test pins the links to artifacts that actually exist in
    // the repo — owner, blob-vs-tree, and exact filename — without a network
    // call. (A live HTTP probe would be flaky and couple the unit suite to
    // GitHub availability; on-disk resolution catches the same drift.)
    // ──────────────────────────────────────────────────────────────────────

    private static readonly Regex RepoLinkRegex = new(
        @"^https://github\.com/(?<owner>[^/]+)/PinballWizard/(?<kind>blob|tree)/main/(?<path>.+?)/?$",
        RegexOptions.Compiled);

    private const string ExpectedOwner = "Early-Bird-Solutions-LLC";

    [Fact]
    public void ArchitectureStoryStrip_AdrLinks_ResolveToRealRepoArtifacts()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();
        var repoRoot = RepoRoot();

        var repoLinks = cut.FindAll("a[href]")
            .Select(a => a.GetAttribute("href") ?? string.Empty)
            .Where(h => h.Contains("/PinballWizard/", StringComparison.Ordinal))
            .ToList();

        Assert.True(repoLinks.Count >= 3,
            $"Expected at least 3 in-repo GitHub links in the strip; found {repoLinks.Count}.");

        foreach (var href in repoLinks)
        {
            var m = RepoLinkRegex.Match(href);
            Assert.True(m.Success, $"Link is not a well-formed github.com/<owner>/PinballWizard URL: {href}");

            Assert.True(m.Groups["owner"].Value == ExpectedOwner,
                $"Link uses the wrong repo owner '{m.Groups["owner"].Value}' (expected '{ExpectedOwner}'): {href}");

            var kind = m.Groups["kind"].Value;
            var relPath = m.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar);
            var onDisk = Path.Combine(repoRoot, relPath);

            if (kind == "blob")
            {
                Assert.True(File.Exists(onDisk),
                    $"'blob' link points at a file that does not exist on disk: {href} (resolved: {onDisk}). " +
                    "Likely a stale or renamed filename.");
            }
            else // tree
            {
                Assert.True(Directory.Exists(onDisk),
                    $"'tree' link points at a directory that does not exist on disk: {href} (resolved: {onDisk}).");
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Strip renders without exception (smoke)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureStoryStrip_Renders_WithoutException()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ArchitectureStoryStrip>();

        // data-testid on the container confirms the component mounted.
        cut.Find("[data-testid='architecture-story-strip']");
    }

    // Walk up from the test assembly to the repo root (the dir holding the
    // slnx). Same convention as PreRenderedDiagramTests.RepoRoot().
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
