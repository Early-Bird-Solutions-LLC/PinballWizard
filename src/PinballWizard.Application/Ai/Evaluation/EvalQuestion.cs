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
    [property: JsonPropertyName("acceptable_sub_agents")] IReadOnlyList<string>? AcceptableSubAgents = null);
