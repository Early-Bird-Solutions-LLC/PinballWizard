using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Application.Tests.Sync;

public sealed class IngestionSourceSeederTests : IDisposable
{
    private readonly IIngestionSourceRepository _repo = Substitute.For<IIngestionSourceRepository>();
    private readonly IngestionSourceSeeder _seeder;
    private readonly List<string> _tempFiles = [];

    public IngestionSourceSeederTests()
    {
        _seeder = new IngestionSourceSeeder(_repo, NullLogger<IngestionSourceSeeder>.Instance);
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_FirstRun_InsertsAllEntriesWithZeroRuntimeFields()
    {
        var manifestPath = WriteManifest(
            Seed("stern", "Stern Pinball", "stern", "https://sternpinball.com/", true, "daily"),
            Seed("jjp", "Jersey Jack", "jjp", "https://www.jerseyjackpinball.com/", true, "daily"));

        _repo.GetByIdAsync(Arg.Any<string>(), "config", Arg.Any<CancellationToken>())
            .Returns((IngestionSource?)null);

        var result = await _seeder.SeedAsync(manifestPath, CancellationToken.None);

        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Total);

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IngestionSource>(e =>
                e.Id == "stern"
                && e.PartitionKey == "config"
                && e.DisplayName == "Stern Pinball"
                && e.Enabled
                && e.LastRunAt == null
                && e.LastSuccessAt == null
                && e.TotalDocumentsDiscovered == 0
                && e.TotalRunFailures == 0),
            Arg.Any<CancellationToken>());

