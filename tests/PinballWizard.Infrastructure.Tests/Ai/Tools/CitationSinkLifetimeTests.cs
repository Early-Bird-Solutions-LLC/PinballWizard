using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Tools;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Tools;

// Regression guard for the citation-sink captive-dependency bug.
//
// SearchCorpusTool is registered as a Singleton, so the
// IRetrievalCitationMetadataSink it consumes MUST also be a Singleton.
// A Scoped sink is a captive dependency: the .NET DI scope validator rejects
// it under ValidateScopes, and with ValidateOnBuild it fails at provider
// build. The existing ApiCompositionRootTests deliberately boot in the
// Production environment (ValidateOnBuild off) to target a different concern,
// so the singleton-consumes-the-sink path was never validated — which is
// exactly how the Scoped registration shipped. This test boots the relevant
// slice with validation ON; it fails if the sink ever regresses to Scoped.
public sealed class CitationSinkLifetimeTests
{
    [Fact]
    public void SearchCorpusTool_resolves_with_citation_sink_under_scope_validation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IRagRetriever>());
        services.TryAddSingleton<IDegradationContext, AmbientDegradationContext>();

        // The fix under test: the shared sink must be a Singleton so the
        // Singleton SearchCorpusTool can consume it without a captive dependency.
        services.TryAddSingleton<IRetrievalCitationMetadataSink, RetrievalCitationMetadataSink>();
        services.TryAddSingleton<SearchCorpusTool>();

        // ValidateOnBuild eagerly constructs every singleton; ValidateScopes
        // rejects a singleton that consumes a scoped service. Throws here if
        // the sink registration regresses to Scoped.
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.NotNull(provider.GetRequiredService<SearchCorpusTool>());
    }
}
