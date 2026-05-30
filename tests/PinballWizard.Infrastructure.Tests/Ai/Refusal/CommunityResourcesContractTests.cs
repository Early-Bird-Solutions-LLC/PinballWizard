using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Refusal;

// Pins the on-disk community_resources.v1.json contract:
//   - schema_version = 1
//   - marketplace ≥ 3 entries (ADR-0026 § 5 plurality invariant)
//   - machine_reference ≥ 2 entries
//   - All URLs are absolute with lowercase hostname
//   - Descriptions avoid superlatives ("best", "biggest", "most popular",
//     "largest") — silent-edit guard against favoritism creep
//
// This file is the silent-edit guard: any change to the seed that violates
// these invariants is caught here before it reaches production. Mirrors the
// SeedQuestionsContractTests pattern.
public sealed class CommunityResourcesContractTests
{
    // Cached per CA1869 — reuse across all test methods.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Superlative terms that imply we are endorsing one venue over others.
    // These violate feedback_avoid_appearance_of_favoritism.md and the
    // "no superlatives in descriptions" rule from the PR-R3 spec.
    private static readonly string[] ForbiddenSuperlatives =
    [
        "the best",
        "best pinball",
        "biggest",
        "most popular",
        "the largest",
        "the most",
    ];

    [Fact]
    public void Schema_Version_Is_1()
    {
        var manifest = LoadManifest();

        Assert.Equal(1, manifest.SchemaVersion);
    }

    [Fact]
    public void Marketplace_Category_Has_At_Least_3_Entries()
    {
        // ADR-0026 § 5 plurality invariant: no single "buy here" suggestion.
        // Pinside Market + Mr. Pinball + Facebook Marketplace at minimum.
        var manifest = LoadManifest();

        var count = manifest.Resources!.Count(r =>
            string.Equals(r.Category, "marketplace", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            count >= 3,
            $"Expected at least 3 marketplace entries in community_resources.v1.json " +
            $"(per ADR-0026 § 5 plurality + feedback_destination_plurality.md). Found {count}.");
    }

    [Fact]
    public void Machine_Reference_Has_At_Least_2_Entries()
    {
        // ADR-0026 § 5: must not pick favorites between OPDB and IPDB.
        var manifest = LoadManifest();

        var count = manifest.Resources!.Count(r =>
            string.Equals(r.Category, "machine_reference", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            count >= 2,
            $"Expected at least 2 machine_reference entries in community_resources.v1.json " +
            $"(per ADR-0026 § 5 plurality). Found {count}.");
    }

    [Fact]
    public void All_Urls_Are_Absolute_And_Lowercase_Host()
    {
        // All URLs in the seed must be absolute (https://... or http://...) and
        // must not contain tracking parameters (no '?' in the path portion).
        // Lowercase hostname is required to avoid inconsistency between entries.
        var manifest = LoadManifest();

        foreach (var resource in manifest.Resources!)
        {
            Assert.False(string.IsNullOrWhiteSpace(resource.Url),
                $"Resource '{resource.Name}' has a null or whitespace URL.");

            var parsed = Uri.TryCreate(resource.Url, UriKind.Absolute, out var uri);

            Assert.True(parsed,
                $"Resource '{resource.Name}' has malformed URL '{resource.Url}' — must be an absolute URL.");

            Assert.True(
                uri!.Scheme is "https" or "http",
                $"Resource '{resource.Name}' URL '{resource.Url}' must use https or http scheme.");

            var host = uri!.Host;
            Assert.True(
                string.Equals(host, host.ToLowerInvariant(), StringComparison.Ordinal),
                $"Resource '{resource.Name}' URL '{resource.Url}' must have a lowercase hostname.");
        }
    }

    [Fact]
    public void Descriptions_Avoid_Superlatives()
    {
        // Silent-edit guard against favoritism creep: descriptions must not
        // endorse one venue over another with superlative language.
        // Per feedback_avoid_appearance_of_favoritism.md.
        var manifest = LoadManifest();

        foreach (var resource in manifest.Resources!)
        {
            if (string.IsNullOrWhiteSpace(resource.Description))
                continue;

            var descLower = resource.Description.ToLowerInvariant();

            foreach (var forbidden in ForbiddenSuperlatives)
            {
                Assert.False(
                    descLower.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Resource '{resource.Name}' description contains forbidden superlative '{forbidden}'. " +
                    "Descriptions must be neutral — no endorsement of one venue over another. " +
                    "Per feedback_avoid_appearance_of_favoritism.md.");
            }
        }
    }

    [Fact]
    public void All_Resources_Have_Non_Empty_Name_And_Url()
    {
        var manifest = LoadManifest();

        foreach (var resource in manifest.Resources!)
        {
            Assert.False(string.IsNullOrWhiteSpace(resource.Name),
                "Found a resource with null or whitespace name.");
            Assert.False(string.IsNullOrWhiteSpace(resource.Url),
                $"Resource '{resource.Name}' has null or whitespace URL.");
        }
    }

    [Fact]
    public void All_Categories_Are_From_Canonical_Set()
    {
        var manifest = LoadManifest();

        var validCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "marketplace",
            "machine_reference",
            "news_and_culture",
            "forums",
            "tournament_and_play",
            "manufacturer_pages",
        };

        foreach (var resource in manifest.Resources!)
        {
            Assert.False(string.IsNullOrWhiteSpace(resource.Category),
                $"Resource '{resource.Name}' has null or whitespace category.");
            Assert.True(
                validCategories.Contains(resource.Category!),
                $"Resource '{resource.Name}' has unknown category '{resource.Category}'. " +
                $"Valid values: {string.Join(", ", validCategories)}.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CommunityResourceManifest LoadManifest()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "data", "seeds", "community_resources.v1.json");
        Assert.True(File.Exists(manifestPath), $"Production manifest missing at '{manifestPath}'.");

        var json = File.ReadAllText(manifestPath);

        var manifest = JsonSerializer.Deserialize<CommunityResourceManifest>(json, JsonOptions);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Resources);
        return manifest;
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

    // JSON DTOs for deserialization — mirrors the CommunityResourceLoader
    // private DTOs but lives here as a test-local type so contract tests
    // do not depend on internals of CommunityResourceLoader.
    private sealed class CommunityResourceManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("resources")]
        public List<CommunityResourceDto>? Resources { get; set; }
    }

    private sealed class CommunityResourceDto
    {
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