        await _repo.Received(1).UpsertAsync(
            Arg.Is<IngestionSource>(e => e.Id == "jjp"),
            Arg.Any<CancellationToken>());
    }

    // ── Idempotency: load-bearing assertion ──────────────────────────────

    [Fact]
    public async Task SeedAsync_ReRun_AppliesConfigButPreservesRuntimeFields()
    {
        var existing = new IngestionSource
        {
            Id = "stern",
            PartitionKey = "config",
            DisplayName = "Stern Pinball (old)",
            ScraperImplKey = "stern",
            BaseUrl = "https://stern-old.example/",
            Enabled = false,
            Cadence = "manual",
            PolitenessOverrides = null,
            // Runtime fields populated by an earlier scraper run — must survive the re-seed.
            LastRunAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            LastSuccessAt = new DateTimeOffset(2026, 5, 1, 0, 30, 0, TimeSpan.Zero),
            TotalDocumentsDiscovered = 1234,
            TotalRunFailures = 7,
            ETag = "\"existing-etag\"",
        };

        _repo.GetByIdAsync("stern", "config", Arg.Any<CancellationToken>()).Returns(existing);

        IngestionSource? upserted = null;
        _repo.UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                upserted = call.Arg<IngestionSource>();
                return Task.FromResult(upserted);
            });

        var manifestPath = WriteManifest(
            Seed("stern", "Stern Pinball", "stern", "https://sternpinball.com/", true, "daily"));

        var result = await _seeder.SeedAsync(manifestPath, CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);

        Assert.NotNull(upserted);
        // Config fields applied from the seed
        Assert.Equal("Stern Pinball", upserted!.DisplayName);
        Assert.Equal("https://sternpinball.com/", upserted.BaseUrl);
        Assert.True(upserted.Enabled);
        Assert.Equal("daily", upserted.Cadence);
        // Runtime fields preserved
        Assert.Equal(1234, upserted.TotalDocumentsDiscovered);
        Assert.Equal(7, upserted.TotalRunFailures);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), upserted.LastRunAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 30, 0, TimeSpan.Zero), upserted.LastSuccessAt);
    }

    // ── Validation ────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_DuplicateIdsInManifest_ThrowsAndDoesNotUpsert()
    {
        var manifestPath = WriteManifest(
            Seed("stern", "Stern A", "stern", "https://a/", true, "daily"),
            Seed("stern", "Stern B", "stern", "https://b/", true, "weekly"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _seeder.SeedAsync(manifestPath, CancellationToken.None));

        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stern", ex.Message, StringComparison.Ordinal);

        await _repo.DidNotReceive().UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_MissingManifestFile_ThrowsFileNotFoundException()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _seeder.SeedAsync(nonexistent, CancellationToken.None));

        await _repo.DidNotReceive().UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_EmptyManifestArray_ReturnsZeroCounts()
    {
        var manifestPath = WriteRawManifest("[]");

        var result = await _seeder.SeedAsync(manifestPath, CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Total);
        await _repo.DidNotReceive().UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_MalformedJson_ThrowsInvalidOperationException()
    {
        var manifestPath = WriteRawManifest("{ this is not valid json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _seeder.SeedAsync(manifestPath, CancellationToken.None));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeedAsync_PreCancelledToken_ThrowsOperationCanceledExceptionWithoutUpserting()
    {
        var manifestPath = WriteManifest(
            Seed("stern", "Stern Pinball", "stern", "https://sternpinball.com/", true, "daily"));

        _repo.GetByIdAsync(Arg.Any<string>(), "config", Arg.Any<CancellationToken>())
            .Returns((IngestionSource?)null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ThrowsAnyAsync accepts OperationCanceledException OR its subtype
        // TaskCanceledException — both are valid cancellation signals depending
        // on which async path observes the token first (File.ReadAllTextAsync
        // raises TaskCanceledException; the explicit ThrowIfCancellationRequested
        // call raises OperationCanceledException).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _seeder.SeedAsync(manifestPath, cts.Token));

        await _repo.DidNotReceive().UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>());
    }

    // ── Production manifest sanity check ─────────────────────────────────
    // Pins that the actual data/seeds/ingestion_sources.v1.json deserializes
    // and contains the expected entries. Catches manifest edits that break
    // the schema before they ship. Phase 3 Wave 1 added "pinballmap" as
    // the 10th entry. Phase 4.5 W3b added 5 bulletin discovery entries
    // (jjp_bulletins, ap_bulletins, spooky_bulletins, cgc_bulletins,
    // pb_bulletins) — enabled=false for NoSource/Deferred, enabled=true for
    // Active (ap_bulletins). Unknown JSON properties (discoveryStatus,
    // discoveryNotes, discoveryDate) are tolerated by System.Text.Json defaults.

    [Fact]
    public void ProductionManifest_DeserializesCleanlyAndContainsExpectedEntries()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "data", "seeds", "ingestion_sources.v1.json");
        Assert.True(File.Exists(manifestPath), $"Production manifest missing at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        var seeds = JsonSerializer.Deserialize<List<IngestionSourceSeed>>(json);

        Assert.NotNull(seeds);
        Assert.Equal(16, seeds!.Count);

        // Canonical manufacturer keys per ScraperManufacturerKey,
        // OpdbMachineMapper normalization, and ScraperOrchestrator.SourceAliases.
        // CGC stays as "cgc" — matches the existing --source cgc CLI filter.
        // "pinballmap" is the Phase 3 Wave 1 addition (read-side API client,
        // no ISourceScraper, no --source alias — its key is used by
        // RecordRunResultAsync only).
        // Phase 4.5 W3b adds 5 bulletin discovery entries (enabled=false for
        // NoSource/Deferred; ap_bulletins enabled=true with ApBulletinScraper wired).
        var expectedIds = new[]
        {
            "stern", "jjp", "ap", "spooky", "spooky_support", "pinballbrothers",
            "barrelsoffun", "multimorphic", "cgc", "opdb", "pinballmap",
            "jjp_bulletins", "ap_bulletins", "spooky_bulletins", "cgc_bulletins", "pb_bulletins",
        };
        Assert.Equal(expectedIds.OrderBy(x => x), seeds.Select(s => s.Id).OrderBy(x => x));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IngestionSourceSeed Seed(
        string id, string displayName, string scraperImplKey,
        string baseUrl, bool enabled, string cadence)
    {
        return new IngestionSourceSeed
        {
            Id = id,
            DisplayName = displayName,
            ScraperImplKey = scraperImplKey,
            BaseUrl = baseUrl,
            Enabled = enabled,
            Cadence = cadence,
            PolitenessOverrides = null,
        };
    }

    private string WriteManifest(params IngestionSourceSeed[] seeds)
    {
        var json = JsonSerializer.Serialize(seeds);
        return WriteRawManifest(json);
    }

    private string WriteRawManifest(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
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
}
