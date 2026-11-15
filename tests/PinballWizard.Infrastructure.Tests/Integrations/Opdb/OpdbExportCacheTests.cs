using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Opdb;

/// <summary>
/// Tests for the on-disk cache layer in <see cref="OpdbClient.StreamAllMachinesAsync"/>.
/// Match Play OPDB V1 CDN export is cached on disk; the cache
/// eliminates repeat CDN hits for dry-run / debug / apply sequences
/// within the configured TTL.
/// </summary>
public sealed class OpdbExportCacheTests : IDisposable
{
    private const string ExportPath = "/latest-opdb.json";
    private readonly string _cachePath;

    public OpdbExportCacheTests()
    {
        _cachePath = Path.GetTempFileName();
        File.Delete(_cachePath);
    }

    public void Dispose()
    {
        if (File.Exists(_cachePath))
        {
            File.SetAttributes(_cachePath, FileAttributes.Normal);
            File.Delete(_cachePath);
        }
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheMiss_FetchesNetworkAndPersists()
    {
        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("AAA-AAAAA"), MachineJson("BBB-BBBBB")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(2, collected.Count);
        Assert.True(File.Exists(_cachePath), "cache file should have been written on cache-miss");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheHit_DoesNotHitNetwork()
    {
        File.WriteAllText(_cachePath, ExportEnvelope(MachineJson("CACHED-1"), MachineJson("CACHED-2")));

        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("NETWORK-1")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(["CACHED-1", "CACHED-2"], collected.Select(m => m.OpdbId));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_StaleCache_RefetchesFromNetwork()
    {
        File.WriteAllText(_cachePath, ExportEnvelope(MachineJson("STALE-1")));
        File.SetLastWriteTimeUtc(_cachePath, DateTime.UtcNow.AddHours(-2));

        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("FRESH-1"), MachineJson("FRESH-2")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(["FRESH-1", "FRESH-2"], collected.Select(m => m.OpdbId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheDisabledByEmptyPath_AlwaysHitsNetwork()
    {
        var (client, handler) = CreateClient(ttlSeconds: 3600, cachePathOverride: "");
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("NET-1")));

        await foreach (var _ in client.StreamAllMachinesAsync(CancellationToken.None)) { }

        Assert.Single(handler.Requests);
        Assert.False(File.Exists(_cachePath), "no cache file should be written when path is empty");
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheDisabledByZeroTtl_AlwaysHitsNetwork_ButStillPersists()
    {
        var (client, handler) = CreateClient(ttlSeconds: 0);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("NET-1")));

        await foreach (var _ in client.StreamAllMachinesAsync(CancellationToken.None)) { }

        Assert.Single(handler.Requests);
        Assert.True(File.Exists(_cachePath), "cache file should be written even with Ttl=0");
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheFileLocked_FallsBackToNetwork()
    {
        File.WriteAllText(_cachePath, ExportEnvelope(MachineJson("CACHED-1")));

        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("FALLBACK-1")));

        using var lockedHandle = new FileStream(_cachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(["FALLBACK-1"], collected.Select(m => m.OpdbId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_AtomicWrite_LeavesNoTempFile()
    {
        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("AAA")));

        await foreach (var _ in client.StreamAllMachinesAsync(CancellationToken.None)) { }

        Assert.True(File.Exists(_cachePath), "final cache file must exist");
        Assert.False(File.Exists(_cachePath + ".tmp"), "temp file must be cleaned up after successful Move");
    }

    [Fact]
    public async Task StreamAllMachinesAsync_PreExistingCacheFile_OverwriteSucceeds()
    {
        File.WriteAllText(_cachePath, ExportEnvelope(MachineJson("OLD-1")));
        File.SetAttributes(_cachePath, FileAttributes.ReadOnly);
        File.SetLastWriteTimeUtc(_cachePath, DateTime.UtcNow.AddHours(-2));

        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("FRESH-1")));

        try
        {
            var collected = new List<OpdbMachineDto>();
            await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
            {
                collected.Add(m);
            }

            Assert.Equal(["FRESH-1"], collected.Select(m => m.OpdbId));
            Assert.True(File.Exists(_cachePath), "cache file should exist after overwrite");
            var written = File.ReadAllText(_cachePath);
            Assert.Contains("FRESH-1", written);
        }
        finally
        {
            if (File.Exists(_cachePath))
            {
                File.SetAttributes(_cachePath, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CachePersistFailure_DoesNotFailFetch()
    {
        var conflictingFilePath = Path.GetTempFileName();
        var unwritablePath = Path.Combine(conflictingFilePath, "opdb-export.json");

        try
        {
            var (client, handler) = CreateClient(ttlSeconds: 3600, cachePathOverride: unwritablePath);
            handler.SetResponseFor(ExportPath, ExportEnvelope(MachineJson("NET-1"), MachineJson("NET-2")));

            var collected = new List<OpdbMachineDto>();
            await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
            {
                collected.Add(m);
            }

            Assert.Equal(2, collected.Count);
            Assert.Single(handler.Requests);
        }
        finally
        {
            if (File.Exists(conflictingFilePath)) File.Delete(conflictingFilePath);
        }
    }

    private (OpdbClient client, StubHandler handler) CreateClient(int ttlSeconds, string? cachePathOverride = null)
    {
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard-Tests/1.0",
            RequestDelayMs = 0,
            RespectRobotsTxt = false,
        });
        var robots = new RobotsTxtCache(new HttpClient(new StubHandler()), politenessOptions, NullLogger<RobotsTxtCache>.Instance);
        var resolver = new DefaultPerSourcePolitenessResolver(politenessOptions);
        var gate = new PolitenessGate(robots, resolver, NullLogger<PolitenessGate>.Instance);

        var handler = new StubHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.matchplay.events/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://api.matchplay.events/",
            ExportUrl = "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json",
            ApiToken = "test-token",
            ExportCachePath = cachePathOverride ?? _cachePath,
            ExportCacheTtlSeconds = ttlSeconds,
            GroupTitleCachePath = "",
        });

        return (
            new OpdbClient(httpClient, gate, politenessOptions, opdbOptions, NullLogger<OpdbClient>.Instance),
            handler);
    }

    private static string MachineJson(string opdbId) => JsonSerializer.Serialize(new
    {
        opdbId,
        isMachine = true,
        name = $"Machine {opdbId}",
        commonName = $"Machine {opdbId}",
        manufacturer = new { manufacturerId = 1, name = "Stern", fullName = "Stern Pinball, Inc." },
    });

    private static string ExportEnvelope(params string[] machines) =>
        "{\"machineGroups\":[],\"machines\":[" + string.Join(",", machines) + "],\"aliases\":[]}";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<HttpRequestMessage> Requests { get; } = [];

        public void SetResponseFor(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _responses[pathAndQuery] = (body, status);

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var key = request.RequestUri!.PathAndQuery;
            if (_responses.TryGetValue(key, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
                });
            }
            var notFound = new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(notFound);
        }
    }
}
