using System.Text.Json;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai.Tools;

// JSON serialization contract test for SearchCorpusHit. Pins the
// invariant that `Score` is NEVER emitted in serialized output —
// the model-doesn't-see-Score guarantee introduced in PR-C2.
//
// The [JsonIgnore] attribute is the load-bearing guard; this test
// detects silent removal of the attribute (e.g., through a naive
// record refactor that recreates the positional parameters without
// copying the property block). The test serializes an instance with
// a non-null Score and asserts the key is absent in the output JSON
// using a case-insensitive search so renaming conventions don't
// accidentally create a backdoor.
//
// This is the same posture used in SternPlaywrightDtoActivatorContractTests:
// test the observable contract (JSON wire shape), not the attribute
// presence, so the pin survives STJ option changes and record rewrites.
public sealed class SearchCorpusHitJsonContractTests
{
    [Fact]
    public void Score_is_JsonIgnored_in_serialized_output()
    {
        // Arrange: a hit with a non-null Score (the value the model must
        // never see in its function-result payload).
        var hit = new SearchCorpusHit(
            MachineId: "GRBE-MJL05",
            MachineTitle: "Godzilla (Premium)",
            DocumentId: "doc_godzilla_manual",
            DocumentUrl: "https://sternpinball.com/godzilla_manual.pdf",
            DocumentType: "manual",
            PageStart: 12,
            PageEnd: 14,
            SectionHeading: "Coil Replacement",
            Content: "Replace the coil…")
        {
            Score = 0.85,
        };

        // Act: serialize as the Foundry function-tool pipeline would
        // (System.Text.Json default options — the same options used by
        // Microsoft.Extensions.AI to serialize FunctionResultContent).
        var json = JsonSerializer.Serialize(hit);

        // Assert: "score" is absent regardless of casing. If [JsonIgnore]
        // were removed, STJ would emit `"Score":0.85` (PascalCase default)
        // which this assertion catches.
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Score_is_readable_as_CSharp_property()
    {
        // Paired assertion: [JsonIgnore] hides Score from JSON but C#
        // code (the citation extractor) can still read the property.
        // This pins the other half of the contract so a future refactor
        // that "fixes" the [JsonIgnore] by removing the Score property
        // entirely would also break here.
        var hit = new SearchCorpusHit(
            MachineId: "GRBE-MJL05",
            MachineTitle: "Godzilla (Premium)",
            DocumentId: "doc_x",
            DocumentUrl: "https://example/doc.pdf",
            DocumentType: "manual",
            PageStart: 1,
            PageEnd: 1,
            SectionHeading: "Intro",
            Content: "…")
        {
            Score = 0.92,
        };

        Assert.Equal(0.92, hit.Score);
    }

    [Fact]
    public void Score_null_is_readable_and_absent_in_json()
    {
        // Null Score path: no score from the semantic re-ranker. The
        // property exists but its value is null. JSON must still omit
        // the key (because [JsonIgnore] applies regardless of value).
        var hit = new SearchCorpusHit(
            MachineId: "GRBE-MJL05",
            MachineTitle: "Godzilla (Premium)",
            DocumentId: "doc_x",
            DocumentUrl: "https://example/doc.pdf",
            DocumentType: "manual",
            PageStart: 1,
            PageEnd: 1,
            SectionHeading: "Intro",
            Content: "…")
        {
            Score = null,
        };

        var json = JsonSerializer.Serialize(hit);

        Assert.Null(hit.Score);
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Other_fields_are_all_present_in_serialized_output()
    {
        // Regression guard: [JsonIgnore] on Score must not accidentally
        // suppress other fields. All model-facing properties must be
        // emitted in the JSON payload.
        var hit = new SearchCorpusHit(
            MachineId: "GRBE-MJL05",
            MachineTitle: "Godzilla (Premium)",
            DocumentId: "doc_godzilla",
            DocumentUrl: "https://example/godzilla.pdf",
            DocumentType: "manual",
            PageStart: 5,
            PageEnd: 7,
            SectionHeading: "Rules",
            Content: "content text")
        {
            Score = 0.75,
        };

        var json = JsonSerializer.Serialize(hit);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // All model-visible fields must be present.
        Assert.True(root.TryGetProperty("MachineId", out _), "MachineId missing");
        Assert.True(root.TryGetProperty("MachineTitle", out _), "MachineTitle missing");
        Assert.True(root.TryGetProperty("DocumentId", out _), "DocumentId missing");
        Assert.True(root.TryGetProperty("DocumentUrl", out _), "DocumentUrl missing");
        Assert.True(root.TryGetProperty("DocumentType", out _), "DocumentType missing");
        Assert.True(root.TryGetProperty("PageStart", out _), "PageStart missing");
        Assert.True(root.TryGetProperty("PageEnd", out _), "PageEnd missing");
        Assert.True(root.TryGetProperty("SectionHeading", out _), "SectionHeading missing");
        Assert.True(root.TryGetProperty("Content", out _), "Content missing");

        // Score must not be present.
        Assert.False(root.TryGetProperty("Score", out _), "Score should be JsonIgnore'd");
        Assert.False(root.TryGetProperty("score", out _), "Score (lowercase) should be JsonIgnore'd");
    }
}
