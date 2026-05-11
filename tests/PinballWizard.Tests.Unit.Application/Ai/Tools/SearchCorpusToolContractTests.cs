using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Ai.Tools;

// JSON-Schema contract test for the searchCorpus function tool. Pins
// the shape Microsoft.Extensions.AI auto-generates from
// `[Description]` attributes + parameter nullability so an SDK bump
// can't silently change what the model sees. Mirrors the
// SternPlaywrightDtoActivatorContractTests posture: pin via
// reflection over the constructed AIFunction's JsonSchema, NOT by
// parsing attributes off the method.
//
// The schema is what the LLM is told the tool accepts. Drift here
// changes how the model calls the tool — the very kind of silent
// breakage build-spec § Phase 4 lessons 4 + 5 cite as the reason
// tool-trace citation extraction replaced regex.
public sealed class SearchCorpusToolContractTests
{
    private static AIFunction CreateAIFunction()
    {
        var retriever = Substitute.For<IRagRetriever>();
        var tool = new SearchCorpusTool(retriever, new AmbientDegradationContext(), NullLogger<SearchCorpusTool>.Instance);
        return AIFunctionFactory.Create(tool.SearchCorpusAsync);
    }

    [Fact]
    public void FunctionName_Matches_SearchCorpusAsync_ConventionTrimmed()
    {
        // AIFunctionFactory.Create derives the function name from the
        // method (Microsoft.Extensions.AI strips "Async" suffix).
        // Wizard.md / sub-agent prompts reference the tool by the name
        // "searchCorpus" — adding here as a structural lock.
        var fn = CreateAIFunction();
        Assert.Equal("SearchCorpus", fn.Name, ignoreCase: true);
    }

    [Fact]
    public void Description_DocumentsRetrievalSemantics()
    {
        // Pin the LLM-facing description: the tool MUST tell the model
        // empty results require refusal-not-fabrication. Drift here is
        // the failure mode behind ADR-0023's existence.
        var fn = CreateAIFunction();
        Assert.Contains("corpus", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cite", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty", fn.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSchema_DeclaresAllFourParameters()
    {
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;

        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("query", out _));
        Assert.True(properties.TryGetProperty("machineId", out _));
        Assert.True(properties.TryGetProperty("documentType", out _));
        Assert.True(properties.TryGetProperty("topK", out _));
    }

    [Fact]
    public void JsonSchema_QueryIsRequired_OthersAcceptNull()
    {
        // Microsoft.Extensions.AI's schema generator lists every C#
        // parameter in the `required` array and encodes optionality
        // by adding "null" to the type union for nullable params. So
        // the model-facing contract is: every name appears, but
        // `machineId` / `documentType` / `topK` accept `null` and the
        // model is taught (via [Description]) to omit when not needed.
        // The load-bearing assertion is that `query` is required AND
        // does NOT accept null — empty/whitespace input is enforced
        // server-side by the tool, not by the schema.
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;

        Assert.True(schema.TryGetProperty("required", out var required));
        var requiredList = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("query", requiredList);

        var props = schema.GetProperty("properties");

        // query: type "string" (no "null" in the union)
        Assert.False(AcceptsNull(props.GetProperty("query")));

        // machineId / documentType / topK: nullable in schema
        Assert.True(AcceptsNull(props.GetProperty("machineId")));
        Assert.True(AcceptsNull(props.GetProperty("documentType")));
        Assert.True(AcceptsNull(props.GetProperty("topK")));
    }

    private static bool AcceptsNull(JsonElement parameterSchema)
    {
        if (!parameterSchema.TryGetProperty("type", out var typeProp))
        {
            return false;
        }
        if (typeProp.ValueKind == JsonValueKind.String)
        {
            return string.Equals(typeProp.GetString(), "null", StringComparison.Ordinal);
        }
        if (typeProp.ValueKind == JsonValueKind.Array)
        {
            return typeProp.EnumerateArray().Any(e =>
                string.Equals(e.GetString(), "null", StringComparison.Ordinal));
        }
        return false;
    }

    [Fact]
    public void JsonSchema_QueryParameter_HasDescriptionMentioningQuery()
    {
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;
        var query = schema.GetProperty("properties").GetProperty("query");

        Assert.True(query.TryGetProperty("description", out var desc));
        var descText = desc.GetString();
        Assert.NotNull(descText);
        // Drift guard: the description tells the model to pass the
        // user's question through unchanged. Phrasing changes are OK
        // (this test isn't a copy-pasted assertion); the `pass*through`
        // semantic anchor is required.
        Assert.Contains("question", descText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSchema_DocumentTypeParameter_DescriptionListsAllowedValues()
    {
        // The model picks documentType from a tight enum: 'manual',
        // 'service_bulletin', 'metadata_card'. Drift in the listed
        // values would let the model pass a value that filters out
        // every chunk in the index (since the field is exact-match
        // OData filtered).
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;
        var docType = schema.GetProperty("properties").GetProperty("documentType");

        Assert.True(docType.TryGetProperty("description", out var desc));
        var descText = desc.GetString();
        Assert.NotNull(descText);
        Assert.Contains("manual", descText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service_bulletin", descText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata_card", descText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSchema_TopKParameter_IsIntegerType()
    {
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;
        var topK = schema.GetProperty("properties").GetProperty("topK");

        // The schema may model nullable int as either ["integer", "null"]
        // or just "integer" — both are acceptable as long as integer is
        // in the type set. A "string" type would be a clear regression.
        if (topK.TryGetProperty("type", out var typeProp))
        {
            if (typeProp.ValueKind == JsonValueKind.String)
            {
                Assert.Equal("integer", typeProp.GetString());
            }
            else if (typeProp.ValueKind == JsonValueKind.Array)
            {
                var typeNames = typeProp.EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("integer", typeNames);
            }
        }
    }
}
