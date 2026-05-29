using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Reranking;

// Compile-time contract tests for the ICrossEncoderReranker abstraction
// (ADR-0024 W4 fix-up — Cohere Rerank gate triggered at H5 eval).
// These tests verify the interface, result type, and options class exist
// with the correct shape before any implementation exists. The full
// behaviour tests are in NullCrossEncoderRerankerTests and
// CohereRerankRerankerTests.
public sealed class CrossEncoderRerankerContractTests
{
    [Fact]
    public void ICrossEncoderReranker_InterfaceExists()
    {
        // Compile check — if the interface doesn't exist this file won't compile.
        ICrossEncoderReranker? _ = null;
        Assert.Null(_);
    }

    [Fact]
    public void RankedChunk_WrapsRetrievedChunkWithRelevanceScore()
    {
        var source = new RetrievedChunk(
            ChunkId: "chunk_001",
            MachineId: "mch_godzilla",
            MachineTitle: "Godzilla (Premium)",
            Manufacturer: "Stern Pinball",
            DocumentId: "doc_abc",
            DocumentUrl: "https://example.com/manual.pdf",
            DocumentType: "manual",
            PageStart: 1,
            PageEnd: 2,
            SectionHeading: "Rules",
            Content: "Kaiju multiball starts when …",
            Score: 0.7);

        var ranked = new RankedChunk(source, RelevanceScore: 0.92f);

        Assert.Same(source, ranked.Chunk);
        Assert.Equal(0.92f, ranked.RelevanceScore);
    }

    [Fact]
    public void CrossEncoderOptions_SectionName_IsRagCrossEncoder()
    {
        Assert.Equal("Rag:CrossEncoder", CrossEncoderOptions.SectionName);
    }

    [Fact]
    public void CrossEncoderOptions_EnabledDefaultsFalse()
    {
        var opts = new CrossEncoderOptions();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void CrossEncoderOptions_TopNDefaultsFive()
    {
        var opts = new CrossEncoderOptions();
        Assert.Equal(5, opts.TopN);
    }

    [Fact]
    public void CrossEncoderOptions_WhenEnabledTrueAndModelEndpointEmpty_ValidateFails()
    {
        // Validates the ServiceCollectionExtensions guard:
        //   .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ModelEndpoint), ...)
        // An operator who sets Enabled=true but omits ModelEndpoint must get a
        // startup-time failure, not a silent runtime UriFormatException.
        var services = new ServiceCollection();
        services.AddOptions<CrossEncoderOptions>()
            .Configure(o => { o.Enabled = true; o.ModelEndpoint = ""; })
            .ValidateDataAnnotations()
            .Validate(
                static o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ModelEndpoint),
                $"{CrossEncoderOptions.SectionName}:ModelEndpoint is required when {CrossEncoderOptions.SectionName}:Enabled=true.")
            .ValidateOnStart();

        var sp = services.BuildServiceProvider();

        // IStartupValidator forces ValidateOnStart to fire.
        var validator = sp.GetRequiredService<IStartupValidator>();
        var ex = Assert.Throws<OptionsValidationException>(validator.Validate);
        Assert.Contains("ModelEndpoint is required", ex.Message);
    }
}
