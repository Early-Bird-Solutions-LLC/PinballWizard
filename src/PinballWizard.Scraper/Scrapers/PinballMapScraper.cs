using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes machine and location data from the Pinball Map REST API.
/// Pinball Map tracks 50,000+ machines at 12,000+ locations worldwide.
/// API: https://pinballmap.com/api/v1/
/// No authentication required for read endpoints.
/// </summary>
public sealed class PinballMapScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private readonly ILogger<PinballMapScraper> _logger;

    private const string BaseApiUrl = "https://pinballmap.com/api/v1";

    public string Name => "PinballMap";

    public PinballMapScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<PinballMapScraper> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Get the machine catalog (unique machine names/manufacturers)
        _logger.LogInformation("Fetching Pinball Map machine catalog");

        var machinesUrl = $"{BaseApiUrl}/machines.json";
        PinballMapMachineResponse? machineResponse;
        try
        {
            machineResponse = await _httpClient.GetFromJsonAsync<PinballMapMachineResponse>(
                machinesUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Pinball Map machines");
            yield break;
        }

        if (machineResponse?.Machines is null)
        {
            _logger.LogWarning("Pinball Map returned no machines");
            yield break;
        }

        var machineCount = 0;
        foreach (var machine in machineResponse.Machines)
        {
            if (string.IsNullOrWhiteSpace(machine.Name)) continue;

            var slug = GenerateSlug(machine.Name, machine.Manufacturer);

            var gameRecord = new GameRecord
            {
                GameId = GameRecord.GenerateId($"pmap_{machine.Id}"),
                Title = machine.Name,
                Slug = slug,
                GamePageUrl = $"https://pinballmap.com/machines/{machine.Id}",
                Manufacturer = machine.Manufacturer,
                Year = machine.Year,
                MachineType = machine.MachineType,
                IpdbNumber = machine.IpdbId,
                OpdbId = machine.OpdbId,
                Source = new GameSourceInfo
                {
                    ScrapedFrom = machinesUrl,
                    ScrapedAt = DateTime.UtcNow
                }
            };

            machineCount++;
            yield return new ScrapedItem
            {
                Game = gameRecord,
                SourceType = SourceType.PinballMapApi,
                DiscoveryUrl = machinesUrl,
                DiscoveryContext = "Pinball Map Machine Catalog"
            };
        }

        _logger.LogInformation("Pinball Map: discovered {Count} unique machines", machineCount);

        // Phase 2: Get regions (for location context)
        _logger.LogInformation("Fetching Pinball Map regions");
        var regionsUrl = $"{BaseApiUrl}/regions.json";
        PinballMapRegionResponse? regionResponse;
        try
        {
            regionResponse = await _httpClient.GetFromJsonAsync<PinballMapRegionResponse>(
                regionsUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Pinball Map regions (non-fatal)");
            yield break;
        }

        if (regionResponse?.Regions is not null)
        {
            _logger.LogInformation("Pinball Map: {Count} regions available for location queries",
                regionResponse.Regions.Count);
        }
    }

    private static string GenerateSlug(string name, string? manufacturer)
    {
        var slug = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("'", "")
            .Replace("\"", "")
            .Replace(":", "")
            .Replace(".", "");

        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        slug = slug.Trim('-');

        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            var mfgSlug = manufacturer.ToLowerInvariant()
                .Replace(' ', '-')
                .Replace("'", "")
                .Trim('-');
            slug = $"{mfgSlug}_{slug}";
        }

        return slug;
    }
}

// JSON models for Pinball Map API
internal sealed class PinballMapMachineResponse
{
    [JsonPropertyName("machines")]
    public List<PinballMapMachine>? Machines { get; set; }
}

internal sealed class PinballMapMachine
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("machine_type")]
    public string? MachineType { get; set; }

    [JsonPropertyName("ipdb_id")]
    public int? IpdbId { get; set; }

    [JsonPropertyName("opdb_id")]
    public string? OpdbId { get; set; }

    [JsonPropertyName("machine_group_id")]
    public int? MachineGroupId { get; set; }
}

internal sealed class PinballMapRegionResponse
{
    [JsonPropertyName("regions")]
    public List<PinballMapRegion>? Regions { get; set; }
}

internal sealed class PinballMapRegion
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lon")]
    public double? Lon { get; set; }
}
