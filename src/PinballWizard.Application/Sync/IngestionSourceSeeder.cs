using System.Text.Json;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Sync;

public sealed class IngestionSourceSeeder : IIngestionSourceSeeder
{
    // Single logical partition for ingestion-source config docs (per ADR 0007 +
    // IngestionSource.PartitionKey default). Hardcoded here mirrors the literal
    // already in IIngestionSourceRepository's docstring; if this changes, both
    // surfaces update together.
    private const string ConfigPartitionKey = "config";

    private readonly IIngestionSourceRepository _repository;
    private readonly ILogger<IngestionSourceSeeder> _logger;

    public IngestionSourceSeeder(
        IIngestionSourceRepository repository,
        ILogger<IngestionSourceSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public async Task<IngestionSourceSeedResult> SeedAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Ingestion source manifest not found at '{manifestPath}'. " +
                "Run from the repo root where data/seeds/ resides, or set the path explicitly.",
                manifestPath);
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);

        List<IngestionSourceSeed>? seeds;
        try
        {
            seeds = JsonSerializer.Deserialize<List<IngestionSourceSeed>>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Ingestion source manifest at '{manifestPath}' is not valid JSON: {ex.Message}",
                ex);
        }

        if (seeds is null || seeds.Count == 0)
        {
            _logger.LogInformation("Ingestion source manifest is empty; nothing to seed.");
            return new IngestionSourceSeedResult { Inserted = 0, Updated = 0, Total = 0 };
        }

        var duplicates = seeds
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Ingestion source manifest contains duplicate id(s): " +
                string.Join(", ", duplicates) + ".");
        }

        var inserted = 0;
        var updated = 0;

        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = await _repository
                .GetByIdAsync(seed.Id, ConfigPartitionKey, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                var newEntity = new IngestionSource
                {
                    Id = seed.Id,
                    PartitionKey = ConfigPartitionKey,
                    DisplayName = seed.DisplayName,
                    ScraperImplKey = seed.ScraperImplKey,
                    BaseUrl = seed.BaseUrl,
                    Enabled = seed.Enabled,
                    Cadence = seed.Cadence,
                    PolitenessOverrides = seed.PolitenessOverrides,
                };

                await _repository.UpsertAsync(newEntity, cancellationToken).ConfigureAwait(false);
                inserted++;
                _logger.LogInformation(
                    "Seeded new ingestion source '{Id}' ({DisplayName}, cadence={Cadence}, enabled={Enabled}).",
                    seed.Id, seed.DisplayName, seed.Cadence, seed.Enabled);
            }
            else
            {
                // Apply config fields; preserve runtime fields (LastRunAt,
                // LastSuccessAt, counters, ETag) populated by actual scraper runs.
                existing.DisplayName = seed.DisplayName;
                existing.ScraperImplKey = seed.ScraperImplKey;
                existing.BaseUrl = seed.BaseUrl;
                existing.Enabled = seed.Enabled;
                existing.Cadence = seed.Cadence;
                existing.PolitenessOverrides = seed.PolitenessOverrides;

                await _repository.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
                updated++;
                _logger.LogInformation(
                    "Updated config for ingestion source '{Id}' (runtime fields preserved).",
                    seed.Id);
            }
        }

        _logger.LogInformation(
            "Ingestion source seed complete: {Inserted} inserted, {Updated} updated, {Total} total.",
            inserted, updated, seeds.Count);

        return new IngestionSourceSeedResult
        {
            Inserted = inserted,
            Updated = updated,
            Total = seeds.Count,
        };
    }
}
