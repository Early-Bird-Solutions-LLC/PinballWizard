using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Scrapes machine data from the Open Pinball Database (OPDB) REST API.
/// OPDB provides structured machine data with manufacturer, year, type, and cross-references.
/// API docs: https://opdb.org/api
///
/// The /api/export endpoint requires an API token (free account at opdb.org).
/// Set OPDB_API_TOKEN environment variable or Scraper:OpdbApiToken in appsettings.json.
/// Without a token, the scraper uses the no-auth typeahead search to discover machines.
/// </summary>
public sealed class OpdbScraper : ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ScraperSettings _settings;
    private readonly ILogger<OpdbScraper> _logger;

    private const string ExportUrl = "https://opdb.org/api/export";
    private const string TypeaheadUrl = "https://opdb.org/api/search/typeahead";

    public string Name => "OPDB";

    public OpdbScraper(
        HttpClient httpClient,
        IOptions<ScraperSettings> settings,
        ILogger<OpdbScraper> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiToken = _settings.OpdbApiToken
                       ?? Environment.GetEnvironmentVariable("OPDB_API_TOKEN");

        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            await foreach (var item in ScrapeViaExportAsync(apiToken, cancellationToken))
                yield return item;
        }
        else
        {
            _logger.LogWarning(
                "No OPDB API token configured. Using typeahead search (slower, partial data). " +
                "Set OPDB_API_TOKEN env var or Scraper:OpdbApiToken in appsettings.json for full export.");

            await foreach (var item in ScrapeViaTypeaheadAsync(cancellationToken))
                yield return item;
        }
    }

    private async IAsyncEnumerable<ScrapedItem> ScrapeViaExportAsync(
        string apiToken,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = $"{ExportUrl}?api_token={apiToken}";
        _logger.LogInformation("Fetching OPDB full machine export (authenticated)");

        OpdbExport? export;
        try
        {
            export = await _httpClient.GetFromJsonAsync<OpdbExport>(
                url, JsonOptions, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch OPDB export (check API token)");
            yield break;
        }

        if (export?.Machines is null)
        {
            _logger.LogWarning("OPDB export returned no machines");
            yield break;
        }

        var count = 0;
        foreach (var machine in export.Machines)
        {
            var item = MachineToScrapedItem(machine, ExportUrl, "OPDB Machine Export");
            if (item is null) continue;
            count++;
            yield return item;
        }

        _logger.LogInformation("OPDB export: discovered {Count} machines", count);
    }

    private async IAsyncEnumerable<ScrapedItem> ScrapeViaTypeaheadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Discovering OPDB machines via typeahead search");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        // Search with single characters to maximize coverage
        var queries = "abcdefghijklmnopqrstuvwxyz0123456789"
            .Select(c => c.ToString())
            .ToList();

        foreach (var query in queries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var url = $"{TypeaheadUrl}?q={query}";
            List<OpdbTypeaheadResult>? results;
            try
            {
                results = await _httpClient.GetFromJsonAsync<List<OpdbTypeaheadResult>>(
                    url, JsonOptions, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Typeahead search failed for query '{Query}'", query);
                continue;
            }

            if (results is null) continue;

            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.Id)) continue;
                if (!seen.Add(result.Id)) continue;

                var machine = result.ToOpdbMachine();
                var item = MachineToScrapedItem(machine, url, "OPDB Typeahead Search");
                if (item is null) continue;
                count++;
                yield return item;
            }

            // Polite delay between requests
            await Task.Delay(200, cancellationToken);
        }

        _logger.LogInformation("OPDB typeahead: discovered {Count} unique machines", count);
    }

    private ScrapedItem? MachineToScrapedItem(OpdbMachine machine, string sourceUrl, string context)
    {
        if (string.IsNullOrWhiteSpace(machine.Name)) return null;

        var slug = GenerateSlug(machine.Name, machine.ManufacturerName);

        var gameRecord = new GameRecord
        {
            GameId = GameRecord.GenerateId(slug),
            Title = machine.Name,
            Slug = slug,
            GamePageUrl = $"https://opdb.org/machines/{machine.OpdbId}",
            Manufacturer = machine.ManufacturerName,
            Year = machine.Year,
            MachineType = machine.MachineType,
            OpdbId = machine.OpdbId,
            IpdbNumber = machine.IpdbId,
            Source = new GameSourceInfo
            {
                ScrapedFrom = sourceUrl,
                ScrapedAt = DateTime.UtcNow
            }
        };

        return new ScrapedItem
        {
            Game = gameRecord,
            SourceType = SourceType.OpdbApi,
            DiscoveryUrl = sourceUrl,
            DiscoveryContext = context
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

// JSON models for OPDB API response
internal sealed class OpdbExport
{
    [JsonPropertyName("machines")]
    public List<OpdbMachine>? Machines { get; set; }
}

/// <summary>
/// Typeahead endpoint returns a different shape: {id, name, supplementary, display}
/// where supplementary = "Manufacturer, Year" and display = machine type.
/// </summary>
internal sealed class OpdbTypeaheadResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("supplementary")]
    public string? Supplementary { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }

    public OpdbMachine ToOpdbMachine()
    {
        string? manufacturer = null;
        int? year = null;

        if (!string.IsNullOrWhiteSpace(Supplementary))
        {
            var lastComma = Supplementary.LastIndexOf(',');
            if (lastComma > 0)
            {
                manufacturer = Supplementary[..lastComma].Trim();
                var yearStr = Supplementary[(lastComma + 1)..].Trim();
                if (int.TryParse(yearStr, out var y))
                    year = y;
            }
            else
            {
                manufacturer = Supplementary.Trim();
            }
        }

        return new OpdbMachine
        {
            OpdbId = Id,
            Name = Name,
            ManufacturerName = manufacturer,
            Year = year,
            MachineType = Display
        };
    }
}

internal sealed class OpdbMachine
{
    [JsonPropertyName("opdb_id")]
    public string? OpdbId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("manufacturer_name")]
    public string? ManufacturerName { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("machine_type")]
    public string? MachineType { get; set; }

    [JsonPropertyName("ipdb_id")]
    public int? IpdbId { get; set; }
}
