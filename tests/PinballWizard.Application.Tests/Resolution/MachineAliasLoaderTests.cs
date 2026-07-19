using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Resolution;
using Xunit;

namespace PinballWizard.Application.Tests.Resolution;

// Exercises MachineAliasLoader in isolation using temp-file fixtures and an
// in-memory fake catalog so no Cosmos call is ever made from tests.
// Validation rules are exercised here against synthetic fixtures. The production
// seed is NOT loaded in these tests — it is pinned separately by
// MachineAliasContractTests (PinballWizard.Infrastructure.Tests/Resolution),
// which reads the real data/seeds/machine_aliases.v1.json off disk.
public sealed class MachineAliasLoaderTests : IDisposable
{
    // ── Fake catalog ─────────────────────────────────────────────────────────

    // Known group IDs and machine IDs in our fake catalog.
    // These mirror the real AP OPDB group IDs provided to the implementer.
    private static readonly string[] KnownGroupIds =
    [
        "Gj6PZ",  // Galactic Tank Force
        "GLWBV",  // Oktoberfest
        "GEL31",  // Hot Wheels
        "GWyyq",  // Legends of Valhalla
    ];

    private sealed class FakeCatalog : IMachineAliasCatalog
    {
        private readonly HashSet<string> _groupIds;
        private readonly HashSet<string> _machineIds;

        public FakeCatalog(IEnumerable<string>? groupIds = null, IEnumerable<string>? machineIds = null)
        {
            _groupIds = new HashSet<string>(groupIds ?? [], StringComparer.OrdinalIgnoreCase);
            _machineIds = new HashSet<string>(machineIds ?? [], StringComparer.OrdinalIgnoreCase);
        }

        public Task<bool> GroupExistsAsync(string groupId, string manufacturerKey, CancellationToken _)
            => Task.FromResult(_groupIds.Contains(groupId));

        public Task<bool> MachineExistsAsync(string machineId, string manufacturerKey, CancellationToken _)
            => Task.FromResult(_machineIds.Contains(machineId));
    }

    // ── Temp file helpers ────────────────────────────────────────────────────

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    private string WriteSeed(int version, params object[] aliases)
    {
        var doc = new { version, aliases };
        return WriteRaw(JsonSerializer.Serialize(doc));
    }

