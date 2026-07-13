using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

// Pins the JSON wire format for AnswerChunk per ADR-0026 § 4.
//
// The SSE endpoint (/api/wizard/ask:stream) serializes each chunk with the
// "$type" discriminator. The frontend WizardStreamingClient deserializes
// using the same [JsonPolymorphic] + [JsonDerivedType] attributes on
// AnswerChunk. These tests ensure:
//
//   1. Each variant round-trips through System.Text.Json with the polymorphic
//      config set up by the attributes (no custom type resolver needed).
//   2. The discriminator key is "$type" and the value matches the snake_case
//      SSE event name mapping used in WizardAskStreamEndpoint.
//   3. A future variant added without a [JsonDerivedType] attribute is caught
//      by the exhaustiveness test before it reaches the wire.
//
// Tests use JsonSerializerDefaults.Web (camelCase property names) mirroring
// the options used in both WizardAskStreamEndpoint and WizardStreamingClient.
public sealed class AnswerChunkJsonContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ──────────────────────────────────────────────────────────────
    // 1. Exhaustiveness: JsonDerivedType count matches the number of
    //    sealed nested record types on AnswerChunk. A new kind added
    //    without a [JsonDerivedType] attribute fails here.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnswerChunk_JsonDerivedTypeCount_MatchesConcreteNestedTypes()
    {
        // Concrete sealed record types = inner types that extend AnswerChunk.
        var concreteTypes = typeof(AnswerChunk)
            .GetNestedTypes()
            .Where(t => t.IsSealed && typeof(AnswerChunk).IsAssignableFrom(t))
            .ToList();

        // [JsonDerivedType] entries registered via the attribute.
        var derivedTypeAttributes = typeof(AnswerChunk)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToList();

        Assert.Equal(concreteTypes.Count, derivedTypeAttributes.Count);
    }

    // ──────────────────────────────────────────────────────────────
    // 2. Discriminator: serialized JSON contains "$type" key with
    //    the expected snake_case value.
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("text_delta")]
    [InlineData("tool_call_started")]
    [InlineData("tool_call_completed")]
    [InlineData("citation_arrived")]
    [InlineData("refusal")]
    [InlineData("final")]
    public void AnswerChunk_Serialize_ContainsExpectedTypeDiscriminator(string expectedDiscriminator)
    {
        AnswerChunk chunk = expectedDiscriminator switch
        {
            "text_delta"          => new AnswerChunk.TextDelta("ping"),
            "tool_call_started"   => new AnswerChunk.ToolCallStarted("getMachineByTitle", "tc-1"),
            "tool_call_completed" => new AnswerChunk.ToolCallCompleted("getMachineByTitle", "tc-1", Succeeded: true),
            "citation_arrived"    => new AnswerChunk.CitationArrived(new Citation("Title", "https://example.com")),
            "refusal"             => new AnswerChunk.Refusal(RefusalCategory.OutOfScope, "I don't know."),
            "final"               => new AnswerChunk.Final(BuildAnswer()),
            _                     => throw new ArgumentOutOfRangeException(nameof(expectedDiscriminator)),
        };

        var json = JsonSerializer.Serialize<AnswerChunk>(chunk, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("$type", out var typeEl),
            $"Serialized JSON for '{chunk.GetType().Name}' must contain '$type' discriminator. Got: {json}");
        Assert.Equal(expectedDiscriminator, typeEl.GetString());
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Round-trip: each variant serializes then deserializes back
    //    to the same concrete type with property values intact.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TextDelta_RoundTrip_PreservesText()
    {
        var original = new AnswerChunk.TextDelta("Hello, world!");
        var json = JsonSerializer.Serialize<AnswerChunk>(original, Options);
        var deserialized = JsonSerializer.Deserialize<AnswerChunk>(json, Options);

        var result = Assert.IsType<AnswerChunk.TextDelta>(deserialized);
        Assert.Equal(original.Text, result.Text);
    }

    [Fact]
    public void ToolCallStarted_RoundTrip_PreservesFields()
    {
        var original = new AnswerChunk.ToolCallStarted("getMachineByTitle", "tc-99");
        var json = JsonSerializer.Serialize<AnswerChunk>(original, Options);
        var deserialized = JsonSerializer.Deserialize<AnswerChunk>(json, Options);

        var result = Assert.IsType<AnswerChunk.ToolCallStarted>(deserialized);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.ToolCallId, result.ToolCallId);
    }

    [Fact]
    public void ToolCallCompleted_RoundTrip_PreservesFields()
    {
        var original = new AnswerChunk.ToolCallCompleted("searchCorpus", "tc-42", Succeeded: false);
        var json = JsonSerializer.Serialize<AnswerChunk>(original, Options);
        var deserialized = JsonSerializer.Deserialize<AnswerChunk>(json, Options);

        var result = Assert.IsType<AnswerChunk.ToolCallCompleted>(deserialized);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.ToolCallId, result.ToolCallId);
        Assert.Equal(original.Succeeded, result.Succeeded);
    }

    [Fact]
    public void CitationArrived_RoundTrip_PreservesCitationFields()
    {
        var citation = new Citation(
            "Stern Addams Family Manual",
            "https://sternpinball.com/manuals/addams-family.pdf",
            MachineId: "mch_abc123",
            PageStart: 42,
            SourceType: CitationSourceType.CorpusChunk);
        var original = new AnswerChunk.CitationArrived(citation);
        var json = JsonSerializer.Serialize<AnswerChunk>(original, Options);
        var deserialized = JsonSerializer.Deserialize<AnswerChunk>(json, Options);

        var result = Assert.IsType<AnswerChunk.CitationArrived>(deserialized);
        Assert.Equal(citation.Title, result.Citation.Title);
        Assert.Equal(citation.SourceUrl, result.Citation.SourceUrl);
        Assert.Equal(citation.MachineId, result.Citation.MachineId);
        Assert.Equal(citation.PageStart, result.Citation.PageStart);
        Assert.Equal(citation.SourceType, result.Citation.SourceType);
    }

    [Fact]
    public void Refusal_RoundTrip_PreservesFields()
    {
        var original = new AnswerChunk.Refusal(
            RefusalCategory.InsufficientGrounding,
            "I don't have enough grounding for this question.");
        var json = JsonSerializer.Serialize<AnswerChunk>(original, Options);
        var deserialized = JsonSerializer.Deserialize<AnswerChunk>(json, Options);

        var result = Assert.IsType<AnswerChunk.Refusal>(deserialized);
        Assert.Equal(original.Category, result.Category);
        Assert.Equal(original.Text, result.Text);
    }

    [Fact]
    public void Final_RoundTrip_PreservesWizardAnswer()
    {
        var answer = BuildAnswer();
        var original = new AnswerChunk.Final(answer);
        var json = JsonSerializer.Serialize<AnswerChunk>(original, Options);
        var deserialized = JsonSerializer.Deserialize<AnswerChunk>(json, Options);

        var result = Assert.IsType<AnswerChunk.Final>(deserialized);
        Assert.Equal(answer.Text, result.Answer.Text);
        Assert.Equal(answer.IsRefusal, result.Answer.IsRefusal);
        Assert.Equal(answer.SubAgentUsed, result.Answer.SubAgentUsed);
        Assert.Equal(answer.Confidence, result.Answer.Confidence);
    }

    // WizardAnswer.ToolCallTrace (#719) is eval-only scaffolding — read in-process
    // by the eval harness, never over the wire. It MUST NOT leak into the SSE
    // AnswerChunk.Final payload (it carries internal tool args: search queries,
    // machineId). This pins the [JsonIgnore] so a future refactor can't silently
    // start shipping the trace to clients.
    [Fact]
    public void Final_ToolCallTrace_IsNotSerializedToTheWire()
    {
        var answer = BuildAnswer() with
        {
            ToolCallTrace =
            [
                new ToolCallRecord(
                    "searchCorpus",
                    new Dictionary<string, string?> { ["machineId"] = "GBLZz-M4ok4", ["query"] = "multiball" }),
            ],
        };

        var json = JsonSerializer.Serialize<AnswerChunk>(new AnswerChunk.Final(answer), Options);

        Assert.DoesNotContain("toolCallTrace", json, StringComparison.OrdinalIgnoreCase);
        // The internal argument values must not appear anywhere in the wire payload.
        Assert.DoesNotContain("GBLZz-M4ok4", json, StringComparison.Ordinal);
        // Sanity: the in-process object still carries the trace for eval to read.
        Assert.Single(answer.ToolCallTrace!);
    }

    // ──────────────────────────────────────────────────────────────
    // 4. Null-optional fields (WhenWritingNull) are omitted from JSON
    //    so the SSE payload stays compact.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallStarted_NullToolCallId_OmittedFromJson()
    {
        var chunk = new AnswerChunk.ToolCallStarted("getMachineByTitle", ToolCallId: null);
        var json = JsonSerializer.Serialize<AnswerChunk>(chunk, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.False(
            doc.RootElement.TryGetProperty("toolCallId", out _),
            "Null ToolCallId should be omitted from JSON when WhenWritingNull is configured.");
    }

    [Fact]
    public void Final_NullFoundryThreadId_OmittedFromJson()
    {
        var chunk = new AnswerChunk.Final(BuildAnswer(foundryThreadId: null));
        var json = JsonSerializer.Serialize<AnswerChunk>(chunk, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("answer", out var answerEl));
        Assert.False(
            answerEl.TryGetProperty("foundryThreadId", out _),
            "Null FoundryThreadId should be omitted from JSON.");
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static WizardAnswer BuildAnswer(string? foundryThreadId = "thread-test-001")
    {
        return new WizardAnswer(
            Text: "The Addams Family is a great game.",
            Citations: [new Citation("Source", "https://example.com")],
            SubAgentUsed: "wizard",
            Confidence: 0.85,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v3.test",
            FoundryThreadId: foundryThreadId);
    }
}
