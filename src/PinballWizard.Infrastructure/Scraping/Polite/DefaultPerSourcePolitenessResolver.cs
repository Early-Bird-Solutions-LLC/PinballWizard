using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Always returns the global <see cref="PolitenessOptions"/> defaults
/// — used when no <c>IngestionSource</c>-backed resolver is wired
/// (e.g., the standalone CLI running without Cosmos / Aspire).
/// </summary>
/// <remarks>
/// Registered via <c>TryAddSingleton</c> by <c>AddPoliteScraping</c>
/// so a Cosmos-backed implementation registered later (by
/// <c>AddCosmosBackedPolitenessOverrides</c>) takes precedence.
/// </remarks>
public sealed class DefaultPerSourcePolitenessResolver : IPerSourcePolitenessResolver
{
    private readonly PolitenessOptions _defaults;

    /// <summary>Initializes a new resolver from the global options.</summary>
    public DefaultPerSourcePolitenessResolver(IOptions<PolitenessOptions> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults.Value;
    }

    /// <inheritdoc />
    public ValueTask<PolitenessOptions> ResolveAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        return ValueTask.FromResult(_defaults);
    }
}
