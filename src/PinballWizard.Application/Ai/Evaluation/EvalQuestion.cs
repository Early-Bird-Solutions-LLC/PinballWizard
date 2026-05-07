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
public sealed record EvalQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("expected_sub_agent")] string ExpectedSubAgent,
    [property: JsonPropertyName("expected_citation_set")] IReadOnlyList<string> ExpectedCitationSet,
    [property: JsonPropertyName("acceptable_refusal")] bool AcceptableRefusal,
    [property: JsonPropertyName("notes")] string? Notes = null);
