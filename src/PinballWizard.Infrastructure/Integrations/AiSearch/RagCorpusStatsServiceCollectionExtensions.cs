using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.AiSearch;

public static class RagCorpusStatsServiceCollectionExtensions
{
    // Narrow registration for the read-only RAG corpus stats reader. Binds
    // AiSearchOptions and registers IRagCorpusStatsReader only — NO Foundry, NO
    // embedder/retriever stack, and deliberately NO ValidateOnStart, so a Web host
    // with AI Search unconfigured still starts (the reader degrades to a visible
    // "unavailable" at read time rather than crashing the host).
    public static IServiceCollection AddRagCorpusStatsRead(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiSearchOptions>()
            .Bind(configuration.GetSection(AiSearchOptions.SectionName));

        services.TryAddSingleton<IRagCorpusStatsReader, AiSearchRagCorpusStatsReader>();
        return services;
    }
}
