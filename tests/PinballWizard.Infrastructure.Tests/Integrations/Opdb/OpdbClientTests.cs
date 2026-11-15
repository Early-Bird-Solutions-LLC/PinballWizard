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
/// Unit tests for <see cref="OpdbClient"/>: Match Play V1 CDN export
/// stream behavior, no-auth on CDN, entry API 404 → null, fail-closed
/// wrong-shape handling. Routes through a stub
/// <see cref="HttpMessageHandler"/> — no live network.
/// </summary>
public sealed class OpdbClientTests : IDisposable
{
    private const string ExportPath = "/latest-opdb.json";
    private readonly StubHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly OpdbClient _client;
    private readonly RobotsTxtCache _robotsCache;

    public OpdbClientTests()
    {
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard-Tests/1.0",
            RequestDelayMs = 250,
            RespectRobotsTxt = false,
        });
        _robotsCache = new RobotsTxtCache(
            new HttpClient(new StubHandler()),
            politenessOptions,
            NullLogger<RobotsTxtCache>.Instance);

        var resolver = new DefaultPerSourcePolitenessResolver(politenessOptions);
        var gate = new PolitenessGate(_robotsCache, resolver, NullLogger<PolitenessGate>.Instance);

        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.matchplay.events/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://api.matchplay.events/",
            ExportUrl = "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json",
            ApiToken = "test-token",
            ExportCachePath = "",
            GroupTitleCachePath = "",
        });

        _client = new OpdbClient(_httpClient, gate, politenessOptions, opdbOptions, NullLogger<OpdbClient>.Instance);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task StreamAllMachinesAsync_HitsMatchPlayV1Export_NotRetiredOpdbOrgPath()
    {
        // Pins ADR-0059: catalog comes from Match Play CDN V1, never
        // opdb.org /api/export (404 since 2026-10-01).
        _handler.SetResponseFor(ExportPath,
            ExportEnvelope(
                machines: [MachineJson("AAA-AAAAA"), MachineJson("BBB-BBBBB"), MachineJson("CCC-CCCCC")]));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in _client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(3, collected.Count);
        Assert.Equal(["AAA-AAAAA", "BBB-BBBBB", "CCC-CCCCC"], collected.Select(m => m.OpdbId));

        Assert.Single(_handler.Requests);
        Assert.Equal(ExportPath, _handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("mp-data.sfo3.cdn.digitaloceanspaces.com", _handler.Requests[0].RequestUri!.Host);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_YieldsMachinesThenAliases_AndSeedsGroupTitles()
    {
        _handler.SetResponseFor(ExportPath, ExportEnvelope(
            machines: [MachineJson("GweeP-MW95j")],
            aliases: [AliasJson("GweeP-MW95j-A70th")],
            groups: [GroupJson("GweeP", "Godzilla")]));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in _client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(["GweeP-MW95j", "GweeP-MW95j-A70th"], collected.Select(m => m.OpdbId));
        Assert.True(collected[0].IsMachine);
        Assert.True(collected[1].IsAlias);

        // Group title available from export seed — no entry-API HTTP.
        var title = await _client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);
        Assert.Equal("Godzilla", title);
        Assert.Single(_handler.Requests); // only the export GET
    }

    [Fact]
    public async Task StreamAllMachinesAsync_EmptyEnvelope_ThrowsRatherThanSilentEmptyCatalog()
    {
        _handler.SetResponseFor(ExportPath, """{"machineGroups":[],"machines":[],"aliases":[]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in _client.StreamAllMachinesAsync(CancellationToken.None))
            {
                // Intentionally drain the async stream to trigger enumeration-time exception.
            }
        });

        Assert.Contains("empty machines, aliases, and machineGroups", ex.Message, StringComparison.Ordinal);
        Assert.Contains("latest-opdb.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_V2EntriesShape_ThrowsRatherThanSilentEmptyCatalog()
    {
        // opdb-v2.json uses { "entries": [...] } — must not deserialize as
        // an empty V1 envelope and sync zero machines.
        _handler.SetResponseFor(ExportPath, """{"entries":[{"opdbId":"G0l2e","entryType":"machineGroup"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in _client.StreamAllMachinesAsync(CancellationToken.None))
            {
                // Intentionally drain the async stream to trigger enumeration-time exception.
            }
        });

        Assert.Contains("empty machines, aliases, and machineGroups", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_Export404_ThrowsWithoutFallback()
    {
        _handler.SetResponseFor(ExportPath, """{"message":"not found"}""", HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in _client.StreamAllMachinesAsync(CancellationToken.None))
            {
                // Intentionally drain the async stream to trigger enumeration-time exception.
            }
        });

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("not used as a fallback", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_DoesNotAttachBearerTokenToCdnExport()
    {
        _handler.SetResponseFor(ExportPath, ExportEnvelope(machines: [MachineJson("AAA-AAAAA")]));

        await foreach (var _ in _client.StreamAllMachinesAsync(CancellationToken.None))
        {
            // Drain stream so the CDN GET is recorded for the Authorization assertion.
        }

        var firstRequest = Assert.Single(_handler.Requests);
        Assert.Null(firstRequest.Headers.Authorization);
    }

    [Fact]
    public async Task GetMachineAsync_404_ReturnsNull()
    {
        _handler.SetResponseFor("/api/opdb/entry/UNKNOWN", null, HttpStatusCode.NotFound);

        var result = await _client.GetMachineAsync("UNKNOWN", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineAsync_200_ReturnsMappedDto()
    {
        _handler.SetResponseFor("/api/opdb/entry/GRBN-MQR4P", MachineJson("GRBN-MQR4P"));

        var result = await _client.GetMachineAsync("GRBN-MQR4P", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        Assert.NotNull(_handler.Requests[0].Headers.Authorization);
        Assert.Equal("test-token", _handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GetMachineAsync_BlankId_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _client.GetMachineAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task GetMachineAsync_DeserializesPhysicalMachine()
    {
        _handler.SetResponseFor("/api/opdb/entry/GweeP-Ml9pZ", JsonSerializer.Serialize(new
        {
            opdbId = "GweeP-Ml9pZ",
            isMachine = true,
            physicalMachine = 0,
            name = "Godzilla (Premium/LE)",
            commonName = "",
            manufacturer = new { manufacturerId = 1, name = "Stern", fullName = "Stern Pinball, Inc." },
        }));

        var result = await _client.GetMachineAsync("GweeP-Ml9pZ", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result!.PhysicalMachine);
    }

    [Fact]
    public async Task GetMachineGroupAsync_200_ReturnsGroupWithCleanTitle()
    {
        _handler.SetResponseFor("/api/opdb/entry/GweeP", JsonSerializer.Serialize(new
        {
            opdbId = "GweeP",
            isMachineGroup = true,
            name = "Godzilla",
            shortname = "GZ",
        }));

        var result = await _client.GetMachineGroupAsync("GweeP", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GweeP", result!.OpdbId);
        Assert.True(result.IsMachineGroup);
        Assert.Equal("Godzilla", result.Name);
    }

    [Fact]
    public async Task GetMachineGroupAsync_404_ReturnsNull()
    {
        _handler.SetResponseFor("/api/opdb/entry/NOGRP", null, HttpStatusCode.NotFound);

        var result = await _client.GetMachineGroupAsync("NOGRP", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineGroupAsync_NonGroupBody_ReturnsNull()
    {
        _handler.SetResponseFor("/api/opdb/entry/GweeP-MW95j", MachineJson("GweeP-MW95j"));

        var result = await _client.GetMachineGroupAsync("GweeP-MW95j", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineGroupAsync_BlankSegment_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _client.GetMachineGroupAsync("", CancellationToken.None));
    }

    private static string MachineJson(string opdbId) => JsonSerializer.Serialize(new
    {
        opdbId,
        isMachine = true,
        name = $"Machine {opdbId}",
        commonName = $"Machine {opdbId}",
        manufacturer = new { manufacturerId = 1, name = "Stern", fullName = "Stern Pinball, Inc." },
    });

    private static string AliasJson(string opdbId) => JsonSerializer.Serialize(new
    {
        opdbId,
        isAlias = true,
        name = $"Alias {opdbId}",
        manufacturer = new { manufacturerId = 1, name = "Stern", fullName = "Stern Pinball, Inc." },
    });

    private static string GroupJson(string segment, string name) => JsonSerializer.Serialize(new
    {
        opdbId = segment,
        isMachineGroup = true,
        name,
        shortname = (string?)null,
    });

    private static string ExportEnvelope(
        string[]? machines = null,
        string[]? aliases = null,
        string[]? groups = null)
    {
        machines ??= [];
        aliases ??= [];
        groups ??= [];
        return "{" +
               $"\"machineGroups\":[{string.Join(",", groups)}]," +
               $"\"machines\":[{string.Join(",", machines)}]," +
               $"\"aliases\":[{string.Join(",", aliases)}]" +
               "}";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string? Body, HttpStatusCode Status)> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<HttpRequestMessage> Requests { get; } = [];

        public void SetResponseFor(string pathAndQuery, string? body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[pathAndQuery] = (body, status);
        }

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
                var response = new HttpResponseMessage(entry.Status);
                if (entry.Body is not null)
                {
                    response.Content = new StringContent(entry.Body, Encoding.UTF8, "application/json");
                }
                return Task.FromResult(response);
            }
            var notFound = new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(notFound);
        }
    }
}
