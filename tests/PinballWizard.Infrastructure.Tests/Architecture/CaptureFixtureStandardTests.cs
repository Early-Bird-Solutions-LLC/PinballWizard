using Xunit;

namespace PinballWizard.Infrastructure.Tests.Architecture;

// Enforces TEST-05 (captured-scraper-fixtures): every Fixtures/{source}/
// directory that houses scraped-source test fixtures must contain a CAPTURE.md
// recording the source URL and capture date.
//
// Why: PR #752 shipped a slug-derivation rule built on invented URL patterns
// that do not exist on the live site.  Its unit tests asserted against the
// same invented shapes, so every gate went green, and only a live probe caught
// it.  Requiring a CAPTURE.md makes the fabrication unrepresentable: you cannot
// add a fixture dir without proving it came from the live source.
//
// See .claude/standards/testing/STANDARD.md (TEST-05) and
// docs/learning-from-failure.md (#758) for the full account.
public sealed class CaptureFixtureStandardTests
{
    [Fact]
    public void EveryFixtureSourceDir_HasCaptureMd()
    {
        var testsDir = Path.Combine(RepoRoot(), "tests");

        // Find every Fixtures/ container, then enumerate one level below to
        // get the per-source directories (e.g. Fixtures/Ap/).
        var fixtureContainers = Directory.GetDirectories(
            testsDir, "Fixtures", SearchOption.AllDirectories);

        var sourceDirs = fixtureContainers
            .SelectMany(Directory.GetDirectories)
            .ToList();

        Assert.NotEmpty(sourceDirs);

        var missing = sourceDirs
            .Where(dir => !File.Exists(Path.Combine(dir, "CAPTURE.md")))
            .Select(dir => Path.GetRelativePath(testsDir, dir))
            .OrderBy(rel => rel)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Fixture source dir(s) missing CAPTURE.md (TEST-05): " +
            string.Join(", ", missing) +
            ". Add CAPTURE.md recording the source URL and capture date — " +
            "see .claude/standards/testing/STANDARD.md TEST-05.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
    }
}
