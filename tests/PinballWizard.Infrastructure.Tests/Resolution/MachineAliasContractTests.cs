using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Resolution;

// Pins the on-disk machine_aliases.v1.json contract — the S5 gate:
//   - version = 1
//   - every alias resolves to a real OPDB group (or a machine id), never dangling
//   - no duplicate (alias, manufacturerKey) pair
//   - every entry carries manufacturerKey + audit fields (notes, addedBy)
//
// This is the silent-edit guard. MachineAliasLoaderTests exercises the LOADER
// against temp-file fixtures; nothing there reads the real seed, so without this
// file a bad group id committed to the production manifest passes CI and fails
// only at application startup.
//
// A curated alias that points at the wrong machine is worse than no alias at
// all: it mis-attributes documents with full confidence. That is why the group
// id set below is pinned explicitly rather than derived from the file itself —
// a test that reads its expectations out of the artifact it is checking cannot
// fail (#758).
public sealed class MachineAliasContractTests
{
    // Cached per CA1869 — reuse across all test methods.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // OPDB group ids sourced from the live catalog when the seed was curated
    // (plan Step 1, ADR-0054 S5). Adding an alias for a NEW machine means
    // adding its group id here too — deliberately, with the live id in hand.
    private static readonly HashSet<string> KnownGroupIds =
    [
        "Gj6PZ",  // Galactic Tank Force
        "GLWBV",  // Oktoberfest
        "GEL31",  // Hot Wheels
        "GWyyq",  // Legends of Valhalla
        "GBLzz",  // Transformers: More Than Meets the Eye (2026) — #802; live ids
                  // GBLzz-M4ok4 / GBLzz-M7Zd5, corroborated by both captured fixtures
    ];

    [Fact]
    public void Seed_Version_Is_1()
    {
        var seed = LoadSeed();

        Assert.Equal(1, seed.Version);
    }

    [Fact]
    public void EverySeedAlias_ResolvesToAKnownGroupOrMachine()
    {
        var seed = LoadSeed();

        foreach (var entry in seed.Aliases!)
        {
            var hasGroup = !string.IsNullOrWhiteSpace(entry.OpdbGroupId);
            var hasMachine = !string.IsNullOrWhiteSpace(entry.MachineId);

            Assert.True(
                hasGroup ^ hasMachine,
                $"Alias '{entry.Alias}' must set exactly one of opdbGroupId / machineId " +
                $"(group='{entry.OpdbGroupId}', machine='{entry.MachineId}').");

            if (hasGroup)
            {
                Assert.True(
                    KnownGroupIds.Contains(entry.OpdbGroupId!),
                    $"Alias '{entry.Alias}' points at opdbGroupId '{entry.OpdbGroupId}', which is " +
                    $"not a known live-catalog group. A dangling alias mis-attributes documents " +
                    $"with confidence — verify the id against OPDB and add it to KnownGroupIds.");
            }
        }
    }

    [Fact]
    public void NoDuplicate_AliasPerManufacturer()
    {
        var seed = LoadSeed();

        var dupes = seed.Aliases!
            .GroupBy(a => (
                Alias: a.Alias?.Trim().ToLowerInvariant(),
                Manufacturer: a.ManufacturerKey?.Trim().ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"({g.Key.Alias}, {g.Key.Manufacturer}) x{g.Count()}")
            .ToList();

        Assert.True(
            dupes.Count == 0,
            "Duplicate (alias, manufacturerKey) pairs make resolution order-dependent:\n  " +
            string.Join("\n  ", dupes));
    }

    [Fact]
    public void EveryEntry_CarriesManufacturerKeyAndAuditTrail()
    {
        var seed = LoadSeed();

        foreach (var entry in seed.Aliases!)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Alias),
                "An alias entry has an empty alias.");
            Assert.False(
                string.IsNullOrWhiteSpace(entry.ManufacturerKey),
                $"Alias '{entry.Alias}' has no manufacturerKey — aliases are scoped per " +
                "manufacturer, so an unscoped one would match across the whole catalog.");

            // notes/addedBy are the audit trail for WHY a curated alias exists.
            // A curated table without provenance cannot be reviewed later.
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Notes),
                $"Alias '{entry.Alias}' has no notes — record the filename or case that justified it.");
            Assert.False(
                string.IsNullOrWhiteSpace(entry.AddedBy),
                $"Alias '{entry.Alias}' has no addedBy — record who curated it.");
        }
    }

    [Fact]
    public void Seed_IsNotEmpty()
    {
        var seed = LoadSeed();

        Assert.NotEmpty(seed.Aliases!);
    }

    private static AliasSeedFile LoadSeed()
    {
        var repoRoot = FindRepoRoot();
        var seedPath = Path.Combine(repoRoot, "data", "seeds", "machine_aliases.v1.json");
        Assert.True(File.Exists(seedPath), $"Production alias seed missing at '{seedPath}'.");

        var json = File.ReadAllText(seedPath);
        var seed = JsonSerializer.Deserialize<AliasSeedFile>(json, JsonOptions);

        Assert.NotNull(seed);
        Assert.NotNull(seed!.Aliases);
        return seed;
    }

    private static string FindRepoRoot()
    {
        // Walk upward from the test assembly until we find the .slnx file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx walking up from the test assembly).");
    }

    // Local shapes so this contract test fails on a WIRE-format change even if the
    // Application-layer records are refactored — the point is to pin the file.
    private sealed record AliasSeedFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("aliases")] IReadOnlyList<AliasEntry>? Aliases);

    private sealed record AliasEntry(
        [property: JsonPropertyName("alias")] string? Alias,
        [property: JsonPropertyName("opdbGroupId")] string? OpdbGroupId,
        [property: JsonPropertyName("machineId")] string? MachineId,
        [property: JsonPropertyName("manufacturerKey")] string? ManufacturerKey,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("addedBy")] string? AddedBy);
}
