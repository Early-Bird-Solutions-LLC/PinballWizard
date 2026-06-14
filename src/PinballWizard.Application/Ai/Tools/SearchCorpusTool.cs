using System.ComponentModel;
using System.Diagnostics;
using Azure;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Hosting;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Observability;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to all four agents per ADR-0014.
// Sibling to MachineGroundingTool — the latter grounds via OPDB on
// machine identity; this one grounds via AI Search RAG retrieval on
// document chunks (manuals, service bulletins, metadata cards) per
// ADR-0021 + ADR-0022.
//
// The Microsoft Agent Framework's AIFunctionFactory.Create wraps the
// SearchCorpusAsync method into an AIFunction with auto-generated
// JSON-Schema (from [Description] attributes on the method + its
// arguments). Sub-agent prompts (Repair / Rules / Valuation) and the
// Wizard prompt teach the model when to call it.
//
// The `= null` / `= default` parameter defaults are load-bearing, not
// style: the schema generator puts every parameter WITHOUT a C# default
// value in the `required` array regardless of nullability. Without them
// the model legitimately omits an "Optional:" argument, and the binder
// throws before the tool body runs ("Error: Function failed." — the
// ev-repair-0008 hard error in eval wizard.20260610T160646Z).
// SearchCorpusToolContractTests pins the required-array shape.
//
// Failure posture (ADR-0023): a transport-level exception from the
// retriever is caught and surfaced as an EMPTY result rather than
// rethrown. The empty result naturally drives the citation-required
// guardrail (W4-3) to refuse with category=NoCitation rather than
// fabricate. Re-throwing here would let the model loop on transient
// failures (Microsoft Agent Framework retries function calls), so we
// fail closed at the tool boundary and let the orchestrator's
// guardrail handle the user-visible refusal.
//
// Wave 2 PR-D2: typed catch arms replace the prior single catch-all.
// Each arm calls MarkAndCountSearchUnavailable with a distinct `reason`
// tag so dashboards can distinguish timeout-induced empty results from
// auth failures from generic 5xx (different alert, different remediation).
// IDegradationContext.Mark() is called so AiRouter can fold the signal
// into WizardAnswer.Degradation per ADR-0026 § 9.
public sealed class SearchCorpusTool
{
    // Server-side TopK ceiling. The model can request up to this; a
    // hostile prompt asking for `topK=1000` is clamped here so it
    // can't pull the entire index per call. 20 is empirically a
    // generous ceiling for chunk-grounded answers; the citation
    // surface starts to feel diluted past ~10.
    internal const int TopKCeiling = 20;
    internal const int TopKDefault = 8;

    // Tool tag value emitted on `pinwiz.ai.tool_duration_ms` and
    // `pinwiz.ai.tool_errors_total`. Matches the JSON-Schema function
    // name the Microsoft Agent Framework derives from this method, so
    // dashboards, prompts, and the MachineGroundingTool sibling all
    // agree on the label.
    internal const string ToolTagValue = "searchCorpus";

    private readonly IRagRetriever _retriever;
    private readonly IDegradationContext _degradationContext;
    private readonly ILogger<SearchCorpusTool> _logger;
    // Optional by design (PR retrieval-runtime-keys): hosts without the
    // admin_settings container (standalone CLI, unit fixtures) run on
    // RetrievalOptions defaults — identical behavior to no stored overrides.
    // Mirrors the AiRouter optional-IRuntimeSettings convention (PR-B1).
    private readonly IRuntimeSettings? _runtimeSettings;
    // Optional by design (fix/citation-metadata-channel): the sink is
    // request-scoped but SearchCorpusTool is registered as Singleton in
    // the DI container. The tool resolves the sink from the service
    // locator (IServiceProvider) at call time when needed, OR — in the
    // common web-host path — the caller wires the scoped instance here
    // via constructor injection from an IServiceScope. Unit fixtures that
    // construct SearchCorpusTool directly pass null; when null, recording
    // is simply skipped and the typed C# path (used in tests) still works.
    private readonly IRetrievalCitationMetadataSink? _metadataSink;

