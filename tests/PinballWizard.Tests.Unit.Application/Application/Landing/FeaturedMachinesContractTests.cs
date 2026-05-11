using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Application.Landing;

// Pins the on-disk data/seeds/featured_machines.v1.json contract:
//   - schema_version = 1
//   - Exactly 6 machines
//   - No duplicate slugs
//   - opdb_id either null or matching GRBN-[A-Z0-9]+ format
//
// This is the silent-edit guard: any addition, removal, duplicate slug, or
// fabricated OPDB ID that violates the pinned invariants fails here before
// it can reach the landing endpoint in production.
//
// Mirrors SeedQuestionsContractTests (same pattern, different manifest).
public sealed class FeaturedMachinesContractTests
{
    // Cached per CA1869 — reuse across all test methods.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void ProductionManifest_SchemaVersionIsOne()
    {
        var manifest = LoadManifest();

        Assert.Equal(1, manifest.SchemaVersion);
    }

    [Fact]
    public void ProductionManifest_HasExactlySixMachines()
    {
        var manifest = LoadManifest();

        Assert.NotNull(manifest.FeaturedMachines);
        Assert.Equal(6, manifest.FeaturedMachines!.Count);
    }

    [Fact]
    public void ProductionManifest_NoduplicateSlugs()
    {
        var manifest = LoadManifest();

        var slugs = manifest.FeaturedMachines!.Select(m => m.Slug!).ToList();
        var distinct = slugs.Distinct(StringComparer.Ordinal).ToList();
        var duplicates = slugs.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(
            slugs.Count == distinct.Count,
            $"Duplicate slugs found: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void ProductionManifest_AllSlugsAreNonEmpty()
    {
        var manifest = LoadManifest();

        foreach (var m in manifest.FeaturedMachines!)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Slug),
                "Found a machine with a null or whitespace slug.");
        }
    }

    [Fact]
    public void ProductionManifest_AllTitlesAreNonEmpty()
    {
        var manifest = LoadManifest();

        foreach (var m in manifest.FeaturedMachines!)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Title),
                $"Machine with slug '{m.Slug}' has a null or whitespace title.");
        }
    }

    [Fact]
    public void ProductionManifest_AllDisplayOrdersArePositive()
    {
        var manifest = LoadManifest();

        foreach (var m in manifest.FeaturedMachines!)
        {
            Assert.True(m.DisplayOrder > 0,
                $"Machine with slug '{m.Slug}' has display_order={m.DisplayOrder} (must be >= 1).");
        }
    }

    [Fact]
    public void ProductionManifest_OpdbIds_AreNullOrMatchExpectedFormat()
    {
        // Only set opdb_id when the OPDB ID has been verified. Null is the
        // correct value for entries where the ID has not been confirmed.
        // Fabricated IDs are a 🔴 per CLAUDE.md showcase posture.
        var manifest = LoadManifest();

        foreach (var m in manifest.FeaturedMachines!)
        {
            if (m.OpdbId is not null)
            {
                Assert.True(
                    System.Text.RegularExpressions.Regex.IsMatch(m.OpdbId, @"^GRBN-[A-Z0-9]+$"),
                    $"Machine with slug '{m.Slug}' has opdb_id '{m.OpdbId}' that does not match the expected GRBN-[A-Z0-9]+ format.");
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FeaturedMachineManifest LoadManifest()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "data", "seeds", "featured_machines.v1.json");
        Assert.True(File.Exists(manifestPath), $"Production manifest missing at '{manifestPath}'.");

        var json = File.ReadAllText(manifestPath);

        var manifest = JsonSerializer.Deserialize<FeaturedMachineManifest>(json, JsonOptions);

        Assert.NotNull(manifest);
        return manifest!;
    }

    private static string FindRepoRoot()
    {
        // Walk upward from the test assembly until we find the .slnx file.
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

    // JSON DTOs — local test types so contract tests don't depend on
    // internals of FeaturedMachineSeedLoader.
    private sealed class FeaturedMachineManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("featured_machines")]
        public List<FeaturedMachineDto>? FeaturedMachines { get; set; }
    }

    private sealed class FeaturedMachineDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("opdb_id")]
        public string? OpdbId { get; set; }

        [JsonPropertyName("display_order")]
        public int DisplayOrder { get; set; }

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }
    }
}
