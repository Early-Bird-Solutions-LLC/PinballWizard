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
/// Unit tests for <see cref="OpdbClient"/>: <c>/api/export</c> stream
/// behavior, auth header application, single-machine 404 → null
/// behavior. Routes through a stub <see cref="HttpMessageHandler"/> —
/// no live network.
/// </summary>
public sealed class OpdbClientTests : IDisposable
{
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

        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://opdb.org/api/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://opdb.org/api/",
            ApiToken = "test-token",
            // Caches disabled for these tests — they pin network contract
            // behavior, not the cache layer. Cache-specific tests are in
            // OpdbExportCacheTests and OpdbGroupTitleCacheTests.
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
    public async Task StreamAllMachinesAsync_HitsExportEndpoint_NotPaginatedMachines()
    {
        // Pins the corrected endpoint after the Item 4 hand-off finding
        // (DL-0003): the live OPDB API returns 404 on /api/machines?page=...
        // but 200 on /api/export. If a future refactor reaches for paging
        // again, this test fails because no /api/machines?... response is
        // stubbed and the export response is what /api/export expects.
        _handler.SetResponseFor("/api/export",
            JsonArray(MachineJson("AAA-AAAAA"), MachineJson("BBB-BBBBB"), MachineJson("CCC-CCCCC")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in _client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(3, collected.Count);
        Assert.Equal(["AAA-AAAAA", "BBB-BBBBB", "CCC-CCCCC"], collected.Select(m => m.OpdbId));

        // Exactly one request — no pagination.
        Assert.Single(_handler.Requests);
        Assert.Equal("/api/export", _handler.Requests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_EmptyArray_YieldsNothing()
    {
        _handler.SetResponseFor("/api/export", JsonArray());

        var count = 0;
        await foreach (var _ in _client.StreamAllMachinesAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_AppliesBearerToken()
    {
        _handler.SetResponseFor("/api/export", JsonArray());

        await foreach (var _ in _client.StreamAllMachinesAsync(CancellationToken.None))
        {
            // drain
        }

        var firstRequest = _handler.Requests[0];
        Assert.NotNull(firstRequest.Headers.Authorization);
        Assert.Equal("Bearer", firstRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", firstRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetMachineAsync_404_ReturnsNull()
    {
        _handler.SetResponseFor("/api/machines/UNKNOWN", null, HttpStatusCode.NotFound);

        var result = await _client.GetMachineAsync("UNKNOWN", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineAsync_200_ReturnsMappedDto()
    {
        _handler.SetResponseFor("/api/machines/GRBN-MQR4P", MachineJson("GRBN-MQR4P"));

        var result = await _client.GetMachineAsync("GRBN-MQR4P", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
    }

    [Fact]
    public async Task GetMachineAsync_BlankId_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _client.GetMachineAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task GetMachineAsync_DeserializesPhysicalMachine()
    {
        // ADR-0029: physical_machine is captured for provenance only. The
        // wire field must round-trip (nullable int) so diagnostics can see
        // it even though it is never used for canonical selection.
        _handler.SetResponseFor("/api/machines/GweeP-Ml9pZ", JsonSerializer.Serialize(new
        {
            opdb_id = "GweeP-Ml9pZ",
            is_machine = true,
            physical_machine = 0,
            name = "Godzilla (Premium/LE)",
            common_name = "",
            manufacturer = new { manufacturer_id = 1, name = "Stern Pinball, Inc." },
        }));

        var result = await _client.GetMachineAsync("GweeP-Ml9pZ", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result!.PhysicalMachine);
    }

    [Fact]
    public async Task GetMachineGroupAsync_200_ReturnsGroupWithCleanTitle()
    {
        _handler.SetResponseFor("/api/machines/GweeP", JsonSerializer.Serialize(new
        {
            opdb_id = "GweeP",
            is_machine_group = true,
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
        _handler.SetResponseFor("/api/machines/NOGRP", null, HttpStatusCode.NotFound);

        var result = await _client.GetMachineGroupAsync("NOGRP", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineGroupAsync_NonGroupBody_ReturnsNull()
    {
        // Defensive: the machines/{id} endpoint is polymorphic. A full
        // machine ID returns a machine body (is_machine_group absent →
        // false). GetMachineGroupAsync must degrade to null, not hand back
        // a bogus group DTO.
        _handler.SetResponseFor("/api/machines/GweeP-MW95j", MachineJson("GweeP-MW95j"));

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
        opdb_id = opdbId,
        is_machine = true,
        name = $"Machine {opdbId}",
        common_name = $"Machine {opdbId}",
        manufacturer = new { manufacturer_id = 1, name = "Stern Pinball, Inc." },
    });

    private static string JsonArray(params string[] items) => "[" + string.Join(",", items) + "]";

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
            // Unknown path — return empty array so paging terminates cleanly in tests that don't pre-stub all pages.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
