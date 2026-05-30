using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Ai.Refusal;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Refusal;

// Exercises CommunityResourceLoader in isolation using temp-file fixtures
// so the test project does not depend on the on-disk
// community_resources.v1.json. The production manifest contract is pinned
// separately in CommunityResourcesContractTests — this file covers the
// loader logic only.
public sealed class CommunityResourceLoaderTests : IDisposable
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
    public async Task LoadAsync_Returns_Seeded_Resources()
    {
        // A valid manifest with the minimum required plurality set should load
        // successfully and return all resources.
        var path = WriteManifest(1,
            R("marketplace", "Alpha Market", "https://alphamarket.example.com", "Alpha desc"),
            R("marketplace", "Beta Market", "https://betamarket.example.com", "Beta desc"),
            R("marketplace", "Gamma Market", "https://gammamarket.example.com", "Gamma desc"),
            R("machine_reference", "IPDB", "https://www.ipdb.example.com", "IPDB desc"),
            R("machine_reference", "OPDB", "https://opdb.example.com", "OPDB desc"));

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(5, result.Count);
        Assert.Contains(result, r => r.Name == "Alpha Market");
        Assert.Contains(result, r => r.Name == "IPDB");
    }

    // ── Validation: missing name ─────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_Rejects_Missing_Name()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "resources": [
                { "category": "marketplace", "name": "", "url": "https://example.com", "description": "desc" },
                { "category": "marketplace", "name": "Beta", "url": "https://beta.example.com", "description": "desc" },
                { "category": "marketplace", "name": "Gamma", "url": "https://gamma.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "IPDB", "url": "https://ipdb.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "OPDB", "url": "https://opdb.example.com", "description": "desc" }
              ]
            }
            """);

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation: missing URL ──────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_Rejects_Missing_Url()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "resources": [
                { "category": "marketplace", "name": "Alpha", "url": "", "description": "desc" },
                { "category": "marketplace", "name": "Beta", "url": "https://beta.example.com", "description": "desc" },
                { "category": "marketplace", "name": "Gamma", "url": "https://gamma.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "IPDB", "url": "https://ipdb.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "OPDB", "url": "https://opdb.example.com", "description": "desc" }
              ]
            }
            """);

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("url", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation: malformed URL ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_Rejects_Malformed_Url()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "resources": [
                { "category": "marketplace", "name": "Alpha", "url": "not-a-url", "description": "desc" },
                { "category": "marketplace", "name": "Beta", "url": "https://beta.example.com", "description": "desc" },
                { "category": "marketplace", "name": "Gamma", "url": "https://gamma.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "IPDB", "url": "https://ipdb.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "OPDB", "url": "https://opdb.example.com", "description": "desc" }
              ]
            }
            """);

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Alpha", ex.Message, StringComparison.Ordinal);
    }

    // ── Validation: unknown category ────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_Rejects_Unknown_Category()
    {
        var path = WriteRaw("""
            {
              "schema_version": 1,
              "resources": [
                { "category": "not_a_real_category", "name": "Alpha", "url": "https://alpha.example.com", "description": "desc" },
                { "category": "marketplace", "name": "Beta", "url": "https://beta.example.com", "description": "desc" },
                { "category": "marketplace", "name": "Gamma", "url": "https://gamma.example.com", "description": "desc" },
                { "category": "marketplace", "name": "Delta", "url": "https://delta.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "IPDB", "url": "https://ipdb.example.com", "description": "desc" },
                { "category": "machine_reference", "name": "OPDB", "url": "https://opdb.example.com", "description": "desc" }
              ]
            }
            """);

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("not_a_real_category", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown category", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation: marketplace below 3 ─────────────────────────────────────

    [Fact]
    public async Task LoadAsync_Rejects_Marketplace_Below_3()
    {
        // Plurality minimum for marketplace is 3. Providing only 2 entries
        // must throw at load time — silent edits that break plurality are
        // caught here, not at refusal time.
        var path = WriteManifest(1,
            R("marketplace", "Alpha", "https://alpha.example.com", "desc"),
            R("marketplace", "Beta", "https://beta.example.com", "desc"),
            R("machine_reference", "IPDB", "https://ipdb.example.com", "desc"),
            R("machine_reference", "OPDB", "https://opdb.example.com", "desc"));

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("marketplace", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plurality", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validation: machine_reference below 2 ───────────────────────────────

    [Fact]
    public async Task LoadAsync_Rejects_Machine_Reference_Below_2()
    {
        // Plurality minimum for machine_reference is 2. Providing only 1 entry
        // must throw at load time.
        var path = WriteManifest(1,
            R("marketplace", "Alpha", "https://alpha.example.com", "desc"),
            R("marketplace", "Beta", "https://beta.example.com", "desc"),
            R("marketplace", "Gamma", "https://gamma.example.com", "desc"),
            R("machine_reference", "IPDB", "https://ipdb.example.com", "desc"));

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("machine_reference", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plurality", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Alphabetical ordering within category ────────────────────────────────

    [Fact]
    public async Task LoadAsync_Alphabetizes_Within_Category()
    {
        // Resources within a category must be returned in alphabetical order
        // by name regardless of their order in the JSON file (no favoritism
        // via insertion order).
        var path = WriteManifest(1,
            R("marketplace", "Zeta Market", "https://zeta.example.com", "desc"),
            R("marketplace", "Alpha Market", "https://alpha.example.com", "desc"),
            R("marketplace", "Beta Market", "https://beta.example.com", "desc"),
            R("machine_reference", "OPDB", "https://opdb.example.com", "desc"),
            R("machine_reference", "IPDB", "https://ipdb.example.com", "desc"));

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        var marketplaceNames = result
            .Where(r => r.Category == "marketplace")
            .Select(r => r.Name)
            .ToList();

        Assert.Equal(["Alpha Market", "Beta Market", "Zeta Market"], marketplaceNames);

        var machineRefNames = result
            .Where(r => r.Category == "machine_reference")
            .Select(r => r.Name)
            .ToList();

        Assert.Equal(["IPDB", "OPDB"], machineRefNames);
    }

    // ── LoadByCategoryAsync filters correctly ────────────────────────────────

    [Fact]
    public async Task LoadByCategoryAsync_Returns_Only_Matching_Category()
    {
        var path = WriteManifest(1,
            R("marketplace", "Alpha Market", "https://alpha.example.com", "desc"),
            R("marketplace", "Beta Market", "https://beta.example.com", "desc"),
            R("marketplace", "Gamma Market", "https://gamma.example.com", "desc"),
            R("machine_reference", "IPDB", "https://ipdb.example.com", "desc"),
            R("machine_reference", "OPDB", "https://opdb.example.com", "desc"),
            R("forums", "Pinside", "https://pinside.example.com", "desc"),
            R("forums", "Tilt Forums", "https://tiltforums.example.com", "desc"),
            R("forums", "AAP Forum", "https://aap.example.com", "desc"));

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);
        var marketplaceResources = await loader.LoadByCategoryAsync(
            CommunityResourceCategory.Marketplace,
            CancellationToken.None);

        Assert.Equal(3, marketplaceResources.Count);
        Assert.All(marketplaceResources, r =>
            Assert.Equal("marketplace", r.Category, StringComparer.OrdinalIgnoreCase));
    }

    // ── Caching: second call returns cached result ───────────────────────────

    [Fact]
    public async Task LoadAsync_SecondCall_ReturnsCachedResult_WithoutRereadingFile()
    {
        var path = WriteManifest(1,
            R("marketplace", "Alpha", "https://alpha.example.com", "desc"),
            R("marketplace", "Beta", "https://beta.example.com", "desc"),
            R("marketplace", "Gamma", "https://gamma.example.com", "desc"),
            R("machine_reference", "IPDB", "https://ipdb.example.com", "desc"),
            R("machine_reference", "OPDB", "https://opdb.example.com", "desc"));

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var first = await loader.LoadAsync(CancellationToken.None);

        // Delete the file to prove the second call uses the cache.
        File.Delete(path);
        _tempFiles.Remove(path);

        var second = await loader.LoadAsync(CancellationToken.None);

        Assert.Same(first, second);
    }

    // ── File not found ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_FileNotFound_Throws_FileNotFoundException()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var loader = new CommunityResourceLoader(nonexistent, NullLogger<CommunityResourceLoader>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync(CancellationToken.None));
    }

    // ── Malformed JSON ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_MalformedJson_Throws_InvalidOperationException()
    {
        var path = WriteRaw("{ this is not valid json");

        var loader = new CommunityResourceLoader(path, NullLogger<CommunityResourceLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Constructor null-checks ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CommunityResourceLoader("path.json", null!));
    }

    [Fact]
    public void Constructor_WhitespaceManifestPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CommunityResourceLoader("   ", NullLogger<CommunityResourceLoader>.Instance));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static object R(string category, string name, string url, string description) =>
        new { category, name, url, description };

    private string WriteManifest(int schemaVersion, params object[] resources)
    {
        var doc = new { schema_version = schemaVersion, resources };
        return WriteRaw(JsonSerializer.Serialize(doc));
    }

    private string WriteRaw(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"community-resources-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }
}
