using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Azure AI Search service backing Phase 4 RAG
// retrieval (ADR-0021 — index schema). The smoke probe (W1-4) verifies
// the service endpoint is reachable + AAD auth works; subsequent Wave 2
// items create the `pinwiz-rag-v1` index, populate it via the Cosmos
// Change Feed Function (W3-2), and consume it via IRagRetriever (W3-3).
//
// Sectioning convention matches AiFoundryOptions / OpdbOptions /
// PinballMapOptions / CosmosOptions: SectionName + per-key constants for
// presence-checking from gating code.
public sealed class AiSearchOptions
{
    public const string SectionName = "AiSearch";

    public const string EndpointKey = $"{SectionName}:{nameof(Endpoint)}";

    // The Azure AI Search service endpoint URL, e.g.
    //   https://pinwiz-search-dev-XXXX.search.windows.net
    // Provisioned by the Phase 2 Bicep block when deployAiSearch=true (the
    // W1-4 flip). The smoke probe validates this is reachable + AAD auth
    // succeeds; the data-plane SDK (Azure.Search.Documents) wraps it for
    // index management + query operations.
    [Url]
    public string Endpoint { get; set; } = string.Empty;

    // Default index name for Phase 4 RAG. Per ADR-0021's versioning
    // strategy, schema-breaking changes ship as `pinwiz-rag-v2` (etc.) with
    // dual-read during cutover. The smoke probe reports this as the
    // expected index name but does NOT require it to exist (W2-3 creates
    // the index after the embedding pipeline lands).
    public string IndexName { get; set; } = "pinwiz-rag-v1";
}
