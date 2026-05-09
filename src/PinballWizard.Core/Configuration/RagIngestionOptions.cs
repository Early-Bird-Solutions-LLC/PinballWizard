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

    // Per build-spec § Phase 4: corpus expansion is Phase 4.5; Phase 4
    // ships the full architecture against a curated 7-machine subset.
    // The orchestrator's first filter is membership in this list.
    // Empty list disables ingestion (every document is filtered as
    // NotInCuratedSubset). The Phase 4.5 expansion PR removes the
    // filter clause entirely, NOT this option, so production never
    // sees an unbounded curated subset config.
    [Required]
    public IReadOnlyList<string> CuratedSubsetMachineIds { get; init; } = [];

    // Document types accepted by the pipeline. Manuals + service
    // bulletins for Phase 4. The metadata-card path (W3-1) flows
    // through a sibling pipeline keyed off `MachineDocument` Change
    // Feed, NOT this list — `MetadataCard` here would be a category
    // error. Anything outside this list returns `Skipped_DocumentTypeFiltered`.
    [Required]
    [MinLength(1)]
    public IReadOnlyList<DocumentType> AcceptedDocumentTypes { get; init; } =
        [DocumentType.Manual, DocumentType.ServiceBulletin];

    // Optional reconciliation pass on worker startup: sample N random
    // `rag_index_state` rows and verify AI Search has matching chunks.
    // Off by default — fast cold-start matters; enable manually after
    // a known purge per the operator runbook. When on, the worker
    // emits `pinwiz.rag.changefeed_reconcile_*` instruments (W3-2
    // Phase C / observability PR) so the cost is observable.
    public bool ReconcileOnStartup { get; init; }

    // Per-document retry budget. Tracked on `IIndexState` rows; once a
    // document accumulates this many failures, the next failure
    // dead-letters the document AND short-circuits future re-deliveries
    // until an operator clears the dead-letter manually. Defaults to 3
    // — enough to ride out transient AI Search 5xx without infinite-
    // looping on a structurally-poison document.
    [Range(1, 10)]
    public int MaxFailuresPerDocument { get; init; } = 3;
}
