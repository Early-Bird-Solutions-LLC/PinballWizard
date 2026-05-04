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

    private static string MachineJson(string opdbId, string manufacturer, string name, string commonName) =>
        JsonSerializer.Serialize(new
        {
            opdb_id = opdbId,
            is_machine = true,
            name,
            common_name = commonName,
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
