using System.Text.Json;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Sync;

// Seeds the `scraped_documents` Cosmos container from the file-system
// catalog (`data/metadata/catalog.json`). This bridges the Phase 1
// scraper's file-catalog output to the Phase 4 RAG Change Feed source
// container: once seeded, the `RagIngestionWorker`'s Change Feed
// processor delivers each document to the embedding pipeline.
//
// Seeding strategy: catalog → filter Manual/ServiceBulletin with a
// resolved game title → `IMachineRepository.QueryByTitleAsync` to get
// the OPDB ID → `IScrapedDocumentRepository.UpsertAsync`. Documents
// whose game title cannot be resolved in the `machines` Cosmos container
// are skipped and logged; the caller decides whether that is an error.
public sealed class ScrapedDocumentSeeder : IScrapedDocumentSeeder
{
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IScrapedDocumentRepository _repository;
    private readonly IMachineRepository _machineRepository;
    private readonly ILogger<ScrapedDocumentSeeder> _logger;

    public ScrapedDocumentSeeder(
        IScrapedDocumentRepository repository,
        IMachineRepository machineRepository,
        ILogger<ScrapedDocumentSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(machineRepository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _machineRepository = machineRepository;
        _logger = logger;
    }

    public async Task<ScrapedDocumentSeedResult> SeedAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);

        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                $"Catalog not found at '{catalogPath}'. " +
                "Run from the repo root where data/metadata/catalog.json resides.",
                catalogPath);
        }

        var json = await File.ReadAllTextAsync(catalogPath, cancellationToken);
        var catalog = JsonSerializer.Deserialize<CatalogJson>(json, CatalogJsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse catalog at '{catalogPath}'.");

        var result = new ScrapedDocumentSeedResult();

        foreach (var doc in catalog.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var docType = doc.Classification?.DocumentType;
            if (docType is not ("Manual" or "ServiceBulletin"))
                continue;

            if (doc.Game?.Title is not { Length: > 0 } gameTitle)
            {
                _logger.LogDebug("Skipping {DocumentId} ({DocType}): no game title.", doc.DocumentId, docType);
                result.Skipped++;
                continue;
            }

            // Look up the machine in Cosmos by title.
            var machine = await _machineRepository.QueryByTitleAsync(gameTitle, cancellationToken)
                .FirstOrDefaultAsync(cancellationToken);

            if (machine is null)
            {
                _logger.LogDebug(
                    "Skipping {DocumentId} ({DocType}, title={Title}): no matching machine in Cosmos.",
                    doc.DocumentId, docType, gameTitle);
                result.Skipped++;
                continue;
            }

            await _repository.UpsertAsync(
                record: BuildRecord(doc, docType),
                machineId: machine.Id,
                machineTitle: machine.Title,
                manufacturer: machine.ManufacturerDisplayName,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Seeded {DocumentId} ({DocType}) → machine {MachineId} ({Title}).",
                doc.DocumentId, docType, machine.Id, machine.Title);

            result.Upserted++;
        }

        _logger.LogInformation(
            "ScrapedDocumentSeeder complete: {Upserted} upserted, {Skipped} skipped.",
            result.Upserted, result.Skipped);

        return result;
    }

    private static DocumentRecord BuildRecord(CatalogDocumentJson doc, string docType)
    {
        return new DocumentRecord
        {
            DocumentId = doc.DocumentId,
            Source = new SourceInfo
            {
                DiscoveryUrl = doc.Source?.DiscoveryUrl ?? string.Empty,
                DiscoveryContext = doc.Source?.DiscoveryContext ?? string.Empty,
                FileUrl = doc.Source?.FileUrl ?? string.Empty,
                LinkText = doc.Source?.LinkText,
                SourceType = Enum.TryParse<SourceType>(doc.Source?.SourceType, out var st) ? st : SourceType.ManualsPage,
                ScrapedAt = doc.Source?.ScrapedAt ?? DateTime.UtcNow,
            },
            Classification = new ClassificationInfo
            {
                DocumentType = Enum.TryParse<DocumentType>(docType, out var dt) ? dt : DocumentType.Other,
                FileFormat = doc.Classification?.FileFormat ?? "pdf",
            },
            Game = doc.Game is null ? null : new GameReference
            {
                Title = doc.Game.Title,
                Slug = doc.Game.Slug ?? string.Empty,
                Edition = doc.Game.Edition,
                GamePageUrl = doc.Game.GamePageUrl ?? string.Empty,
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = doc.Timeline?.FirstDiscoveredAt ?? DateTime.UtcNow,
                LastDownloadedAt = doc.Timeline?.LastDownloadedAt,
            },
        };
    }

    // Minimal JSON projection shapes for catalog deserialization — only the
    // fields the seeder actually reads. STJ snake_case + case-insensitive
    // tolerates the catalog's existing property naming.
    private sealed class CatalogJson
    {
        public List<CatalogDocumentJson> Documents { get; init; } = [];
    }

    private sealed class CatalogDocumentJson
    {
        public string DocumentId { get; init; } = string.Empty;
        public CatalogSourceJson? Source { get; init; }
        public CatalogClassificationJson? Classification { get; init; }
        public CatalogGameJson? Game { get; init; }
        public CatalogTimelineJson? Timeline { get; init; }
    }

    private sealed class CatalogSourceJson
    {
        public string? DiscoveryUrl { get; init; }
        public string? DiscoveryContext { get; init; }
        public string? FileUrl { get; init; }
        public string? LinkText { get; init; }
        public string? SourceType { get; init; }
        public DateTime ScrapedAt { get; init; }
    }

    private sealed class CatalogClassificationJson
    {
        public string? DocumentType { get; init; }
        public string? FileFormat { get; init; }
    }

    private sealed class CatalogGameJson
    {
        public string Title { get; init; } = string.Empty;
        public string? Slug { get; init; }
        public string? Edition { get; init; }
        public string? GamePageUrl { get; init; }
    }

    private sealed class CatalogTimelineJson
    {
        public DateTime FirstDiscoveredAt { get; init; }
        public DateTime? LastDownloadedAt { get; init; }
    }
}
