using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using PinballWizard.Domain.Abstractions;
using PinballWizard.Domain.Models;
using PinballWizard.Processor.Chunking;
using PinballWizard.Processor.Indexing;

namespace PinballWizard.Processor.Pipeline;

public sealed class PipelineOrchestrator
{
    private readonly IEnumerable<IContentExtractor> _extractors;
    private readonly SlidingWindowChunker _slidingWindowChunker;
    private readonly SectionAwareChunker _sectionAwareChunker;
    private readonly WholeDocumentChunker _wholeDocumentChunker;
    private readonly IndexBatchPublisher _publisher;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IEnumerable<IContentExtractor> extractors,
        SlidingWindowChunker slidingWindowChunker,
        SectionAwareChunker sectionAwareChunker,
        WholeDocumentChunker wholeDocumentChunker,
        IndexBatchPublisher publisher,
        BlobServiceClient blobServiceClient,
        ILogger<PipelineOrchestrator> logger)
    {
        _extractors = extractors;
        _slidingWindowChunker = slidingWindowChunker;
        _sectionAwareChunker = sectionAwareChunker;
        _wholeDocumentChunker = wholeDocumentChunker;
        _publisher = publisher;
        _blobServiceClient = blobServiceClient;
        _logger = logger;
    }

    public async Task ProcessBlobAsync(string containerName, string blobName, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing blob: {Container}/{Blob}", containerName, blobName);

        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
        var mimeType = properties.Value.ContentType ?? "application/octet-stream";
        var extension = Path.GetExtension(blobName).ToLowerInvariant();

        // Find the right extractor
        var extractor = _extractors.FirstOrDefault(e => e.CanExtract(mimeType, extension));
        if (extractor is null)
        {
            _logger.LogWarning("No extractor found for {MimeType} / {Extension}. Skipping blob {Blob}", mimeType, extension, blobName);
            return;
        }

        _logger.LogInformation("Using extractor '{Extractor}' for {Blob}", extractor.Name, blobName);

        // Download and extract
        using var downloadStream = await blobClient.OpenReadAsync(cancellationToken: ct);
        var extractionResult = await extractor.ExtractAsync(downloadStream, blobName, ct);

        if (string.IsNullOrWhiteSpace(extractionResult.Text))
        {
            _logger.LogWarning("Extraction produced no text for {Blob}. Skipping.", blobName);
            return;
        }

        // Select chunking strategy
        var chunker = SelectChunker(extractionResult, mimeType, extension);
        _logger.LogInformation("Using chunker '{Chunker}' for {Blob}", chunker.Name, blobName);

        var textChunks = chunker.Chunk(extractionResult);

        if (textChunks.Count == 0)
        {
            _logger.LogWarning("Chunking produced no chunks for {Blob}. Skipping.", blobName);
            return;
        }

        // Parse document metadata from blob metadata if available
        var metadata = properties.Value.Metadata;
        var documentId = metadata.TryGetValue("documentId", out var docId) ? docId : GenerateDocumentId(blobName);
        var gameSlug = GetMetadata(metadata, "gameSlug");
        var gameTitle = GetMetadata(metadata, "gameTitle");
        var manufacturer = GetMetadata(metadata, "manufacturer");
        var documentType = ParseEnum<DocumentType>(GetMetadata(metadata, "documentType"));
        var sourceType = ParseEnum<SourceType>(GetMetadata(metadata, "sourceType"));
        var sourceUrl = GetMetadata(metadata, "sourceUrl");
        var sourceName = GetMetadata(metadata, "sourceName");
        var contentCategories = ParseCategories(GetMetadata(metadata, "contentCategories"));

        // Convert TextChunks to SearchChunks
        var searchChunks = textChunks.Select((chunk, index) => new SearchChunk
        {
            ChunkId = $"{documentId}_chunk_{index:D4}",
            Content = chunk.Content,
            ParentDocId = documentId,
            GameSlug = gameSlug,
            GameTitle = gameTitle,
            Manufacturer = manufacturer,
            DocumentType = documentType,
            SourceType = sourceType,
            SourceUrl = sourceUrl,
            SourceName = sourceName,
            SectionPath = chunk.SectionPath,
            PageNumber = chunk.PageNumber,
            ContentCategories = contentCategories,
            LastUpdated = DateTimeOffset.UtcNow
        }).ToList();

        _logger.LogInformation("Publishing {Count} search chunks for {Blob}", searchChunks.Count, blobName);
        await _publisher.PublishAsync(searchChunks, ct);
    }

    internal IChunkingStrategy SelectChunker(ExtractionResult result, string mimeType, string extension)
    {
        // Short documents use whole document chunker
        var tokenCount = Chunking.TokenHelper.CountTokens(result.Text);
        if (tokenCount <= 2048)
            return _wholeDocumentChunker;

        // Documents with clear section structure use section-aware chunker
        var hasSections = result.Sections.Any(s => s.Heading is not null && s.Level > 0);
        if (hasSections)
            return _sectionAwareChunker;

        // Default: sliding window
        return _slidingWindowChunker;
    }

    private static string GenerateDocumentId(string blobName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(blobName.ToLowerInvariant()));
        return $"doc_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static T ParseEnum<T>(string? value) where T : struct, Enum
    {
        if (value is not null && Enum.TryParse<T>(value, ignoreCase: true, out var result))
            return result;
        return default;
    }

    private static List<ContentCategory> ParseCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<ContentCategory>(s, ignoreCase: true, out var cat) ? cat : (ContentCategory?)null)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();
    }

    private static string? GetMetadata(IDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? value : null;
}
