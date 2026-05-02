using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Persistence;
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
        var gate = new PolitenessGate(robots, politenessOptions, NullLogger<PolitenessGate>.Instance);

        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://opdb.org/api/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://opdb.org/api/",
            ApiToken = "test-token",
            PageSize = 100,
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
        _handler.SetResponseFor("/api/machines?page=1&page_size=100", JsonArray(
            MachineJson("GRBN-MQR4P", manufacturer: "Stern Pinball, Inc.", name: "Stranger Things (Pro)", commonName: "Stranger Things"),
            MachineJson("XYZ", manufacturer: "Jersey Jack Pinball", name: "Wonka", commonName: "Wonka")));

        _repository.GetByOpdbIdAsync("GRBN-MQR4P", "stern", Arg.Any<CancellationToken>()).Returns((Machine?)null);
        _repository.GetByOpdbIdAsync("XYZ", "jjp", Arg.Any<CancellationToken>()).Returns((Machine?)null);

        var sync = new OpdbSyncService(_client, _repository, NullLogger<OpdbSyncService>.Instance, _time);
        var result = await sync.SyncAsync(CancellationToken.None);

        Assert.Equal(2, result.Fetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);

        await _repository.Received(2).UpsertAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ExistingMachine_MergesAndUpdates()
    {
        _handler.SetResponseFor("/api/machines?page=1&page_size=100", JsonArray(
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

        var sync = new OpdbSyncService(_client, _repository, NullLogger<OpdbSyncService>.Instance, _time);
        var result = await sync.SyncAsync(CancellationToken.None);

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
        _handler.SetResponseFor("/api/machines?page=1&page_size=100", $"[{nonMachine}]");

        var sync = new OpdbSyncService(_client, _repository, NullLogger<OpdbSyncService>.Instance, _time);
        var result = await sync.SyncAsync(CancellationToken.None);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Skipped);
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
