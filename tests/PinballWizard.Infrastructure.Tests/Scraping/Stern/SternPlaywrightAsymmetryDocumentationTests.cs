using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

// Pins the Stern Playwright asymmetry as documented (route ii of Phase 2
// § Scope item 8). The family-wide scraper-pipeline integration test
// infrastructure (FakePolitenessGate + QueueingHttpMessageHandler) wires
// at the typed-HttpClient layer; Stern's two Playwright scrapers
// (GamePageScraper, ServiceBulletinScraper) drive a real browser and
// don't go through HttpClient, so they're not covered by the template.
//
// This test asserts the test-project README still names the asymmetry.
// If the README is deleted or the asymmetry section removed, this test
// fails — forcing a re-read or a real fix (build a Playwright-route
// fixture and add the missing 5-test coverage).
//
// When the asymmetry is genuinely resolved, delete this file alongside
// the README section it pins.
public sealed class SternPlaywrightAsymmetryDocumentationTests
{
    [Fact]
    public void Stern_Playwright_Pipeline_Test_Asymmetry_IsAcknowledged()
    {
        var repoRoot = FindRepoRoot();
        var readmePath = Path.Combine(repoRoot, "tests", "PinballWizard.Infrastructure.Tests", "README.md");

        Assert.True(
            File.Exists(readmePath),
            $"Test-project README missing at {readmePath}. " +
            "The README documents the Stern Playwright asymmetry per " +
            "docs/build-spec.md Phase 2 § Scope item 8 (route ii). " +
            "Restore the README or — if the asymmetry has been genuinely " +
            "resolved by adding Playwright-route test infrastructure — " +
            "delete this pinning test alongside the asymmetry section.");

        var content = File.ReadAllText(readmePath);

        // The phrases below are the load-bearing markers of the asymmetry
        // documentation. If a future edit removes any of them, the
        // documentation has degraded enough that this test should fail
        // and force a re-read.
        Assert.Contains("Stern Playwright asymmetry", content, StringComparison.Ordinal);
        Assert.Contains("HttpClient", content, StringComparison.Ordinal);
        Assert.Contains("GamePageScraper", content, StringComparison.Ordinal);
        Assert.Contains("ServiceBulletinScraper", content, StringComparison.Ordinal);
        Assert.Contains("Revisit criteria", content, StringComparison.Ordinal);
        // Anchor at a phrase that uniquely appears inside the asymmetry
        // justification (not in the family-wide-infra section). Catches
        // the false-pass case where the asymmetry section is gutted but
        // the generic markers above survive elsewhere in the README.
        Assert.Contains("deliberately not covered", content, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        // Walk upward from the test assembly until we find the .slnx file.
        // Mirrors the helper in IngestionSourceSeederTests; if a third
        // consumer appears, extract to Scraping/_TestInfra/RepoPaths.cs.
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
