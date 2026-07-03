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
        var manifestPath = Path.Join(repoRoot, "data", "seeds", "ingestion_sources.v1.json");
        Assert.True(File.Exists(manifestPath), $"Production manifest missing at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        var seeds = JsonSerializer.Deserialize<List<IngestionSourceSeed>>(json);

        Assert.NotNull(seeds);
        Assert.Equal(21, seeds!.Count);

        // Canonical manufacturer keys per ScraperManufacturerKey,
        // OpdbMachineMapper normalization, and ScraperOrchestrator.SourceAliases.
        // CGC stays as "cgc" — matches the existing --source cgc CLI filter.
        // "pinballmap" is the Phase 3 Wave 1 addition (read-side API client,
        // no ISourceScraper, no --source alias — its key is used by
        // RecordRunResultAsync only).
        // Phase 4.5 W3b adds 5 bulletin discovery entries (enabled=false for
        // NoSource/Deferred; ap_bulletins enabled=true with ApBulletinScraper wired).
        // "pb_docs" adds Pinball Brothers per-game document PDFs (rulesheet-class).
        // "twip" adds This Week in Pinball newsletter indexing (ADR-0043, Domain-2).
        // "jjp_support" adds JJP per-edition support page PDFs (manuals, rules).
        // "pb_freshdesk" adds Pinball Brothers Freshdesk support portal (2026-07-03);
        // "pb_bulletins" is superseded by it and remains as a Superseded discovery entry.
        var expectedIds = new[]
        {
            "stern", "jjp", "jjp_support", "ap", "spooky", "spooky_support", "pinballbrothers",
            "barrelsoffun", "multimorphic", "cgc", "opdb", "pinballmap",
            "jjp_bulletins", "ap_bulletins", "spooky_bulletins", "cgc_bulletins", "pb_bulletins",
            "pb_docs",
            "kineticist_tutorials",
            "twip",
            "pb_freshdesk",
        };
        Assert.Equal(expectedIds.OrderBy(x => x), seeds.Select(s => s.Id).OrderBy(x => x));
    }

    [Fact]
    public void ProductionManifest_EveryEntryHasSourceGroupAndDiscoveryStatus()
    {
        var repoRoot = FindRepoRoot();
        var manifestPath = Path.Join(repoRoot, "data", "seeds", "ingestion_sources.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();

            Assert.True(entry.TryGetProperty("sourceGroup", out var group)
                && !string.IsNullOrWhiteSpace(group.GetString()),
                $"Entry '{id}' is missing a non-empty sourceGroup.");

            Assert.True(entry.TryGetProperty("discoveryStatus", out var status)
                && status.GetString() is "Active" or "NoSource" or "Deferred" or "Superseded",
                $"Entry '{id}' has an invalid or missing discoveryStatus.");

            // No display-name mojibake or leftover status suffixes.
            var name = entry.GetProperty("displayName").GetString()!;
            Assert.DoesNotContain("â€", name, StringComparison.Ordinal); // corrupted em-dash bytes
            Assert.DoesNotContain("(NoSource)", name, StringComparison.Ordinal);
            Assert.DoesNotContain("(Deferred)", name, StringComparison.Ordinal);
        }

        // The disabled sub-feeds (NoSource/Deferred) must carry an explanation.
        // pb_bulletins moved to Superseded (2026-07-03), so the count is 3.
        var disabledWithReason = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("discoveryStatus").GetString() is "NoSource" or "Deferred")
            .ToList();
        Assert.Equal(3, disabledWithReason.Count);
        Assert.All(disabledWithReason, e =>
        {
            var id = e.GetProperty("id").GetString();
            Assert.True(e.TryGetProperty("discoveryNotes", out var notes)
                && !string.IsNullOrWhiteSpace(notes.GetString()),
                $"Disabled entry '{id}' is missing a non-empty discoveryNotes.");
        });
    }

    // ── Discovery + group fields ──────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_FirstRun_PersistsDiscoveryAndGroupFields()
    {
        _repo.GetByIdAsync(Arg.Any<string>(), "config", Arg.Any<CancellationToken>())
            .Returns((IngestionSource?)null);

        IngestionSource? upserted = null;
        _repo.UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>())
            .Returns(call => { upserted = call.Arg<IngestionSource>(); return Task.FromResult(upserted); });

        var manifestPath = WriteManifest(
            Seed("jjp_bulletins", "Service Bulletins", "jjp_bulletins",
                "https://www.jerseyjackpinball.com/", false, "none",
                sourceGroup: "Jersey Jack Pinball",
                discoveryStatus: "NoSource",
                discoveryNotes: "No bulletin section exists.",
                discoveryDate: new DateOnly(2026, 5, 26)));

        await _seeder.SeedAsync(manifestPath, CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.Equal("Jersey Jack Pinball", upserted!.SourceGroup);
        Assert.Equal("NoSource", upserted.DiscoveryStatus);
        Assert.Equal("No bulletin section exists.", upserted.DiscoveryNotes);
        Assert.Equal(new DateOnly(2026, 5, 26), upserted.DiscoveryDate);
    }

    [Fact]
    public async Task SeedAsync_ReRun_UpdatesDiscoveryFieldsWhilePreservingRuntimeCounters()
    {
        var existing = new IngestionSource
        {
            Id = "pb_bulletins",
            PartitionKey = "config",
            DisplayName = "old",
            ScraperImplKey = "pb_bulletins",
            BaseUrl = "https://old/",
            Enabled = false,
            Cadence = "none",
            SourceGroup = "old-group",
            DiscoveryStatus = "NoSource",
            DiscoveryNotes = "old note",
            TotalDocumentsDiscovered = 99,
        };
        _repo.GetByIdAsync("pb_bulletins", "config", Arg.Any<CancellationToken>()).Returns(existing);

        IngestionSource? upserted = null;
        _repo.UpsertAsync(Arg.Any<IngestionSource>(), Arg.Any<CancellationToken>())
            .Returns(call => { upserted = call.Arg<IngestionSource>(); return Task.FromResult(upserted); });

        var manifestPath = WriteManifest(
            Seed("pb_bulletins", "Service Bulletins", "pb_bulletins",
                "https://pinballbrothers.freshdesk.com/", false, "none",
                sourceGroup: "Pinball Brothers",
                discoveryStatus: "Deferred",
                discoveryNotes: "Needs API key.",
                discoveryDate: new DateOnly(2026, 5, 26)));

        await _seeder.SeedAsync(manifestPath, CancellationToken.None);

        Assert.NotNull(upserted);
        // Discovery/group config re-applied from the seed…
        Assert.Equal("Pinball Brothers", upserted!.SourceGroup);
        Assert.Equal("Deferred", upserted.DiscoveryStatus);
        Assert.Equal("Needs API key.", upserted.DiscoveryNotes);
        Assert.Equal(new DateOnly(2026, 5, 26), upserted.DiscoveryDate);
        // …runtime counter preserved.
        Assert.Equal(99, upserted.TotalDocumentsDiscovered);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IngestionSourceSeed Seed(
        string id, string displayName, string scraperImplKey,
        string baseUrl, bool enabled, string cadence,
        string? sourceGroup = null,
        string? discoveryStatus = null,
        string? discoveryNotes = null,
        DateOnly? discoveryDate = null)
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
            SourceGroup = sourceGroup ?? displayName, // default keeps existing call sites valid
            DiscoveryStatus = discoveryStatus,
            DiscoveryNotes = discoveryNotes,
            DiscoveryDate = discoveryDate,
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