    public SearchCorpusTool(
        IRagRetriever retriever,
        IDegradationContext degradationContext,
        ILogger<SearchCorpusTool> logger,
        IRuntimeSettings? runtimeSettings = null,
        IRetrievalCitationMetadataSink? metadataSink = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(degradationContext);
        ArgumentNullException.ThrowIfNull(logger);
        _retriever = retriever;
        _degradationContext = degradationContext;
        _logger = logger;
        _runtimeSettings = runtimeSettings;
        _metadataSink = metadataSink;
    }

    [Description("Search the indexed pinball-machine corpus (manuals, service bulletins, metadata cards) for chunks relevant to a question. Returns up to topK page-anchored chunks with document URLs you must cite. Returns an empty list if nothing matches — when empty, do not fabricate; refuse instead.")]
    public async Task<SearchCorpusResult> SearchCorpusAsync(
        [Description("The natural-language question or query to search the corpus with. Pass the user's question through unchanged unless you need to scope it to a specific machine or document type.")] string query,
        [Description("Optional: constrain results to a specific machine by OPDB ID (for example: 'GRBNN-MQERZ'). Use this when the user has already identified the machine via getMachineByTitle and you want manual/bulletin chunks for that specific machine.")] string? machineId = null,
        [Description("Optional: constrain to a document type. Allowed values: 'manual', 'service_bulletin', 'metadata_card'. Omit for unfiltered.")] string? documentType = null,
        [Description("Optional: maximum number of chunks to return. Default 8; max 20.")] int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // Mirror MachineGroundingTool's whitespace-input posture:
            // return empty rather than throwing, so a confused agent
            // loop can't take down the orchestrator. The empty result
            // hits the citation-required guardrail naturally.
            _logger.LogDebug("SearchCorpusTool: empty query, returning empty result.");
            return new SearchCorpusResult([]);
        }

        // Outer Stopwatch + try/finally measures per-tool latency
        // including the catch path (ADR-0023 fail-closed-on-transport-
        // error path). Failure latency is operationally meaningful —
        // a slow-then-empty response and a fast-then-empty response
        // need different alerts. The retrieval-only timing lives on
        // `pinwiz.rag.retrieval_duration_ms` separately so dashboards
        // can subtract retrieval latency from tool latency to surface
        // tool-side overhead drift.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // One snapshot per tool invocation (PR retrieval-runtime-keys).
            // Mirrors the one-snapshot-per-ask pattern AiRouter uses for
            // confidence_threshold / cost_ceiling — a single retrieval call
            // is internally consistent even if an admin saves mid-stream.
            // When IRuntimeSettings is absent the record-parameter defaults
            // apply (TopK=10, MinimumScore=0.0 per ADR-0021 § Search defaults).
            var rtTopK = TopKDefault;
            var rtMinimumScore = new RetrievalOptions().MinimumScore;
            if (_runtimeSettings is not null)
            {
                var snapshot = await _runtimeSettings
                    .GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
                rtTopK = snapshot.RetrievalTopK;
                rtMinimumScore = snapshot.RetrievalMinimumScore;
            }

            // The model-requested topK (clamped to TopKCeiling) wins when
            // supplied; the runtime-configured default applies when the model
            // omits the argument (null / ≤ 0). This preserves the sub-agent
            // ability to request fewer chunks (e.g. topK=3 for a tight Repair
            // query) while letting the admin tune the unspecified baseline.
            var clampedTopK = ClampTopK(topK, rtTopK);
            var options = new RetrievalOptions(
                TopK: clampedTopK,
                MachineId: NormalizeOptional(machineId),
                DocumentType: NormalizeDocumentType(documentType),
                MinimumScore: rtMinimumScore);