    private string WriteRaw(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"machine-aliases-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    private static object Alias(string alias, string? groupId, string? machineId,
        string manufacturerKey = "americanpinball", string? notes = null, string? addedBy = null) =>
        new
        {
            alias,
            opdbGroupId = groupId,
            machineId,
            manufacturerKey,
            notes,
            addedBy
        };

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_ValidSeed_ReturnsAllAliases()
    {
        var path = WriteSeed(1,
            Alias("GTF", "Gj6PZ", null),
            Alias("HW", "GEL31", null));

        var catalog = new FakeCatalog(groupIds: ["Gj6PZ", "GEL31"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("GTF", result[0].Alias);
        Assert.Equal("Gj6PZ", result[0].OpdbGroupId);
        Assert.Equal("americanpinball", result[0].ManufacturerKey);
        Assert.Equal("HW", result[1].Alias);
    }

    // ── Contract: every alias resolves to a real group/machine ───────────────

    [Fact]
    public async Task Load_EveryAlias_ResolvesToARealGroupOrMachine()
    {
        // Uses the production seed file shape; each alias must resolve in the fake catalog.
        // A dangling alias silently mis-attributes nothing — but it also silently does nothing.
        // Fail CI rather than ship a lie.
        var path = WriteSeed(1,
            Alias("GTF", "Gj6PZ", null),
            Alias("Okto", "GLWBV", null),
            Alias("HW", "GEL31", null),
            Alias("HWL", "GEL31", null),
            Alias("LOV", "GWyyq", null));

        var catalog = new FakeCatalog(groupIds: KnownGroupIds);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(5, result.Count);
        // All 5 resolved — no exception thrown means no dangling entries.
    }

    [Fact]
    public async Task Load_DanglingGroupId_ThrowsAtStartup()
    {
        // An alias whose group does not exist in the catalog must throw at load time,
        // not silently do nothing at resolution time.
        var path = WriteSeed(1,
            Alias("BAD", "XXXXX", null));

        var catalog = new FakeCatalog(groupIds: []); // empty catalog — XXXXX unknown
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("XXXXX", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BAD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_DanglingMachineId_ThrowsAtStartup()
    {
        var path = WriteSeed(1,
            Alias("ED1", null, "ZZZZZ-M1"));

        var catalog = new FakeCatalog(machineIds: []); // empty catalog
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("ZZZZZ-M1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_MachineIdAlias_ResolvesByMachineId()
    {
        var path = WriteSeed(1,
            Alias("PRO", null, "GEL31-M1"));

        var catalog = new FakeCatalog(machineIds: ["GEL31-M1"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("GEL31-M1", result[0].MachineId);
        Assert.Null(result[0].OpdbGroupId);
    }

    // ── Contract: no duplicate (alias, manufacturerKey) ──────────────────────

    [Fact]
    public async Task Load_NoDuplicateAliasPerManufacturer()
    {
        var path = WriteSeed(1,
            Alias("HW", "GEL31", null, "americanpinball"),
            Alias("HW", "GEL31", null, "americanpinball")); // duplicate

        var catalog = new FakeCatalog(groupIds: ["GEL31"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("HW", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_SameAliasDifferentManufacturers_IsAllowed()
    {
        // "HW" for americanpinball and "HW" for stern are distinct scopes.
        var path = WriteSeed(1,
            Alias("HW", "GEL31", null, "americanpinball"),
            Alias("HW", "GRBN1", null, "stern"));

        var catalog = new FakeCatalog(groupIds: ["GEL31", "GRBN1"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    // ── Contract: every entry has a manufacturerKey ──────────────────────────

    [Fact]
    public async Task Load_EveryEntry_HasManufacturerKey()
    {
        // An unscoped alias could collide across manufacturers ("hw" is not universal).
        var path = WriteRaw("""
            {
              "version": 1,
              "aliases": [
                { "alias": "HW", "opdbGroupId": "GEL31", "machineId": null, "manufacturerKey": "", "notes": null, "addedBy": null }
              ]
            }
            """);

        var catalog = new FakeCatalog(groupIds: ["GEL31"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("manufacturerKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_NullManufacturerKey_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "version": 1,
              "aliases": [
                { "alias": "HW", "opdbGroupId": "GEL31", "machineId": null, "notes": null, "addedBy": null }
              ]
            }
            """);

        var catalog = new FakeCatalog(groupIds: ["GEL31"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("manufacturerKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Contract: exactly one of OpdbGroupId / MachineId ────────────────────

    [Fact]
    public async Task Load_BothGroupAndMachineIdNull_ThrowsInvalidOperationException()
    {
        var path = WriteSeed(1,
            Alias("HW", null, null)); // both null

        var catalog = new FakeCatalog();
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("HW", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_BothGroupAndMachineIdSet_ThrowsInvalidOperationException()
    {
        var path = WriteSeed(1,
            Alias("HW", "GEL31", "GEL31-M1")); // both set

        var catalog = new FakeCatalog(groupIds: ["GEL31"], machineIds: ["GEL31-M1"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("HW", ex.Message, StringComparison.Ordinal);
        Assert.Contains("both", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Contract: corrupt seed throws at startup ─────────────────────────────

    [Fact]
    public async Task Load_CorruptSeed_ThrowsAtStartup()
    {
        // Fail-fast, like CommunityResourceLoader — a corrupt alias file must not
        // silently degrade attribution.
        var path = WriteRaw("{ not valid json");

        var catalog = new FakeCatalog();
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_EmptyAliasesArray_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""{"version":1,"aliases":[]}""");

        var catalog = new FakeCatalog();
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_FileNotFound_ThrowsFileNotFoundException()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"missing-aliases-{Guid.NewGuid():N}.json");

        var catalog = new FakeCatalog();
        var loader = new MachineAliasLoader(nonexistent, catalog, NullLogger<MachineAliasLoader>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Load_EmptyAlias_ThrowsInvalidOperationException()
    {
        var path = WriteRaw("""
            {
              "version": 1,
              "aliases": [
                { "alias": "", "opdbGroupId": "GEL31", "machineId": null, "manufacturerKey": "americanpinball", "notes": null, "addedBy": null }
              ]
            }
            """);

        var catalog = new FakeCatalog(groupIds: ["GEL31"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("alias", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_AliasNormalizesToZeroTokens_ThrowsInvalidOperationException()
    {
        // An alias of "---" normalizes to zero tokens and is therefore unusable.
        var path = WriteRaw("""
            {
              "version": 1,
              "aliases": [
                { "alias": "---", "opdbGroupId": "GEL31", "machineId": null, "manufacturerKey": "americanpinball", "notes": null, "addedBy": null }
              ]
            }
            """);

        var catalog = new FakeCatalog(groupIds: ["GEL31"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(CancellationToken.None));

        Assert.Contains("zero tokens", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_CalledTwice_ReturnsSameInstance()
    {
        var path = WriteSeed(1, Alias("GTF", "Gj6PZ", null));
        var catalog = new FakeCatalog(groupIds: ["Gj6PZ"]);
        var loader = new MachineAliasLoader(path, catalog, NullLogger<MachineAliasLoader>.Instance);

        var first = await loader.LoadAsync(CancellationToken.None);
        var second = await loader.LoadAsync(CancellationToken.None);

        Assert.Same(first, second);
    }

    // ── Constructor null-checks ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MachineAliasLoader("path.json", new FakeCatalog(), null!));
    }

    [Fact]
    public void Constructor_WhitespaceSeedPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new MachineAliasLoader("   ", new FakeCatalog(), NullLogger<MachineAliasLoader>.Instance));
    }

    [Fact]
    public void Constructor_NullCatalog_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MachineAliasLoader("path.json", null!, NullLogger<MachineAliasLoader>.Instance));
    }
}
