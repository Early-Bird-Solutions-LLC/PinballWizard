using PinballWizard.Application.SeedData;
using Xunit;

namespace PinballWizard.Application.Tests.Application.SeedData;

// Tests for SeedPathResolver — the path-resolution helper that makes seed
// files findable regardless of working directory.
//
// Root cause context: before commit 8b7681a, loaders used raw relative paths
// resolved against the working directory. The local Aspire-launched API sets
// workdir = the project dir (not the repo root), so "data/seeds/*.json"
// resolved to src\PinballWizard.Api\data\seeds\… which does not exist →
// FileNotFoundException every call → community CTAs absent on every local
// refusal. The resolver walks up from AppContext.BaseDirectory (the approach
// contract tests already used) to find the file.
//
// These tests confirm that the resolver:
//   1. Finds all three bundled seed files via the walk-up strategy (proving
//      local Aspire dev and in-process test assembly both work).
//   2. Returns the ORIGINAL relative path unchanged when the file does not
//      exist (so callers get the expected FileNotFoundException with a
//      clear message, not a resolved-but-wrong path — fail-visible, per
//      invariant #17 / OBS-01).
//   3. Returns an absolute path as-is (guard against double-resolution).
public sealed class SeedPathResolverTests
{
    // ── Production manifests are resolvable ─────────────────────────────

    [Theory]
    [InlineData("data/seeds/community_resources.v1.json")]
    [InlineData("data/seeds/wizard_seed_questions.v1.json")]
    [InlineData("data/seeds/featured_machines.v1.json")]
    public void Resolve_ProductionSeedFile_ReturnsExistingPath(string relativePath)
    {
        // Act
        var resolved = SeedPathResolver.Resolve(relativePath);

        // Assert: the returned path must point to an existing file — the
        // resolver's contract is "return a path that File.Exists() on, or
        // return the original as a sentinel for the caller's FileNotFoundException."
        Assert.True(
            File.Exists(resolved),
            $"SeedPathResolver.Resolve(\"{relativePath}\") returned \"{resolved}\" which does not exist. " +
            "Either the seed file was removed from data/seeds/, or the walk-up strategy failed to reach the repo root.");
    }

    [Theory]
    [InlineData("data/seeds/community_resources.v1.json")]
    [InlineData("data/seeds/wizard_seed_questions.v1.json")]
    [InlineData("data/seeds/featured_machines.v1.json")]
    public void Resolve_ProductionSeedFile_ReturnsAbsolutePath(string relativePath)
    {
        // The resolver must return an absolute path so callers never
        // accidentally re-resolve it against a different working directory.
        var resolved = SeedPathResolver.Resolve(relativePath);

        Assert.True(
            Path.IsPathRooted(resolved),
            $"SeedPathResolver.Resolve(\"{relativePath}\") returned \"{resolved}\" which is not an absolute path. " +
            "Callers depend on absolute paths so File.ReadAllTextAsync does not re-resolve against workdir.");
    }

    // ── Missing file returns the ORIGINAL path (caller gets FileNotFoundException) ──

    [Fact]
    public void Resolve_NonExistentFile_ReturnsOriginalRelativePath()
    {
        // When no ancestor directory contains the file, the resolver must
        // return the original relative path — so the caller's FileNotFoundException
        // message is "file not found at 'data/seeds/nonexistent.json'" rather
        // than a confusingly long rooted path to the wrong place.
        // This is the fail-visible contract: the resolver does NOT fabricate a
        // "resolved" path to a nonexistent location (invariant #17 / OBS-01).
        const string missing = "data/seeds/nonexistent_seed_file_for_test_only.json";

        var resolved = SeedPathResolver.Resolve(missing);

        // The resolver should return the original when nothing was found.
        Assert.Equal(missing, resolved);
    }

    // ── Absolute path short-circuits the walk-up ────────────────────────

    [Fact]
    public void Resolve_AbsolutePath_ReturnsAbsolutePathUnchanged()
    {
        // An already-absolute path (e.g. injected by tests via the internal
        // constructor) must be returned as-is — no walk-up, no re-resolution.
        var absolute = Path.GetTempFileName();
        try
        {
            var resolved = SeedPathResolver.Resolve(absolute);
            Assert.Equal(absolute, resolved);
        }
        finally
        {
            File.Delete(absolute);
        }
    }

    // ── Blank / null argument validation ────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WhitespaceRelativePath_ThrowsArgumentException(string blank)
    {
        Assert.Throws<ArgumentException>(() => SeedPathResolver.Resolve(blank));
    }
}
