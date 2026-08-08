using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Domain.Models;

namespace PinballWizard.Processor.Indexing;

public sealed class IndexBatchPublisher
{
    private readonly SearchClient _searchClient;
    private readonly ProcessorSettings _settings;
    private readonly ILogger<IndexBatchPublisher> _logger;

    public IndexBatchPublisher(
        SearchClient searchClient,
        IOptions<ProcessorSettings> settings,
        ILogger<IndexBatchPublisher> logger)
    {
        _searchClient = searchClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task PublishAsync(IReadOnlyList<SearchChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        _logger.LogInformation("Publishing {Count} chunks to search index", chunks.Count);

        var batchSize = _settings.IndexBatchSize;

        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            await PublishBatchWithRetryAsync(batch, ct);
        }

        _logger.LogInformation("Successfully published all {Count} chunks", chunks.Count);
    }

    private async Task PublishBatchWithRetryAsync(List<SearchChunk> batch, CancellationToken ct)
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var actions = batch.Select(chunk => IndexDocumentsAction.Upload(chunk));
                var indexBatch = IndexDocumentsBatch.Create(actions.ToArray());
                await _searchClient.IndexDocumentsAsync(indexBatch, cancellationToken: ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 503 || ex.Status == 429)
            {
                if (attempt == maxRetries)
                {
                    _logger.LogError(ex, "Failed to publish batch after {MaxRetries} attempts", maxRetries);
                    throw;
                }

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning("Transient error publishing batch (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s",
                    attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }
}
