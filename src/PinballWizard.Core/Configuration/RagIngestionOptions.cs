using System.ComponentModel.DataAnnotations;
using PinballWizard.Core.Models;

namespace PinballWizard.Core.Configuration;

// Configuration for the W3-2 RAG ingestion pipeline. Bound from the
// `Rag:Ingestion` configuration section at startup. The W3-2 worker
// process (Container App) reads this; the AI Search retriever does
// not — see `AiSearchOptions` for the read side.
public sealed class RagIngestionOptions
{
    public const string SectionName = "Rag:Ingestion";

    // Document types accepted by the pipeline. Manuals + service bulletins +
    // feature matrices (the per-edition feature table — gameplay-relevant).
    // The metadata-card / game-overview synthesis paths flow through Cli sync
    // verbs, NOT this list. Anything outside returns Skipped_DocumentTypeFiltered.
    [Required]
    [MinLength(1)]
    public List<DocumentType> AcceptedDocumentTypes { get; set; } =
        [DocumentType.Manual, DocumentType.ServiceBulletin, DocumentType.FeatureMatrix, DocumentType.Rulesheet];

    // Optional reconciliation pass on worker startup: sample N rows
    // from `rag_index_state` and verify AI Search has matching chunks.
    // Off by default — fast cold-start matters; enable manually after
    // a known purge per the operator runbook, or via Bicep param for a
    // canary deploy. When on, the worker emits the
    // `pinwiz.rag.changefeed_reconcile_*` instruments so operators can
    // see what the pass found.
    //
    // Failure posture: the reconcile runs ASYNC after the change-feed
    // processor starts so worker boot isn't blocked. A reconcile
    // exception is logged at warning and the worker continues serving
    // the change feed normally — a stale-but-trustworthy index is
    // operationally better than a refusing-to-start worker.
    public bool ReconcileOnStartup { get; init; }

    // Number of `rag_index_state` rows to inspect during the
    // reconcile-on-startup pass. The reconciler reads the most-recently-
    // recorded N rows (ORDER BY recorded_utc DESC) — recency-biased
    // sampling because recent ingests are the documents most likely to
    // have hit a transient AI Search outage that would surface as
    // drift. Default 50 is fast (under one second on Cosmos serverless).
    // Range 1-1000 — larger than 1000 starts to burn meaningful Cosmos
    // RU on every cold start.
    [Range(1, 1000)]
    public int ReconcileSampleSize { get; init; } = 50;

    // Per-document retry budget. Tracked on `IIndexState` rows; once a
    // document accumulates this many failures, the next failure
    // dead-letters the document AND short-circuits future re-deliveries
    // until an operator clears the dead-letter manually. Defaults to 3
    // — enough to ride out transient AI Search 5xx without infinite-
    // looping on a structurally-poison document.
    [Range(1, 10)]
    public int MaxFailuresPerDocument { get; init; } = 3;

    // Number of documents the backfill service processes concurrently.
    // Purely internal Azure calls — no politeness throttle applies here.
    // Each document already fans out internally (EmbeddingMaxConcurrency
    // + IndexUploadConcurrency); this multiplies that fan-out across
    // documents. 4 is empirically safe at 350k TPM with the embedder's
    // per-batch retry-with-backoff absorbing any momentary 429 bursts.
    [Range(1, 32)]
    public int BackfillConcurrency { get; init; } = 4;
}
