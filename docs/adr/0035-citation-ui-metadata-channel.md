# 0035 — Citation UI-metadata side channel for freshness + relevance score

**Status:** Accepted
**Date:** 2026-06-14

## Context

`SearchCorpusHit.Score` and `SearchCorpusHit.LastScrapedUtc` are decorated
`[JsonIgnore]` (introduced in PR-C2 and PR-C3 respectively). The intent is
correct: the model must not see retrieval internals such as relevance scores
or freshness timestamps — seeing them could lead the model to reason about
retrieval confidence rather than document content.

`ToolTraceCitationExtractor` rebuilds `Citation` objects by reading the
`AgentResponse.Messages` tool-result trace. On the real Foundry path
(`feat/wire-api-ai-runtime`), `AIFunctionFactory.Create` serializes the C#
return value of `SearchCorpusAsync` into JSON before storing it in
`FunctionResultContent.Result`. Because `Score` and `LastScrapedUtc` are
`[JsonIgnore]`, they are absent from that JSON. When the extractor
deserializes `SearchCorpusResult` from the `JsonElement`, those fields
arrive as `null` — every corpus citation on the deployed site showed
"freshness unknown" and no relevance score.

The unit tests (`ToolTraceCitationExtractorTests`) passed because they
construct `SearchCorpusResult` typed objects and hand them to the extractor
directly. The extractor's typed-object arm (`result is SearchCorpusResult`)
reads the C# properties, which do carry the values. The JSON arm (the
production path) was never exercised by a test that checked `LastScrapedUtc`
or `RelevanceScore`. This is the same test-blindness class as the 2026-06-10
citation outage (typed-object tests green; live JSON path broken).

## Decision

UI-only citation metadata (`LastScrapedUtc`, `RelevanceScore`) travels a
request-scoped side channel — `IRetrievalCitationMetadataSink` — rather
than the model-facing tool-result trace.

`SearchCorpusTool` records `(DocumentUrl → RetrievalCitationMetadata)` into
the sink immediately after building each `SearchCorpusHit`, before returning
`SearchCorpusResult` to the agent framework. The sink is keyed by
`DocumentUrl` with first-write-wins semantics, matching the citation
dedup-by-`DocumentUrl` in `ToolTraceCitationExtractor` — the first
(highest-ranked) hit per document wins in both channels.

`ToolTraceCitationExtractor` is injected with the sink (`IRetrievalCitationMetadataSink?`
— optional so tests that construct it without a container continue to work).
In `AddCitationsFromCorpusHits`, it enriches each corpus `Citation` using:

```
RelevanceScore: hit.Score ?? sinkMeta?.RelevanceScore
LastScrapedUtc: hit.LastScrapedUtc ?? sinkMeta?.LastScrapedUtc
```

Typed C# hit values take precedence (non-null on the unit-test / typed-object
path). Sink values are the fallback for the JSON path where `[JsonIgnore]`
strips the fields. `[JsonIgnore]` is NOT removed — the model-invisibility
invariant is preserved.

`IRetrievalCitationMetadataSink` is registered as **Scoped** in the DI
container (one instance per HTTP request / streaming turn), ensuring the
metadata recorded by one retrieval call is available to the extractor in the
same request without leaking across requests.

## Consequences

- Citation cards on the deployed site show correct freshness timestamps and
  relevance scores. The "freshness unknown" bug is resolved.
- The `[JsonIgnore]` model-visibility contract on `SearchCorpusHit.Score`
  and `SearchCorpusHit.LastScrapedUtc` is unchanged. `SearchCorpusHitJsonContractTests`
  continues to pin this invariant.
- **New test requirement:** Tests that exercise the citation-metadata path
  must use the JSON-serialized path (via `JsonSerializer.SerializeToElement`
  with `JsonSerializerDefaults.Web`), not typed objects — the typed path
  bypasses `[JsonIgnore]` and will always pass even if the fix is absent.
  `ToolTraceCitationExtractorTests` now includes a POSITIVE test (sink wired,
  JSON path, values correct) and a NEGATIVE test (no sink, JSON path, values
  null — documents the root cause).
- References:
  - ADR-0022: tool-trace citation extraction (the extractor this PR extends)
  - ADR-0026 §4 + §8: freshness badge and relevance score on the citation surface
