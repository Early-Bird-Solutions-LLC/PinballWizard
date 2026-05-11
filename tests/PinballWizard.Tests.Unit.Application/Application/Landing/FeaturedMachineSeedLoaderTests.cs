using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Landing;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Application.Landing;

// Exercises FeaturedMachineSeedLoader in isolation using temp-file fixtures
// so the test project does not depend on the on-disk featured_machines.v1.json.
// The production manifest contract is pinned separately in
// FeaturedMachinesContractTests — this file covers the loader logic only.
public sealed class FeaturedMachineSeedLoaderTests : IDisposable
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
    public async Task LoadAsync_ValidManifest_ReturnsAllDocuments()
    {
        var path = WriteManifest(1,
            M("stern-godzilla", "Godzilla Pro", null, 1, "King of the monsters"),
            M("jjp-wonka", "Wonka", null, 2, "Pure imagination"));

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("stern-godzilla", result[0].Id);
        Assert.Equal("stern-godzilla", result[0].PartitionKey);
        Assert.Equal("Godzilla Pro", result[0].Title);
        Assert.Null(result[0].OpdbId);
        Assert.Equal(1, result[0].DisplayOrder);
        Assert.Equal("King of the monsters", result[0].Tagline);

        Assert.Equal("jjp-wonka", result[1].Id);
        Assert.Equal(2, result[1].DisplayOrder);
    }

    [Fact]
    public async Task LoadAsync_WithOpdbId_MapsOpdbIdToDocument()
    {
        var path = WriteManifest(1,
            M("stern-foo", "Foo Machine", "GRBN-XXXXX", 1, "Tagline here"));

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("GRBN-XXXXX", result[0].OpdbId);
    }

    [Fact]
    public async Task LoadAsync_NullOpdbId_MapsToNull()
    {
        var path = WriteManifest(1,
            M("stern-bar", "Bar Machine", null, 1, "A tagline"));

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Null(result[0].OpdbId);
    }

    // ── Validation — malformed JSON ──────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("{ this is not valid json");

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation — missing file ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"missing-featured-{Guid.NewGuid():N}.json");

        var loader = new FeaturedMachineSeedLoader(nonexistent, NullLogger<FeaturedMachineSeedLoader>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync(CancellationToken.None));
    }

    // ── Validation — missing slug ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_BlankSlug_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "featured_machines": [
                { "slug": "", "title": "Some Machine", "opdb_id": null, "display_order": 1, "tagline": "T" }
              ]
            }
            """);

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("slug", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation — missing title ───────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_BlankTitle_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "featured_machines": [
                { "slug": "my-slug", "title": "", "opdb_id": null, "display_order": 1, "tagline": "T" }
              ]
            }
            """);

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("title", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("my-slug", ex.Message, StringComparison.Ordinal);
    }

    // ── Validation — non-positive display_order ──────────────────────────────

    [Fact]
    public async Task LoadAsync_ZeroDisplayOrder_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "featured_machines": [
                { "slug": "my-slug", "title": "T", "opdb_id": null, "display_order": 0, "tagline": "T" }
              ]
            }
            """);

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("display_order", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("my-slug", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_NegativeDisplayOrder_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "featured_machines": [
                { "slug": "my-slug", "title": "T", "opdb_id": null, "display_order": -1, "tagline": "T" }
              ]
            }
            """);

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("display_order", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation — duplicate slugs ─────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_DuplicateSlugs_ThrowsInvalidOperationException()
    {
        // Duplicate slugs would collapse into a single Cosmos document on
        // upsert — the validator must catch this before any write is attempted.
        var path = WriteManifest(1,
            M("same-slug", "Machine A", null, 1, "Tagline A"),
            M("same-slug", "Machine B", null, 2, "Tagline B"));

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("same-slug", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Empty manifest ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_EmptyList_ReturnsEmptyList()
    {
        var path = WriteRaw("""{"schema_version":1,"featured_machines":[]}""");

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var path = WriteManifest(1,
            M("slug-a", "Machine A", null, 1, "T"),
            M("slug-b", "Machine B", null, 2, "T"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(cts.Token));
    }

    // ── Constructor null-checks ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new FeaturedMachineSeedLoader("path.json", null!));
    }

    [Fact]
    public void Constructor_WhitespaceManifestPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new FeaturedMachineSeedLoader("   ", NullLogger<FeaturedMachineSeedLoader>.Instance));
    }

    // ── Document mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_IdEqualsPartitionKey_EqualsSlug()
    {
        // ADR-0025 § 4 pattern: id == partition-key value == slug so reads
        // are pure point-lookups with two equal arguments.
        var path = WriteManifest(1, M("cool-machine", "Cool Machine", null, 1, "T"));

        var loader = new FeaturedMachineSeedLoader(path, NullLogger<FeaturedMachineSeedLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(result[0].Id, result[0].PartitionKey);
        Assert.Equal("cool-machine", result[0].Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object M(string slug, string title, string? opdbId, int displayOrder, string tagline) =>
        new { slug, title, opdb_id = opdbId, display_order = displayOrder, tagline };

    private string WriteManifest(int schemaVersion, params object[] machines)
    {
        var doc = new { schema_version = schemaVersion, featured_machines = machines };
        return WriteRaw(JsonSerializer.Serialize(doc));
    }

    private string WriteRaw(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"featured-machines-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }
}
