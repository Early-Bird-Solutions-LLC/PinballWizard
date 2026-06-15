using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Infrastructure.Catalog;

// Configuration for the catalog_stats change-feed projection consumer.
// Bound from "Catalog:Stats" — a separate section from "Rag:ChangeFeed"
// so the two consumers can be independently tuned without collision.
//
// The processor runs against the same `scraped_documents` source container
// as the RAG consumer but writes to `catalog_stats` (not `rag_leases`).
// KEDA does NOT need to watch this consumer's leases — the catalog_stats
// projection is a lightweight denormalization, not a scale-trigger signal.
public sealed class CatalogStatsProjectionOptions
{
    public const string SectionName = "Catalog:Stats";

    [Required]
    public string SourceContainerName { get; init; } = "scraped_documents";

    [Required]
    public string LeaseContainerName { get; init; } = "catalog_stats_leases";

    [Required]
    public string ProcessorName { get; init; } = "catalog-stats";

    // Pin to a fixed string in multi-replica deploys to prevent lease-split
    // (two replicas claiming disjoint lease ranges). For strict correctness,
    // the Container App should run this consumer at replica count = 1.
    // Null → Environment.MachineName (same default as CosmosChangeFeedHostedService).
    public string? InstanceName { get; init; }

    // True = start from the beginning of scraped_documents history on first
    // lease grant, so an initial deploy populates catalog_stats from existing
    // data. Subsequent restarts always resume from the stored checkpoint.
    public bool StartFromBeginning { get; init; } = true;
}
