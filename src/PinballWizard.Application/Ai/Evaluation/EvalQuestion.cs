using System.Text.Json.Serialization;

namespace PinballWizard.Application.Ai.Evaluation;

// One ground-truth row from data/eval/wizard.v1.jsonl per ADR-0016.
// Hand-curated against real OPDB machine titles already in the deployed
// Cosmos catalog so each grounded question can cite a stable mch_<id>;
// out-of-scope rows have AcceptableRefusal=true and an empty expected
// citation set.
//
// JSON shape (one object per line in the .jsonl file):
//   {
//     "id": "ev-rules-0001",
//     "question": "What's the wizard mode in Stern Foo Fighters?",
//     "expected_sub_agent": "Rules",
//     "expected_citation_set": ["mch_GRBN-MQR4P"],
//     "acceptable_refusal": false,
//     "notes": "Optional curator note explaining the choice"
//   }
//
// Edition-aware extension (AB#259, edition-scope-model-design §6). The
// single-canonical-id model above REWARDED collapsing every Godzilla
// edition to one id — the exact failure the edition-aware linker fixes.
// The new fields let a curator encode the R1/R2/R3 ground truth:
//
//   - AcceptableCitationSets: an ANY-OF list of acceptable citation
//     sets. A predicted set that matches (per the citation evaluator's
//     set semantics) ANY listed set scores correct. Models R1's "either
//     base is fine" and edition-subset's "these two bases together".
//     When present it supersedes ExpectedCitationSet for any-of matching;
//     ExpectedCitationSet is retained for back-compat (old rows) and as
//     the recall denominator when AcceptableCitationSets is absent.
//   - FranchiseWideOk: when true, a franchise-wide document cited for any
//     edition is acceptable (a rulesheet/feature-matrix answers the same
//     for all editions). LIMITATION (see CitationPrecision/RecallEvaluator):
//     today Citation carries no edition_scope, so the evaluator cannot
//     verify a cited chunk IS franchise-wide; the flag is plumbed as the
//     row-level intent and consumed where reachable. The per-citation
//     scope check unlocks when Citation gains edition_scope (design §4).
//   - ExpectedOutcome: the curator's intended answer SHAPE —
//       "grounded"               (default; cite the right base[s])
//       "answered_all_editions"  (R2; one response attributing each edition)
//       "honest_substitution"    (R3; disclose the named edition is absent,
//                                 then cite the substitute)
//   - RequiredEditions: for ExpectedOutcome="answered_all_editions", the
//     edition labels (e.g. ["Pro","Premium/LE"]) that must each appear
//     attributed in the answer text.
public sealed record EvalQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("expected_sub_agent")] string ExpectedSubAgent,
    [property: JsonPropertyName("expected_citation_set")] IReadOnlyList<string> ExpectedCitationSet,
    [property: JsonPropertyName("acceptable_refusal")] bool AcceptableRefusal,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("acceptable_citation_sets")] IReadOnlyList<IReadOnlyList<string>>? AcceptableCitationSets = null,
    // NOTE: FranchiseWideOk is intentionally unconsumed by the runtime
    // scorer until Citation gains edition_scope (design §4). It is the
    // row-level curator intent and is pinned by a parser round-trip test
    // (EvalQuestionParserTests.Parse_EditionAwareFields_RoundTrip) so it
    // is not mistaken for dead code and removed.
    [property: JsonPropertyName("franchise_wide_ok")] bool FranchiseWideOk = false,
    [property: JsonPropertyName("expected_outcome")] string ExpectedOutcome = "grounded",
    [property: JsonPropertyName("required_editions")] IReadOnlyList<string>? RequiredEditions = null,
    // RefusalRequired (AB#259 metric-hygiene fix, 2026-06-10): refusal
    // expectations are three-state, split across two flags —
    //
    //   refusal_required=true                      → the agent MUST refuse
    //     (genuinely out-of-scope: weather, car repair, shipping quotes).
    //     Answering scores refusal_correctness=0. expected_citation_set
    //     must be empty (the parser enforces it).
    //   acceptable_refusal=true (and not required) → EITHER behavior is
    //     correct (content-gap rows: the corpus may not ground an answer,
    //     but a correct grounded answer is equally fine). The row carries
    //     NO refusal signal — refusal_correctness is null and excluded
    //     from the aggregate. expected_citation_set holds the answer-path
    //     ground truth, graded only when the agent answers.
    //   both false                                 → the agent MUST answer;
    //     refusing scores refusal_correctness=0.
    //
    // refusal_required=true implies acceptable_refusal=true (the parser
    // rejects the contradiction) so pre-three-state readers of the .jsonl
    // still see refusal rows flagged.
    [property: JsonPropertyName("refusal_required")] bool RefusalRequired = false,
    // AcceptableSubAgents (AB#259): an optional list of predicted sub-agent
    // names that score as correct in addition to expected_sub_agent. When
    // absent the evaluator uses exact-match against expected_sub_agent —
    // the pre-AB#259 default behavior is fully preserved.
    //
    // Canonical use case: questions whose answer is plainly available from
    // OPDB machine data (theme, manufacturer, editions, MSRP-from-record).
    // The Wizard can answer these directly via getMachineByTitle without
    // dispatching a sub-agent; that is a CORRECT, EFFICIENT path. Annotate
    // those rows with acceptable_sub_agents=["Wizard"] so the evaluator
    // does not score an efficient direct answer as a routing failure.
    //
    // Do NOT annotate questions that require corpus retrieval (rules details,
    // repair procedures, service bulletins) — a direct Wizard answer there
    // IS a routing miss and must stay scored as such.
    [property: JsonPropertyName("acceptable_sub_agents")] IReadOnlyList<string>? AcceptableSubAgents = null,
    // Hard-eval slice/source/first_stage_rank fields (reranker-sensitive
    // hard set). Slice tags the question category; Source tags the
    // confusability pattern; FirstStageRank is the BM25/vector rank of
    // the correct document before reranking (measures reranker lift).
    // All three are optional so existing wizard.v2.jsonl rows parse unchanged.
    [property: JsonPropertyName("slice")] string? Slice = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("first_stage_rank")] int? FirstStageRank = null,
    // MachineId (issue #719): when set, the question explicitly names a machine
    // and the MachineIdCoverageEvaluator applies — every searchCorpus call in
    // the tool-call trace must carry a non-null machineId argument matching this
    // OPDB ID. Absent on questions that don't name a machine (out-of-scope rows,
    // cross-machine comparisons, manufacturer-level questions) so the metric is
    // undefined and excluded from the aggregate denominator for those rows.
    [property: JsonPropertyName("machine_id")] string? MachineId = null);