            IReadOnlyList<RetrievedChunk> chunks;
            try
            {
                chunks = await _retriever
                    .RetrieveAsync(query, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (IsTimeoutCancellation(oce))
            {
                // SDK-internal timeout (the retriever's own CancellationToken
                // fired, not the caller's). Surface as SearchUnavailable/timeout
                // rather than propagating — the model should not loop on a
                // retriever timeout; fail closed and let NoCitation refuse.
                MarkAndCountSearchUnavailable(
                    "timeout",
                    "AI Search retrieval timed out.",
                    oce,
                    options,
                    query);
                return new SearchCorpusResult([]);
            }
            catch (OperationCanceledException)
            {
                // Caller intent — propagate so the outer request can be
                // cancelled cleanly.
                throw;
            }
            catch (AuthenticationFailedException afe)
            {
                // Azure.Identity auth failure (misconfigured credential,
                // expired MSI token). Surface as SearchUnavailable/auth_failure.
                MarkAndCountSearchUnavailable(
                    "auth_failure",
                    "AI Search authentication failed.",
                    afe,
                    options,
                    query);
                return new SearchCorpusResult([]);
            }
            catch (RequestFailedException rfe) when (rfe.Status is >= 400 and < 500)
            {
                // AI Search 4xx (wrong index name, auth scope mismatch,
                // malformed query). These indicate misconfiguration, not a
                // transient outage. Log at Error so monitoring fires on first
                // occurrence; return empty so the NoCitation guardrail handles
                // the turn gracefully rather than propagating a 500 to the user.
                // Previously these were allowed to propagate unhandled — that was
                // safe when sub-agents called searchCorpus (framework isolated the
                // failure) but breaks the Wizard turn now that searchCorpus is
                // called at the Wizard level before sub-agent dispatch.
                MarkAndCountSearchUnavailable(
                    "http_4xx",
                    $"AI Search returned HTTP {rfe.Status} — likely misconfiguration.",
                    rfe,
                    options,
                    query);
                _logger.LogError(rfe,
                    "SearchCorpusTool: AI Search returned HTTP {Status} — check index name, RBAC assignment, and query syntax. query={Query}",
                    rfe.Status,
                    query);
                return new SearchCorpusResult([]);
            }
            catch (RequestFailedException rfe) when (rfe.Status >= 500)
            {
                // AI Search 5xx (service outage, gateway timeout).
                MarkAndCountSearchUnavailable(
                    "http_5xx",
                    $"AI Search returned HTTP {rfe.Status}.",
                    rfe,
                    options,
                    query);
                return new SearchCorpusResult([]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Generic transport-level failures (network partition, DNS,
                // unexpected SDK wrapping). Categorized as "other" so the
                // dashboard can distinguish this bucket from the typed arms.
                MarkAndCountSearchUnavailable(
                    "other",
                    "AI Search retrieval failed with an unexpected error.",
                    ex,
                    options,
                    query);
                return new SearchCorpusResult([]);
            }

            var hits = new List<SearchCorpusHit>(capacity: chunks.Count);
            foreach (var chunk in chunks)
            {
                hits.Add(new SearchCorpusHit(
                    MachineId: chunk.MachineId,
                    MachineTitle: chunk.MachineTitle,
                    DocumentId: chunk.DocumentId,
                    DocumentUrl: chunk.DocumentUrl,
                    DocumentType: chunk.DocumentType,
                    PageStart: chunk.PageStart,
                    PageEnd: chunk.PageEnd,
                    SectionHeading: chunk.SectionHeading,
                    Content: chunk.Content,
                    // Model-visible (Task 7, AB#259): the model reads each
                    // chunk's edition_scope to decide R1/R2/R3 and edition to
                    // attribute per-edition answers. Threaded from the
                    // retrieved chunk's index fields.
                    Edition: chunk.Edition,
                    EditionScope: chunk.EditionScope)
                {
                    // Score is threaded through [JsonIgnore] so the model
                    // does not see it; the citation extractor reads it to
                    // populate Citation.RelevanceScore (PR-C2). RetrievedChunk
                    // guarantees a non-null double (AiSearchRagRetriever
                    // resolves reranker → BM25 → 0.0 before constructing it),
                    // so the cast to double? is always value-present.
                    Score = chunk.Score,
                    // LastScrapedUtc is threaded through [JsonIgnore] (PR-C3)
                    // so the model never sees it. The citation extractor reads
                    // it to populate Citation.LastScrapedUtc for the frontend
                    // freshness badge (ADR-0026 § 4). Null for chunks indexed
                    // before PR-C3 — the frontend badge is conditionally rendered.
                    LastScrapedUtc = chunk.LastScrapedUtc,
                });

                // UI-metadata side channel (fix/citation-metadata-channel):
                // publish Score + LastScrapedUtc to the request-scoped sink so
                // ToolTraceCitationExtractor can enrich citations even though
                // these fields are [JsonIgnore]'d from the model-facing JSON.
                // First-write-wins per URL (sink semantics) matches the citation
                // dedup — the first/highest-ranked hit per document wins.
                // Skip when DocumentUrl is absent (defensive; indexer bug guard).
                if (_metadataSink is not null && !string.IsNullOrWhiteSpace(chunk.DocumentUrl))
                {
                    _metadataSink.Record(
                        chunk.DocumentUrl,
                        new RetrievalCitationMetadata(chunk.LastScrapedUtc, chunk.Score));
                }
            }

            _logger.LogDebug(
                "SearchCorpusTool: query length={QueryLength} hits={HitCount} machineId={MachineId} documentType={DocumentType} topK={TopK}",
                query.Length,
                hits.Count,
                options.MachineId ?? "(any)",
                options.DocumentType ?? "(any)",
                options.TopK);

            return new SearchCorpusResult(hits);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", ToolTagValue));
        }
    }

    // When `requested` is null or ≤ 0 (model omitted the argument), the
    // effective default is `runtimeDefault` — which is the runtime-mutable
    // rag.retrieval_top_k value when IRuntimeSettings is wired, or
    // TopKDefault (8) otherwise. The ceiling is always TopKCeiling (20).
    internal static int ClampTopK(int? requested, int runtimeDefault = TopKDefault)
    {
        if (requested is null || requested <= 0)
        {
            return Math.Min(runtimeDefault, TopKCeiling);
        }
        return Math.Min(requested.Value, TopKCeiling);
    }

    // Returns true when the OperationCanceledException was raised by an
    // SDK-internal timeout rather than by the caller's CancellationToken.
    // The heuristic: a non-default (non-None) token that is already
    // cancelled indicates the SDK passed its own internal token; the
    // caller's token would typically be cancelled only when the caller
    // intentionally cancels (in which case we propagate above). This
    // mirrors the convention used in Azure.Core SDK timeout wrapping.
    internal static bool IsTimeoutCancellation(OperationCanceledException oce)
        => oce.CancellationToken != CancellationToken.None
           && oce.CancellationToken.IsCancellationRequested;

    // Marks the ambient degradation context, increments the search-unavailable
    // OTel counter, increments the tool-errors counter, and logs a warning.
    // All typed catch arms delegate here so the three side-effects stay
    // synchronized — one place to update if the contract changes.
    private void MarkAndCountSearchUnavailable(
        string reason,
        string detail,
        Exception ex,
        RetrievalOptions options,
        string query)
    {
        _degradationContext.Mark(DegradationMode.SearchUnavailable, detail);

        PinballWizardTelemetry.AiSearchUnavailable.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));

        // AiToolErrors continues to fire so existing alerts and dashboards
        // that rely on tool=searchCorpus are unaffected by the finer
        // reason-tagged counter shipping in this PR.
        PinballWizardTelemetry.AiToolErrors.Add(
            1,
            new KeyValuePair<string, object?>("tool", ToolTagValue));

        _logger.LogWarning(
            ex,
            "SearchCorpusTool: retriever threw ({Reason}) — returning empty result. query length={QueryLength} machineId={MachineId} documentType={DocumentType} topK={TopK}",
            reason,
            query.Length,
            options.MachineId ?? "(any)",
            options.DocumentType ?? "(any)",
            options.TopK);
    }

    // Normalize "" / "  " to null so the retriever's filter builder
    // doesn't emit an `eq ''` clause that excludes every legitimate
    // value. Matches AiSearchRagRetriever.BuildFilter's empty-as-absent
    // behavior — both ends of the contract agree on what "not set"
    // looks like.
    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    // The index stores document_type as the DocumentType enum's .ToString()
    // representation (e.g. "Manual", "ServiceBulletin", "MetadataCard").
    // The Wizard prompt and SearchCorpusTool [Description] expose lowercase
    // snake_case aliases ("manual", "service_bulletin", "metadata_card") for
    // readability. This method maps prompt-friendly values to the indexed form
    // so the OData filter matches the stored data.
    //
    // Unknown values are passed through unchanged so the retriever's filter
    // returns an empty result (→ NoCitation refuse) rather than silently
    // widening the query by ignoring the constraint.
    internal static string? NormalizeDocumentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "manual" => "Manual",
            "service_bulletin" => "ServiceBulletin",
            "metadata_card" => "MetadataCard",
            _ => value.Trim(),
        };
    }
}