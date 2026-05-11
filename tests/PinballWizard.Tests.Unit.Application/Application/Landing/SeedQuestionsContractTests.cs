using System.Text.Json;
using System.Text.Json.Serialization;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Landing;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Application.Landing;

// Pins the on-disk wizard_seed_questions.v1.json contract:
//   - schema_version = 1
//   - Exactly 4 questions
//   - One question per sub-agent path (Wizard / Valuation / Rules / Repair)
//
// This is the silent-edit guard: any addition, rename, or removal that
// shifts the 4-question invariant or introduces an unsupported sub-agent
// fails here before it can reach the landing endpoint in production.
//
// Mirrors the ProductionManifest_DeserializesCleanlyAndContainsExpectedEntries
// test in IngestionSourceSeederTests.
public sealed class SeedQuestionsContractTests
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
    public void ProductionManifest_HasExactlyFourQuestions()
    {
        var manifest = LoadManifest();

        Assert.NotNull(manifest.Questions);
        Assert.Equal(4, manifest.Questions!.Count);
    }

    [Fact]
    public void ProductionManifest_HasExactlyOneQuestionPerSubAgent()
    {
        var manifest = LoadManifest();

        var subAgents = manifest.Questions!
            .Select(q => q.TargetSubAgent)
            .OrderBy(s => s)
            .ToList();

        var expected = AgentName.All
            .OrderBy(s => s)
            .ToList();

        Assert.Equal(expected, subAgents, StringComparer.Ordinal);
    }

    [Fact]
    public void ProductionManifest_AllSlugsAreUniqueUrlFriendlyLowercase()
    {
        var manifest = LoadManifest();

        var slugs = manifest.Questions!.Select(q => q.Slug!).ToList();

        // All slugs must be unique
        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.Ordinal).Count());

        // All slugs must be URL-friendly: lowercase letters, digits, hyphens only
        foreach (var slug in slugs)
        {
            Assert.False(string.IsNullOrWhiteSpace(slug),
                $"Slug '{slug}' must not be null or whitespace.");
            Assert.True(
                slug.Equals(slug.ToLowerInvariant(), StringComparison.Ordinal),
                $"Slug '{slug}' must be lowercase.");
            Assert.Matches(
                @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$",
                slug);
        }
    }

    [Fact]
    public void ProductionManifest_AllQuestionsHaveNonEmptyText()
    {
        var manifest = LoadManifest();

        foreach (var q in manifest.Questions!)
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Question),
                $"Slug '{q.Slug}' has null or whitespace question text.");
        }
    }

    [Fact]
    public void ProductionManifest_AllTargetSubAgentsAreValid()
    {
        var manifest = LoadManifest();

        foreach (var q in manifest.Questions!)
        {
            Assert.Contains(
                q.TargetSubAgent,
                AgentName.All,
                StringComparer.Ordinal);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SeedQuestionManifest LoadManifest()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "data", "seeds", "wizard_seed_questions.v1.json");
        Assert.True(File.Exists(manifestPath), $"Production manifest missing at '{manifestPath}'.");

        var json = File.ReadAllText(manifestPath);

        var manifest = JsonSerializer.Deserialize<SeedQuestionManifest>(json, JsonOptions);

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

    // JSON DTO for deserialization — mirrors the SeedQuestionLoader private DTO
    // but lives here as a test-local type so contract tests don't depend on
    // internals of SeedQuestionLoader.
    private sealed class SeedQuestionManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("questions")]
        public List<SeedQuestionDto>? Questions { get; set; }
    }

    private sealed class SeedQuestionDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("question")]
        public string? Question { get; set; }

        [JsonPropertyName("target_sub_agent")]
        public string? TargetSubAgent { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
