using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Landing;
using Xunit;

namespace PinballWizard.Scraper.Tests.Application.Landing;

// Exercises SeedQuestionLoader in isolation using temp-file fixtures so
// the test project does not depend on the on-disk wizard_seed_questions.v1.json.
// The production manifest contract is pinned separately in
// SeedQuestionsContractTests — this file covers the loader logic only.
public sealed class SeedQuestionLoaderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ValidManifest_ReturnsAllQuestions()
    {
        var path = WriteManifest(1,
            Q("rules-slug", "A rules question?", "Rules", "A desc"),
            Q("valuation-slug", "A valuation question?", "Valuation", "A desc"));

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("rules-slug", result[0].Slug);
        Assert.Equal("Rules", result[0].TargetSubAgent);
        Assert.Equal("valuation-slug", result[1].Slug);
        Assert.Equal("Valuation", result[1].TargetSubAgent);
    }

    [Fact]
    public async Task LoadAsync_AllFourSubAgents_LoadsAllSuccessfully()
    {
        var path = WriteManifest(1,
            Q("wizard-slug", "Q?", "Wizard", "desc"),
            Q("valuation-slug", "Q?", "Valuation", "desc"),
            Q("rules-slug", "Q?", "Rules", "desc"),
            Q("repair-slug", "Q?", "Repair", "desc"));

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Contains(result, q => q.TargetSubAgent == "Wizard");
        Assert.Contains(result, q => q.TargetSubAgent == "Valuation");
        Assert.Contains(result, q => q.TargetSubAgent == "Rules");
        Assert.Contains(result, q => q.TargetSubAgent == "Repair");
    }

    // ── Validation — unknown sub-agent ───────────────────────────────────────

    [Fact]
    public async Task LoadAsync_UnknownTargetSubAgent_ThrowsInvalidOperationException()
    {
        var path = WriteManifest(1,
            Q("slug-ok", "Q?", "Rules", "desc"),
            Q("slug-bad", "Q?", "UnknownAgent", "desc"));

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("UnknownAgent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("slug-bad", ex.Message, StringComparison.Ordinal);
    }

    // ── Validation — malformed JSON ──────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("{ this is not valid json");

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation — missing file ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var loader = new SeedQuestionLoader(nonexistent, NullLogger<SeedQuestionLoader>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync(CancellationToken.None));
    }

    // ── Validation — null / empty fields ────────────────────────────────────

    [Fact]
    public async Task LoadAsync_BlankSlug_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "questions": [
                { "slug": "", "question": "Q?", "target_sub_agent": "Rules", "description": "desc" }
              ]
            }
            """);

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("slug", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_BlankTargetSubAgent_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "questions": [
                { "slug": "s", "question": "Q?", "target_sub_agent": "", "description": "d" }
              ]
            }
            """);

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("target_sub_agent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Empty manifest ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_EmptyQuestionsList_ReturnsEmptyList()
    {
        var path = WriteRaw("""{"schema_version":1,"questions":[]}""");

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var path = WriteManifest(1,
            Q("s1", "Q?", "Rules", "d"),
            Q("s2", "Q?", "Repair", "d"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var loader = new SeedQuestionLoader(path, NullLogger<SeedQuestionLoader>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(cts.Token));
    }

    // ── Constructor null-checks ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SeedQuestionLoader("path.json", null!));
    }

    [Fact]
    public void Constructor_WhitespaceManifestPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SeedQuestionLoader("   ", NullLogger<SeedQuestionLoader>.Instance));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object Q(string slug, string question, string targetSubAgent, string description) =>
        new { slug, question, target_sub_agent = targetSubAgent, description };

    private string WriteManifest(int schemaVersion, params object[] questions)
    {
        var doc = new { schema_version = schemaVersion, questions };
        return WriteRaw(JsonSerializer.Serialize(doc));
    }

    private string WriteRaw(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seed-questions-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }
}
