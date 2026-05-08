namespace PinballWizard.Application.Ai.Retrieval;

// Per-call options for `IRagRetriever.RetrieveAsync`. Defaults match
// ADR-0021 § Search defaults and ADR-0019 § Retrieval expectations:
// hybrid (vector + keyword + semantic) retrieval, top 10 chunks.
// Sub-agent-aware filtering is the showcase of this options surface —
// `MachineId` constrains retrieval to a single machine (the dominant
// pattern for Repair / Rules queries that already know which machine
// the user is asking about), `DocumentType` constrains to manuals /
// service bulletins / metadata cards independently.
//
// `MinimumScore` is post-filter: the retriever drops results whose
// re-ranker score falls below it before returning. The default of
// `0.0` returns every result the search engine produced; H3
// calibration (ADR-0023) may raise it as the citation-required
// guardrail's first stage.
public sealed record RetrievalOptions(
    int TopK = 10,
    string? MachineId = null,
    string? DocumentType = null,
    string? Manufacturer = null,
    double MinimumScore = 0.0);
