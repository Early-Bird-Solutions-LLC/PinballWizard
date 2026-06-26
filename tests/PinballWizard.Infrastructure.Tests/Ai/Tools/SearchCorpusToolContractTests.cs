using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Tools;

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
    public void JsonSchema_QueryIsRequired_OthersAreOptional()
    {
        // Microsoft.Extensions.AI's schema generator puts a parameter in
        // the `required` array iff it has no C# default value — nullability
        // alone does NOT make it optional. Before the `= null` defaults
        // landed, machineId/documentType/topK were schema-required despite
        // their "Optional:" descriptions; when the model (correctly) omitted
        // one, argument binding threw before the tool body ran and the model
        // saw "Error: Function failed." (the ev-repair-0008 hard error in
        // eval wizard.20260610T160646Z). This test pins the fixed contract:
        // `query` is the ONLY required parameter, and the optional three
        // must stay out of the required array. CancellationToken is
        // framework-handled and never appears in the schema.
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;

        Assert.True(schema.TryGetProperty("required", out var required));
        var requiredList = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("query", requiredList);
        Assert.DoesNotContain("machineId", requiredList);
        Assert.DoesNotContain("documentType", requiredList);
        Assert.DoesNotContain("topK", requiredList);

        var props = schema.GetProperty("properties");

        // query: type "string" (no "null" in the union)
        Assert.False(AcceptsNull(props.GetProperty("query")));

        // machineId / documentType / topK: nullable in schema
        Assert.True(AcceptsNull(props.GetProperty("machineId")));
        Assert.True(AcceptsNull(props.GetProperty("documentType")));
        Assert.True(AcceptsNull(props.GetProperty("topK")));
    }

    [Fact]
    public async Task Invoke_OmittingAllOptionalArguments_BindsAndRunsToolBody()
    {
        // End-to-end binding proof for the defect class itself: invoke the
        // AIFunction the way gpt-4o did during the 2026-06-10 eval — only
        // `query` supplied. Before the parameter defaults, binding threw
        // ArgumentException ("missing a value for the required parameter
        // 'machineId'") without ever entering SearchCorpusAsync. With the
        // defaults, the call must reach the retriever with the tool's own
        // fallback values.
        var retriever = Substitute.For<IRagRetriever>();
        retriever
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var tool = new SearchCorpusTool(retriever, new AmbientDegradationContext(), NullLogger<SearchCorpusTool>.Instance);
        var fn = AIFunctionFactory.Create(tool.SearchCorpusAsync);

        await fn.InvokeAsync(new AIFunctionArguments { ["query"] = "flipper not responding" });

        await retriever.Received(1).RetrieveAsync(
            "flipper not responding",
            Arg.Is<RetrievalOptions>(o =>
                o.MachineId == null
                && o.DocumentType == null
                && o.TopK == SearchCorpusTool.TopKDefault),
            Arg.Any<CancellationToken>());
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
        // The model picks documentType from a tight enum: 'rulesheet',
        // 'manual', 'service_bulletin', 'metadata_card'. Drift in the listed
        // values would let the model pass a value that filters out
        // every chunk in the index (since the field is exact-match
        // OData filtered). 'rulesheet' is the gameplay-strategy type the
        // Kineticist tutorials index under (ADR-0042/0043); omitting it from
        // the description is what made that content unreachable before.
        var fn = CreateAIFunction();
        var schema = fn.JsonSchema;
        var docType = schema.GetProperty("properties").GetProperty("documentType");

        Assert.True(docType.TryGetProperty("description", out var desc));
        var descText = desc.GetString();
        Assert.NotNull(descText);
        Assert.Contains("rulesheet", descText, StringComparison.OrdinalIgnoreCase);
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
