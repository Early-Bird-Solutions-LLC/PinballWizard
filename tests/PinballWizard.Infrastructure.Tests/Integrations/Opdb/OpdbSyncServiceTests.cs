using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Opdb;

/// <summary>
/// Tests for <see cref="OpdbSyncService"/>. Drives a stub OPDB
/// HTTP handler and a substituted <see cref="IMachineRepository"/>;
/// asserts the result counters reflect the inserted / updated /
/// skipped tally and that mapping to / merging onto existing
/// machines flows through correctly.
/// </summary>
public sealed class OpdbSyncServiceTests : IDisposable
{
    private readonly StubHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly OpdbClient _client;
    private readonly IMachineRepository _repository = Substitute.For<IMachineRepository>();
    private readonly IIngestionSourceRepository _ingestionSources = Substitute.For<IIngestionSourceRepository>();
    private readonly IMachineTitleLookupRepository _titleLookups = Substitute.For<IMachineTitleLookupRepository>();
    private static readonly DateTimeOffset NowFixed = new(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly TimeProvider _time = new FakeTimeProvider(NowFixed);

    public OpdbSyncServiceTests()
    {
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard-Tests/1.0",
            RequestDelayMs = 250,
            RespectRobotsTxt = false,
        });
        var robots = new RobotsTxtCache(new HttpClient(new StubHandler()), politenessOptions, NullLogger<RobotsTxtCache>.Instance);
        var resolver = new DefaultPerSourcePolitenessResolver(politenessOptions);
        var gate = new PolitenessGate(robots, resolver, NullLogger<PolitenessGate>.Instance);

        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://opdb.org/api/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://opdb.org/api/",
            ApiToken = "test-token",
            ExportCachePath = "",      // Cache disabled — tests pin sync semantics, not cache.
            GroupTitleCachePath = "",  // Group-title disk cache disabled for same reason.
        });

        _client = new OpdbClient(_httpClient, gate, politenessOptions, opdbOptions, NullLogger<OpdbClient>.Instance);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task SyncAsync_NewMachine_InsertsAndCounts()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things"),
            MachineJson("XYZ", manufacturer: "Jersey Jack Pinball", name: "Wonka", commonName: "Wonka")));

        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        _repository.GetByOpdbIdAsync("XYZ", "jjp", Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(2, result.Fetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);

        await _repository.Received(2).UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ExistingMachine_MergesAndUpdates()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Old Title",
            FirstSeenAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSeenAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Skipped);

        await _repository.Received(1).UpsertAsync(
            Arg.Is<Machine>(m => m.Title == "Stranger Things" && m.LastSeenAt == NowFixed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_NonMachineRecord_Skipped()
    {
        var nonMachine = JsonSerializer.Serialize(new
        {
            opdb_id = "GRBN-X",
            is_machine = false,
            manufacturer = new { manufacturer_id = 1, name = "Stern" },
        });
        _handler.SetResponseFor("/api/export", $"[{nonMachine}]");

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Skipped);
    }

    // ── Dry-run mode ─────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_DryRunNewMachine_ProjectsInsertWithoutUpserting()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things"),
            MachineJson("XYZ", manufacturer: "Jersey Jack Pinball", name: "Wonka", commonName: "Wonka")));

        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        _repository.GetByOpdbIdAsync("XYZ", "jjp", Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.DryRun, CancellationToken.None);

        // Counters reflect what WOULD have been written.
        Assert.Equal(2, result.Fetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);

        // Reads must still happen — they're how we distinguish projected
        // insert from projected update.
        await _repository.Received(2).GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The load-bearing assertion: NO writes occur in dry-run mode.
        await _repository.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ApplyMode_RecordsSuccessfulRunResultOnIngestionSource()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things"),
            MachineJson("XYZ", manufacturer: "Jersey Jack Pinball", name: "Wonka", commonName: "Wonka")));

        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        await _ingestionSources.Received(1).RecordRunResultAsync(
            "opdb",
            Arg.Is<IngestionSourceRunResult>(r =>
                r.Succeeded
                && r.RunAt == NowFixed
                && r.DocumentsDiscovered == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_DryRunMode_DoesNotRecordRunResultOnIngestionSource()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.DryRun, CancellationToken.None);

        // Dry-run should NOT touch the IngestionSource document — operator-visible
        // "last run" timestamps shouldn't reflect projection runs.
        await _ingestionSources.DidNotReceive().RecordRunResultAsync(
            Arg.Any<string>(), Arg.Any<IngestionSourceRunResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_RecordRunResultThrows_DoesNotMaskOriginalSyncSuccess()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        // Write-back fails — the sync itself succeeded, so the failure must be
        // logged but not surfaced as an exception to the caller.
        _ingestionSources
            .When(x => x.RecordRunResultAsync(Arg.Any<string>(), Arg.Any<IngestionSourceRunResult>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("simulated cosmos hiccup"));

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);

        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(1, result.Inserted);
    }

    [Fact]
    public async Task SyncAsync_ApplyMode_RepositoryThrows_RecordsFailedRunResultThenRethrows()
    {
        // Failure-path test: when the sync loop throws, the finally block
        // must still record a write-back with Succeeded=false so the
        // operator-visible TotalRunFailures counter is incremented and
        // LastRunAt advances. This is the load-bearing assertion for the
        // most operationally important code path — operators look at the
        // dashboard precisely when runs are failing.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        var simulatedFailure = new InvalidOperationException("simulated cosmos read failure");
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(simulatedFailure);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None));
        Assert.Same(simulatedFailure, actual);

        // Despite the exception, the write-back fired with Succeeded=false.
        // DocumentsDiscovered=0 because the failure happened on the first
        // record before any insert/update increment ran.
        await _ingestionSources.Received(1).RecordRunResultAsync(
            IngestionSourceIds.Opdb,
            Arg.Is<IngestionSourceRunResult>(r =>
                !r.Succeeded
                && r.RunAt == NowFixed
                && r.DocumentsDiscovered == 0),
            Arg.Any<CancellationToken>());

        // No actual machine writes — the failure cut the loop short.
        await _repository.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_DryRunExistingMachine_ProjectsUpdateWithoutUpserting()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Old Title",
            FirstSeenAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSeenAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.DryRun, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Skipped);

        await _repository.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());

        // Merge MUST still run in dry-run (per the documented contract on
        // OpdbSyncMode.DryRun). The substituted repository handed back a
        // reference to `existing`; the service mutated it in place even
        // though it didn't persist. If a future refactor wraps the merge
        // call in `if (!isDryRun)`, this assertion fails — forcing a
        // re-read of the contract.
        Assert.Equal("Stranger Things", existing.Title);
        Assert.Equal(NowFixed, existing.LastSeenAt);
    }

    // ── Aliases as editions (two-pass flow) ──────────────────────────────

    [Fact]
    public async Task SyncAsync_AliasWithExistingBase_AppendsEditionAndUpsertsBase()
    {
        // Pass 1 inserts the base machine; pass 2 appends the alias as an
        // edition and upserts the base again. Buffer ordering is exercised
        // because the alias precedes the base in the response.
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GRBN-MQR4P-A97X1", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Premium LE)"),
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        // Pass 1's read returns null so the base is created; pass 2's read
        // returns the freshly-created base. NSubstitute can't naturally
        // chain like that without test plumbing; we substitute
        // GetByOpdbIdAsync to dynamically return whatever was last
        // upserted.
        Machine? lastUpserted = null;
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>())
            .Returns(_ => lastUpserted);
        _repository.UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>())
            .Returns(call => { lastUpserted = call.Arg<Machine>(); return call.Arg<Machine>(); });

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(2, result.Fetched);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.AliasesAppended);
        Assert.Equal(0, result.AliasesOrphaned);

        // Two upserts: one for pass-1 base insert, one for pass-2 edition append.
        await _repository.Received(2).UpsertAsync(
            Arg.Is<Machine>(m => m.Id == "GRBN-MQR4P"),
            Arg.Any<CancellationToken>());

        // The final upsert has Editions populated with the parsed edition.
        Assert.NotNull(lastUpserted);
        Assert.Single(lastUpserted!.Editions);
        Assert.Equal("Premium LE", lastUpserted.Editions[0].Name);
        Assert.Equal("Stranger Things (Premium LE)", lastUpserted.Editions[0].Description);
    }

    [Fact]
    public async Task SyncAsync_AliasWithoutBase_CountedAsOrphaned_AndNotUpserted()
    {
        // The alias has no base machine in the response — its base would
        // have been a separate record but isn't there. The alias is
        // counted as orphaned and no upsert happens for it.
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GRBN-XYZ99-AABCD", manufacturer: "Stern Pinball, Inc.", name: "Phantom (LE)")));

        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.AliasesAppended);
        Assert.Equal(1, result.AliasesOrphaned);

        await _repository.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_AliasReRunWithSameAliasId_IsIdempotent()
    {
        // Re-running the sync with the same alias should NOT duplicate the
        // edition — the replace-by-OpdbAliasId guard handles re-runs. The
        // existing edition was last seen with a stale description; the
        // re-run should refresh both the description AND the source URL
        // without doubling the editions list.
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GRBN-MQR4P-A97X1", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Premium LE)")));

        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Stranger Things",
            FirstSeenAt = NowFixed,
            LastSeenAt = NowFixed,
            Editions =
            [
                new MachineEdition
                {
                    Name = "Premium LE",
                    Description = "STALE DESCRIPTION — should be replaced",
                    OpdbAliasId = "GRBN-MQR4P-A97X1",
                    OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P-A97X1",
                },
            ],
        };
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.AliasesAppended);
        Assert.Single(existing.Editions); // Still 1, not 2.
        Assert.Equal("Premium LE", existing.Editions[0].Name);
        Assert.Equal("Stranger Things (Premium LE)", existing.Editions[0].Description); // Refreshed.
        Assert.Equal("GRBN-MQR4P-A97X1", existing.Editions[0].OpdbAliasId);
    }

    [Fact]
    public async Task SyncAsync_AliasReRunOnLegacyEdition_DedupesByName()
    {
        // Legacy data path: an edition pre-dating this PR has Name set but
        // OpdbAliasId is null. Re-running the sync should still deduplicate
        // against it (replacing the legacy name-only entry with the
        // OpdbAliasId-bearing version). Without this fallback, every alias
        // run on legacy data would double the editions list.
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GRBN-MQR4P-A97X1", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Premium LE)")));

        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Stranger Things",
            FirstSeenAt = NowFixed,
            LastSeenAt = NowFixed,
            Editions = [new MachineEdition { Name = "Premium LE" }], // No OpdbAliasId — pre-dates this PR.
        };
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.AliasesAppended);
        Assert.Single(existing.Editions);
        Assert.Equal("GRBN-MQR4P-A97X1", existing.Editions[0].OpdbAliasId); // Provenance now populated.
    }

    [Fact]
    public async Task SyncAsync_AliasBlankShortName_FallsThroughToFullManufacturerName()
    {
        // Sibling drift of OpdbMachineMapper.FirstNonBlank applied to the
        // alias pass-2 path. Before the fix, a `?? ` chain on
        // `aliasDto.Manufacturer.ShortName ?? aliasDto.Manufacturer.Name`
        // preserved an empty ShortName as `""`, which then tripped
        // NormalizeManufacturerKey's blank-input guard with an
        // ArgumentException — caught by the per-alias try/catch on
        // OpdbSyncService (counted as skipped). The fix uses
        // FirstNonBlank so a blank ShortName falls through to the
        // verified-non-blank Name. This test pins that the alias is
        // folded as an edition (not silently dropped) when OPDB returns
        // ShortName="".
        var baseJson = MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: "Godzilla");
        var aliasWithBlankShortName = JsonSerializer.Serialize(new
        {
            opdb_id = "GweeP-MW95j-AvariantX",
            is_alias = true,
            name = "Godzilla (Limited Edition)",
            manufacturer = new
            {
                manufacturer_id = 1,
                name = "Stern Pinball, Inc.",
                shortname = "",  // blank, must NOT be promoted to the partition key
            },
        });
        _handler.SetResponseFor("/api/export", JsonArray(baseJson, aliasWithBlankShortName));

        Machine? lastUpserted = null;
        _repository.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns(_ => lastUpserted);
        _repository.UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>())
            .Returns(call => { lastUpserted = call.Arg<Machine>(); return call.Arg<Machine>(); });

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        var snapshot = $"AliasesAppended={result.AliasesAppended} AliasesOrphaned={result.AliasesOrphaned} Skipped={result.Skipped} Inserted={result.Inserted}";
        Assert.True(result.AliasesAppended == 1, snapshot);
        Assert.True(result.AliasesOrphaned == 0, snapshot);
        Assert.True(result.Skipped == 0, snapshot);
        Assert.Single(lastUpserted!.Editions);
        Assert.Equal("Limited Edition", lastUpserted.Editions[0].Name);
    }

    [Fact]
    public async Task SyncAsync_AliasMissingManufacturer_CountedAsSkippedNotOrphaned()
    {
        // An alias missing manufacturer can't compute its partition key,
        // so the lookup against the base machine is impossible. Counted as
        // skipped (not orphaned — orphaned implies "we looked, didn't find
        // it"; skipped means "we couldn't even look").
        var aliasWithoutManufacturer = JsonSerializer.Serialize(new
        {
            opdb_id = "GRBN-MQR4P-A97X1",
            is_alias = true,
            name = "Stranger Things (Premium LE)",
        });
        _handler.SetResponseFor("/api/export", $"[{aliasWithoutManufacturer}]");

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(0, result.AliasesAppended);
        Assert.Equal(0, result.AliasesOrphaned);
        Assert.Equal(1, result.Skipped);
        await _repository.DidNotReceive().GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_AliasLookupThrows_SkipsBadAliasAndContinues()
    {
        // Per-alias exception isolation: if the Cosmos lookup for one
        // alias throws, the remaining aliases must still be processed.
        // The bad alias is counted as `skipped` and logged at warning
        // level; the run completes successfully overall.
        //
        // Test shape: two aliases, one pointing at a base in pass-1's
        // response (succeeds) and one pointing at a phantom base whose
        // lookup throws. Pass-1 never queries the phantom (it's not in
        // the response), so the throw only fires in pass-2 — isolating
        // the assertion to the alias-loop catch.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-OK000", manufacturer: "Stern Pinball, Inc.", name: "Good Title (Pro)", commonName: "Good Title"),
            AliasJson("GRBN-OK000-AYYYY", manufacturer: "Stern Pinball, Inc.", name: "Good Title (LE)"),
            AliasJson("GRBN-PHANTOM-AXXXX", manufacturer: "Stern Pinball, Inc.", name: "Phantom (LE)")));

        Machine? lastOkUpserted = null;
        _repository.GetByOpdbIdAsync("GRBN-OK000", "stern", Arg.Any<CancellationToken>())
            .Returns(_ => lastOkUpserted);
        _repository.GetByOpdbIdAsync("GRBN-PHANTOM", "stern", Arg.Any<CancellationToken>())
            .Returns<Machine?>(_ => throw new InvalidOperationException("simulated cosmos read failure"));
        _repository.UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var m = call.Arg<Machine>();
                lastOkUpserted = m;
                return m;
            });

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(3, result.Fetched);
        Assert.Equal(1, result.Inserted);              // Pass-1 inserted GRBN-OK000.
        Assert.Equal(1, result.AliasesAppended);       // OK alias appended.
        Assert.Equal(0, result.AliasesOrphaned);
        Assert.Equal(1, result.Skipped);               // Phantom alias caught, counted skipped.
        Assert.NotNull(lastOkUpserted);
        Assert.Single(lastOkUpserted!.Editions);
    }

    [Fact]
    public async Task SyncAsync_DryRunAlias_ProjectsAppendWithoutUpserting()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GRBN-MQR4P-A97X1", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Premium LE)"),
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        Machine? lastUpserted = null;
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>())
            .Returns(_ => lastUpserted);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.DryRun, CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.AliasesAppended); // Pass 2 in dry-run can't see the pass-1 insert (it wasn't upserted), so the alias is orphaned.
        Assert.Equal(1, result.AliasesOrphaned);

        // The load-bearing assertion: NO writes occur in dry-run mode for
        // either pass, even though the alias would have appended an edition.
        await _repository.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_FoldsAliasEditionNamesIntoBaseEditionTokens()
    {
        // AB#259: the Premium/LE base machine (GweeP-Ml9pZ) owns three
        // OPDB aliases (Premium, LE, 70th Anniversary). Pass 2 already
        // folds those into Editions; this test pins that the alias edition
        // NAMES are also folded into the base's EditionTokens so the linker
        // can map a per-edition document (e.g. a "_70th_" manual) to this
        // base. The base seeds with its label-derived tokens
        // ["premium","le"]; after sync the set must be a superset of
        // {"premium","le","70th"}.
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GweeP-Ml9pZ-Apremium", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium)"),
            AliasJson("GweeP-Ml9pZ-Ale", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (LE)"),
            AliasJson("GweeP-Ml9pZ-A70th", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (70th Anniversary)")));

        var existing = new Machine
        {
            Id = "GweeP-Ml9pZ",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Godzilla",
            EditionLabel = "Premium/LE",
            EditionTokens = ["premium", "le"],
            FirstSeenAt = NowFixed,
            LastSeenAt = NowFixed,
        };
        _repository.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(3, result.AliasesAppended);
        // "70th Anniversary" → "70th" (DeriveEditionTokens drops "anniversary").
        Assert.Contains("premium", existing.EditionTokens);
        Assert.Contains("le", existing.EditionTokens);
        Assert.Contains("70th", existing.EditionTokens);
    }

    [Fact]
    public async Task SyncAsync_FoldAliasTokens_IsIdempotentAcrossReRuns()
    {
        // The daily sync re-runs; folding alias names into EditionTokens
        // must not grow the list unboundedly. Running the same alias-set
        // twice against the same base must yield the same token set (no
        // duplicates). The base already carries the previously-folded
        // tokens (premium, le, 70th) from a prior run.
        _handler.SetResponseFor("/api/export", JsonArray(
            AliasJson("GweeP-Ml9pZ-Apremium", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium)"),
            AliasJson("GweeP-Ml9pZ-Ale", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (LE)"),
            AliasJson("GweeP-Ml9pZ-A70th", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (70th Anniversary)")));

        var existing = new Machine
        {
            Id = "GweeP-Ml9pZ",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Godzilla",
            EditionLabel = "Premium/LE",
            EditionTokens = ["premium", "le", "70th"], // already folded on a prior run
            FirstSeenAt = NowFixed,
            LastSeenAt = NowFixed,
        };
        _repository.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Exactly one of each token — no growth, no duplicates.
        Assert.Equal(
            "70th,le,premium",
            string.Join(",", existing.EditionTokens.OrderBy(t => t, StringComparer.Ordinal)));
    }

    // ── ADR-0025 § 4 dual-write ──────────────────────────────────────────
    // PR 5 of the Cosmos for User Delight track adds a dual-write of the
    // machine_title_lookups materialized view alongside every base-machine
    // upsert. Tests below pin the four operationally-load-bearing
    // behaviors: (a) insert-path writes a lookup row; (b) update-path
    // with same normalized title leaves prior lookup row untouched
    // structurally; (c) rename-path moves the entry from old to new
    // lookup row, deleting the old row when it becomes empty; (d) dry-run
    // does NOT touch the lookup repo at all (matches the existing
    // dry-run-doesn't-write-machines invariant).

    [Fact]
    public async Task SyncAsync_NewMachine_DualWritesTitleLookup()
    {
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        // Lookup repo returns null on first call (no existing row), so
        // the helper creates a new row.
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Lookup row was upserted with the normalized title.
        await _titleLookups.Received(1).UpsertAsync(
            Arg.Is<MachineTitleLookup>(l =>
                l.Id == "stranger things"
                && l.PartitionKey == "stranger things"
                && l.OpdbIds.Contains("GRBN-MQR4P")
                && l.Manufacturers.Contains("stern")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_RenameTitle_RemovesEntryFromOldLookupRow()
    {
        // OPDB renames a machine: existing record had Title="Old Name",
        // new export carries name="New Name (Pro)" common_name="New Name".
        // Helper must remove the entry from the old normalized title's
        // lookup row AND upsert the new normalized title's lookup row.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "New Name (Pro)", commonName: "New Name")));

        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Old Name",
            FirstSeenAt = NowFixed,
            LastSeenAt = NowFixed,
        };
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        // Old lookup row exists with our machine + a sibling.
        var oldLookup = new MachineTitleLookup
        {
            Id = "old name",
            PartitionKey = "old name",
        };
        oldLookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]);
        oldLookup.UpsertEntry("GRBN-OTHER", "jjp", ["jjp"]);
        _titleLookups.GetByTitleAsync("Old Name", Arg.Any<CancellationToken>()).Returns(oldLookup);
        // New lookup row doesn't exist yet.
        _titleLookups.GetByTitleAsync("New Name", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Old row was upserted (sibling entry remains; our entry removed).
        await _titleLookups.Received(1).UpsertAsync(
            Arg.Is<MachineTitleLookup>(l =>
                l.Id == "old name"
                && !l.OpdbIds.Contains("GRBN-MQR4P")
                && l.OpdbIds.Contains("GRBN-OTHER")),
            Arg.Any<CancellationToken>());

        // New row was upserted with our entry.
        await _titleLookups.Received(1).UpsertAsync(
            Arg.Is<MachineTitleLookup>(l =>
                l.Id == "new name"
                && l.OpdbIds.Contains("GRBN-MQR4P")
                && l.Manufacturers.Contains("stern")),
            Arg.Any<CancellationToken>());

        // Old row was NOT deleted (sibling remains).
        await _titleLookups.DidNotReceive().DeleteByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_RenameTitle_LastEntry_DeletesOldLookupRow()
    {
        // Edge case: the rename leaves the old normalized title's row
        // empty (no other machines shared the title). The helper deletes
        // the row instead of upserting an empty list.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "New Name (Pro)", commonName: "New Name")));

        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Old Name",
            FirstSeenAt = NowFixed,
            LastSeenAt = NowFixed,
        };
        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns(existing);

        var oldLookup = new MachineTitleLookup { Id = "old name", PartitionKey = "old name" };
        oldLookup.UpsertEntry("GRBN-MQR4P", "stern", ["stern"]); // only entry
        _titleLookups.GetByTitleAsync("Old Name", Arg.Any<CancellationToken>()).Returns(oldLookup);
        _titleLookups.GetByTitleAsync("New Name", Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Old row was deleted (would have been empty otherwise).
        await _titleLookups.Received(1).DeleteByTitleAsync("Old Name", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_DryRun_DoesNotTouchLookupRepo()
    {
        // Dry-run must NOT mutate the lookup container. Matches the
        // existing dry-run-doesn't-touch-machines invariant.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.DryRun, CancellationToken.None);

        await _titleLookups.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
        await _titleLookups.DidNotReceiveWithAnyArgs().DeleteByTitleAsync(default!, default);
        await _titleLookups.DidNotReceiveWithAnyArgs().GetByTitleAsync(default!, default);
    }

    [Fact]
    public async Task SyncAsync_LookupUpsertThrows_LogsAndContinues()
    {
        // Resilience: a transient lookup-write failure must NOT abort
        // the OPDB sync. The machine row already landed; the cross-
        // partition fallback in MachineGroundingTool keeps queries
        // working until the next sync repopulates the row.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));

        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);
        _titleLookups.UpsertAsync(Arg.Any<MachineTitleLookup>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated lookup write failure"));

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Sync completed despite the lookup failure.
        Assert.Equal(1, result.Inserted);
        await _repository.Received(1).UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_WritesEditionQualifiedTitleLookupRows()
    {
        // AB#259 (Task 3): both Godzilla bases store Title="Godzilla", so a
        // bare "godzilla" lookup can't disambiguate Pro from Premium/LE. The
        // sync must ALSO write edition-qualified lookup rows keyed off each
        // base's EditionTokens, so getMachineByTitle("Godzilla Premium")
        // resolves to GweeP-Ml9pZ.
        //
        // Production-faithful fixtures: EditionTokens is label-derived in
        // pass-1 (Task 1) AND alias-folded in pass-2 (Task 2). The Pro base
        // name "Godzilla (Pro)" → ["pro"]. The Premium/LE base name
        // "Godzilla (Premium/LE)" → ["premium","le"] from the label, with
        // "70th" arriving ONLY via the "70th Anniversary" alias folded in
        // pass-2. This pins that the edition-qualified rows are written AFTER
        // pass-2 (so the alias-derived "70th" row exists) — a pass-1-time
        // write would silently drop it.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: "Godzilla"),
            MachineJson("GweeP-Ml9pZ", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium/LE)", commonName: "Godzilla"),
            AliasJson("GweeP-Ml9pZ-A70th", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (70th Anniversary)")));

        // Pass-1 inserts the two bases; pass-2 re-reads the Premium/LE base to
        // fold its "70th Anniversary" alias. Mirror the alias-fold tests'
        // dynamic stub: return the last-upserted instance for GweeP-Ml9pZ so
        // pass-2 sees (and mutates) the folded-token object, and null for the
        // Pro base (no alias).
        Machine? lastPremLe = null;
        _repository.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        _repository.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>())
            .Returns(_ => lastPremLe);
        _repository.UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var m = call.Arg<Machine>();
                if (m.Id == "GweeP-Ml9pZ") lastPremLe = m;
                return m;
            });
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        // Capture every lookup row the sync upserts, keyed by normalized title.
        var upsertedLookups = new ConcurrentBag<MachineTitleLookup>();
        await _titleLookups.UpsertAsync(Arg.Do<MachineTitleLookup>(upsertedLookups.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Sanity: the alias was folded in pass-2 (so "70th" is in the token set).
        Assert.Equal(1, result.AliasesAppended);

        // Helper: assert at least one upserted row for the normalized title
        // carries the expected base id. (The stubbed GetByTitleAsync returns
        // null on every call, so the production read-modify-write that would
        // collapse same-title writes onto one shared row can't fire here —
        // each base produces its own row instance. We therefore assert the
        // expected base id is present in some row for the key, not that there
        // is exactly one row. Production behavior — a single shared row per
        // ADR-0025 §4 — is covered by SyncAsync_NewMachine_DualWritesTitleLookup.)
        static void AssertRowMapsTo(ConcurrentBag<MachineTitleLookup> rows, string normalizedTitle, string expectedOpdbId)
        {
            var matching = rows.Where(l => l.Id == normalizedTitle).ToList();
            Assert.True(matching.Count > 0, $"no lookup row written for normalized title '{normalizedTitle}'");
            Assert.Contains(matching, l => l.OpdbIds.Contains(expectedOpdbId));
            // The edition-qualified key must NEVER be the bare franchise key —
            // they must coexist as distinct rows.
            Assert.NotEqual("godzilla", normalizedTitle);
        }

        // The bare franchise rows still exist and collectively cover both bases.
        var bareRows = upsertedLookups.Where(l => l.Id == "godzilla").ToList();
        Assert.Contains(bareRows, l => l.OpdbIds.Contains("GweeP-MW95j"));
        Assert.Contains(bareRows, l => l.OpdbIds.Contains("GweeP-Ml9pZ"));

        // Edition-qualified rows resolve to the correct base.
        AssertRowMapsTo(upsertedLookups, "godzilla pro", "GweeP-MW95j");
        AssertRowMapsTo(upsertedLookups, "godzilla premium", "GweeP-Ml9pZ");
        AssertRowMapsTo(upsertedLookups, "godzilla le", "GweeP-Ml9pZ");
        AssertRowMapsTo(upsertedLookups, "godzilla 70th", "GweeP-Ml9pZ");
    }

    [Fact]
    public async Task SyncAsync_ReSync_ExistingBases_WritesEditionQualifiedTitleLookupRows()
    {
        // REPRODUCTION (AB#259 Track B live failure): the FIRST live re-sync
        // updated 2154 existing bases (0 inserted) and populated EditionTokens
        // correctly, but wrote ZERO edition-qualified lookup rows. The original
        // phase-(d) test (SyncAsync_WritesEditionQualifiedTitleLookupRows) only
        // exercised the INSERTED path (GetByOpdbIdAsync -> null). This pins the
        // UPDATED (re-sync) path: both bases already exist, so phase-b takes the
        // `updated` branch and pass-2 re-reads the base. The edition-qualified
        // rows MUST still be written.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: ""),
            MachineJson("GweeP-Ml9pZ", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium/LE)", commonName: ""),
            AliasJson("GweeP-Ml9pZ-A70th", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (70th Anniversary)")));
        _handler.SetResponseFor("/api/machines/GweeP", GroupJson("GweeP", "Godzilla"));

        // BOTH bases ALREADY EXIST — the re-sync (updated) path the live run took.
        var pro = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Godzilla", GroupId = "GweeP", Year = 2021, FirstSeenAt = NowFixed, LastSeenAt = NowFixed,
        };
        Machine? lastPremLe = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Godzilla", GroupId = "GweeP", Year = 2021, FirstSeenAt = NowFixed, LastSeenAt = NowFixed,
        };
        _repository.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>()).Returns(pro);
        _repository.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>()).Returns(_ => lastPremLe);
        _repository.UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>())
            .Returns(call => { var m = call.Arg<Machine>(); if (m.Id == "GweeP-Ml9pZ") lastPremLe = m; return m; });
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var upsertedLookups = new ConcurrentBag<MachineTitleLookup>();
        await _titleLookups.UpsertAsync(Arg.Do<MachineTitleLookup>(upsertedLookups.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        bool RowMapsTo(string normalizedTitle, string expectedOpdbId) =>
            upsertedLookups.Any(l => l.Id == normalizedTitle && l.OpdbIds.Contains(expectedOpdbId));

        Assert.True(RowMapsTo("godzilla pro", "GweeP-MW95j"), "re-sync path did not write 'godzilla pro' qualified lookup row");
        Assert.True(RowMapsTo("godzilla premium", "GweeP-Ml9pZ"), "re-sync path did not write 'godzilla premium' qualified lookup row");
        Assert.True(RowMapsTo("godzilla le", "GweeP-Ml9pZ"), "re-sync path did not write 'godzilla le' qualified lookup row");
        Assert.True(RowMapsTo("godzilla 70th", "GweeP-Ml9pZ"), "re-sync path did not write 'godzilla 70th' qualified lookup row");
    }

    // --- ADR-0029 S4: per-segment group-title resolution (D1) ---
    // The model: every is_machine base record stays a DISTINCT Machine
    // (no fold, no canonical pick). When common_name is empty (modern
    // Stern), Title comes from the is_machine_group record; GroupId
    // relates siblings. These tests assert that behavior end-to-end
    // through SyncAsync.

    [Fact]
    public async Task SyncAsync_Godzilla_BothBasesStayDistinctWithCleanGroupTitle()
    {
        // Godzilla: pm:1 Pro (GweeP-MW95j) + pm:0 Premium/LE
        // (GweeP-Ml9pZ). Both have empty common_name. ADR-0029: TWO
        // distinct Machine docs, BOTH titled "Godzilla" (from the group
        // record), both GroupId=GweeP. NOT folded into one.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: ""),
            MachineJson("GweeP-Ml9pZ", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium/LE)", commonName: "")));
        _handler.SetResponseFor("/api/machines/GweeP", GroupJson("GweeP", "Godzilla"));
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var captured = new ConcurrentBag<Machine>();
        await _repository.UpsertAsync(Arg.Do<Machine>(captured.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(2, result.Inserted); // two distinct machines, NOT one
        var pro = Assert.Single(captured, m => m.Id == "GweeP-MW95j");
        var prem = Assert.Single(captured, m => m.Id == "GweeP-Ml9pZ");
        Assert.Equal("Godzilla", pro.Title);   // clean group title (D1), not "Godzilla (Pro)"
        Assert.Equal("Godzilla", prem.Title);
        Assert.Equal("GweeP", pro.GroupId);    // related by GroupId
        Assert.Equal("GweeP", prem.GroupId);
    }

    [Fact]
    public async Task SyncAsync_Metallica_ThreeBasesAllDistinctSameGroupTitle()
    {
        // Metallica: 3 separate base records, all pm:1 (no pm:0). ADR-0029:
        // three distinct Machine docs, all titled "Metallica", all
        // GroupId=GRBE4 — the "all-pm:1" majority pattern the fold model
        // could not handle.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBE4-MQK1Z", manufacturer: "Stern Pinball, Inc.", name: "Metallica (Pro)", commonName: ""),
            MachineJson("GRBE4-MJ9rE", manufacturer: "Stern Pinball, Inc.", name: "Metallica (Premium)", commonName: ""),
            MachineJson("GRBE4-MOE4l", manufacturer: "Stern Pinball, Inc.", name: "Metallica (LE)", commonName: "")));
        _handler.SetResponseFor("/api/machines/GRBE4", GroupJson("GRBE4", "Metallica"));
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var captured = new ConcurrentBag<Machine>();
        await _repository.UpsertAsync(Arg.Do<Machine>(captured.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(3, result.Inserted);
        Assert.All(captured, m => Assert.Equal("Metallica", m.Title));
        Assert.All(captured, m => Assert.Equal("GRBE4", m.GroupId));
        Assert.Equal(3, captured.Select(m => m.Id).Distinct().Count()); // genuinely 3 distinct
    }

    [Fact]
    public async Task SyncAsync_GroupRecordFetchedOncePerSegment()
    {
        // The is_machine_group fetch is memoized per run: two editions
        // sharing GweeP must trigger exactly ONE GET /api/machines/GweeP.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: ""),
            MachineJson("GweeP-Ml9pZ", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium/LE)", commonName: "")));
        _handler.SetResponseFor("/api/machines/GweeP", GroupJson("GweeP", "Godzilla"));
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, _handler.RequestCountFor("/api/machines/GweeP"));
    }

    [Fact]
    public async Task SyncAsync_GroupRecord404_FallsBackToEditionSuffixedTitle()
    {
        // Graceful degradation (ADR-0029 D1): if the group record 404s,
        // the machine keeps its name-derived title rather than the sync
        // failing. The title is the edition-suffixed name (the documented
        // pre-D1 behavior), and GroupId is still set for sibling relation.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: "")));
        _handler.SetResponseFor("/api/machines/GweeP", "{}", HttpStatusCode.NotFound);
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var captured = new ConcurrentBag<Machine>();
        await _repository.UpsertAsync(Arg.Do<Machine>(captured.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Inserted); // sync did NOT fail
        var m = Assert.Single(captured);
        Assert.Equal("Godzilla (Pro)", m.Title); // fell back to name
        Assert.Equal("GweeP", m.GroupId);        // GroupId still derived
    }

    [Fact]
    public async Task SyncAsync_GroupRecord500_FallsBackWithoutAbortingSync()
    {
        // Distinct from the 404 case: a transient 5xx on the group
        // endpoint exercises ResolveGroupTitleAsync's broad catch (404 is
        // handled in GetMachineGroupAsync itself; 500 throws
        // HttpRequestException). The sync must NOT abort — the record
        // keeps its edition-suffixed title and GroupId is still set. This
        // pins the best-effort contract against a future change that
        // narrows the catch scope.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: "")));
        _handler.SetResponseFor("/api/machines/GweeP", "upstream error", HttpStatusCode.InternalServerError);
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var captured = new ConcurrentBag<Machine>();
        await _repository.UpsertAsync(Arg.Do<Machine>(captured.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        Assert.Equal(1, result.Inserted); // 5xx did NOT abort the sync
        var m = Assert.Single(captured);
        Assert.Equal("Godzilla (Pro)", m.Title); // fell back to name
        Assert.Equal("GweeP", m.GroupId);
    }

    [Fact]
    public async Task SyncAsync_Singleton_NoGroupRecord_TitleFromCommonName()
    {
        // Regression guard: a populated common_name WINS over any group
        // title (ADR-0029 precedence: common_name → groupTitle → name).
        // The resolver still runs per segment (it is precedence-blind by
        // design — the mapper decides), so we deliberately stub a
        // *conflicting* group title and assert common_name still wins.
        // This proves the precedence, not an (incorrect) zero-fetch
        // claim — the resolver is memoized so the call cost is bounded.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things")));
        _handler.SetResponseFor("/api/machines/GRBN", GroupJson("GRBN", "WRONG GROUP TITLE"));
        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var captured = new ConcurrentBag<Machine>();
        await _repository.UpsertAsync(Arg.Do<Machine>(captured.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        var m = Assert.Single(captured);
        Assert.Equal("Stranger Things", m.Title); // common_name wins over the conflicting group title
        Assert.Equal("GRBN", m.GroupId);
    }

    [Fact]
    public async Task SyncAsync_WritesManufacturerQualifiedTitleLookupRows()
    {
        // Phase (e): "stern godzilla" lookup row must exist after a sync so that
        // getMachineByTitle("Stern Godzilla") resolves via the fast point-read
        // path instead of falling through to the cross-partition fallback.
        // Both Godzilla bases (Pro + Premium/LE) must map to "stern godzilla";
        // multi-token manufacturers (e.g. jjp → ["jjp","jersey","jack"]) produce
        // one row per token.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: "Godzilla"),
            MachineJson("GweeP-Ml9pZ", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium/LE)", commonName: "Godzilla")));

        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var upsertedLookups = new ConcurrentBag<MachineTitleLookup>();
        await _titleLookups.UpsertAsync(Arg.Do<MachineTitleLookup>(upsertedLookups.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // Both Stern bases must appear in the "stern godzilla" row.
        var sternRows = upsertedLookups.Where(l => l.Id == "stern godzilla").ToList();
        Assert.True(sternRows.Count > 0, "no lookup row written for 'stern godzilla'");
        Assert.Contains(sternRows, l => l.OpdbIds.Contains("GweeP-MW95j"));
        Assert.Contains(sternRows, l => l.OpdbIds.Contains("GweeP-Ml9pZ"));

        // Bare "godzilla" row must still coexist.
        var bareRows = upsertedLookups.Where(l => l.Id == "godzilla").ToList();
        Assert.True(bareRows.Count > 0, "bare 'godzilla' row was not written");
    }

    [Fact]
    public async Task SyncAsync_ReSync_ExistingBases_WritesManufacturerQualifiedTitleLookupRows()
    {
        // Resync path (existing bases): the manufacturer-qualified rows must be
        // written even when both machines already existed (GetByOpdbIdAsync
        // returns an existing machine rather than null), because phase (e) uses
        // editionTokenBases which is populated regardless of insert-vs-update.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("GweeP-MW95j", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Pro)", commonName: "Godzilla"),
            MachineJson("GweeP-Ml9pZ", manufacturer: "Stern Pinball, Inc.", name: "Godzilla (Premium/LE)", commonName: "Godzilla")));

        var existingPro = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Godzilla", EditionLabel = "Pro", EditionTokens = ["pro"],
            FirstSeenAt = NowFixed, LastSeenAt = NowFixed,
        };
        var existingPremLe = new Machine
        {
            Id = "GweeP-Ml9pZ", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Godzilla", EditionLabel = "Premium/LE", EditionTokens = ["premium", "le"],
            FirstSeenAt = NowFixed, LastSeenAt = NowFixed,
        };
        _repository.GetByOpdbIdAsync("GweeP-MW95j", "stern", Arg.Any<CancellationToken>()).Returns(existingPro);
        _repository.GetByOpdbIdAsync("GweeP-Ml9pZ", "stern", Arg.Any<CancellationToken>()).Returns(existingPremLe);
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var upsertedLookups = new ConcurrentBag<MachineTitleLookup>();
        await _titleLookups.UpsertAsync(Arg.Do<MachineTitleLookup>(upsertedLookups.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        var sternRows = upsertedLookups.Where(l => l.Id == "stern godzilla").ToList();
        Assert.True(sternRows.Count > 0, "no 'stern godzilla' row written on resync path");
        Assert.Contains(sternRows, l => l.OpdbIds.Contains("GweeP-MW95j"));
        Assert.Contains(sternRows, l => l.OpdbIds.Contains("GweeP-Ml9pZ"));
    }

    [Fact]
    public async Task SyncAsync_MultiTokenManufacturer_WritesOneRowPerToken()
    {
        // JJP's GetMatchTokens returns ["jjp", "jersey", "jack"] — three tokens.
        // Phase (e) must write one lookup row per token, not one row for the
        // manufacturer as a whole. Verifies that the per-token loop in phase (e)
        // fires correctly for multi-token manufacturers.
        _handler.SetResponseFor("/api/export", JsonArray(
            MachineJson("XYZ12-AB34C", manufacturer: "Jersey Jack Pinball", name: "Toy Story 4 (Standard Edition)", commonName: "Toy Story 4")));

        _repository.GetByOpdbIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        _titleLookups.GetByTitleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MachineTitleLookup?)null);

        var upsertedLookups = new ConcurrentBag<MachineTitleLookup>();
        await _titleLookups.UpsertAsync(Arg.Do<MachineTitleLookup>(upsertedLookups.Add), Arg.Any<CancellationToken>());

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, _titleLookups, NullLogger<OpdbSyncService>.Instance, scraperSettings: null, _time);
        await sync.SyncAsync(OpdbSyncMode.Apply, CancellationToken.None);

        // All three JJP token rows must be written.
        foreach (var key in new[] { "jjp toy story 4", "jersey toy story 4", "jack toy story 4" })
        {
            var rows = upsertedLookups.Where(l => l.Id == key).ToList();
            Assert.True(rows.Count > 0, $"no lookup row written for '{key}'");
            Assert.Contains(rows, l => l.OpdbIds.Contains("XYZ12-AB34C"));
        }
    }

    private static string GroupJson(string segment, string name) =>
        JsonSerializer.Serialize(new
        {
            opdb_id = segment,
            is_machine_group = true,
            name,
            shortname = (string?)null,
        });

    private static string MachineJson(string opdbId, string manufacturer, string name, string commonName) =>
        JsonSerializer.Serialize(new
        {
            opdb_id = opdbId,
            is_machine = true,
            name,
            common_name = commonName,
            manufacturer = new { manufacturer_id = 1, name = manufacturer },
        });

    private static string AliasJson(string opdbId, string manufacturer, string name) =>
        JsonSerializer.Serialize(new
        {
            opdb_id = opdbId,
            is_alias = true,
            name,
            manufacturer = new { manufacturer_id = 1, name = manufacturer },
        });

    private static string JsonArray(params string[] items) => "[" + string.Join(",", items) + "]";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _requestCounts = new(StringComparer.OrdinalIgnoreCase);

        public void SetResponseFor(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[pathAndQuery] = (body, status);
        }

        public int RequestCountFor(string pathAndQuery) =>
            _requestCounts.TryGetValue(pathAndQuery, out var n) ? n : 0;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            _requestCounts[key] = RequestCountFor(key) + 1;
            if (_responses.TryGetValue(key, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixed;
        public FakeTimeProvider(DateTimeOffset fixedTime) => _fixed = fixedTime;
        public override DateTimeOffset GetUtcNow() => _fixed;
    }
}
