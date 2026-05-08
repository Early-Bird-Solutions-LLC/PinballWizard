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

namespace PinballWizard.Scraper.Tests.Integrations.Opdb;

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
            ExportCachePath = "", // Cache disabled — tests pin sync semantics, not cache.
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);

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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);

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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
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

        var sync = new OpdbSyncService(_client, _repository, _ingestionSources, NullLogger<OpdbSyncService>.Instance, _time);
        var result = await sync.SyncAsync(OpdbSyncMode.DryRun, CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.AliasesAppended); // Pass 2 in dry-run can't see the pass-1 insert (it wasn't upserted), so the alias is orphaned.
        Assert.Equal(1, result.AliasesOrphaned);

        // The load-bearing assertion: NO writes occur in dry-run mode for
        // either pass, even though the alias would have appended an edition.
        await _repository.DidNotReceive().UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

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

        public void SetResponseFor(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[pathAndQuery] = (body, status);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
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
