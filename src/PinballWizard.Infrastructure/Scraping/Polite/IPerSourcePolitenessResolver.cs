using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Resolves the effective <see cref="PolitenessOptions"/> for a given
/// request URL — applying any per-source overrides defined on the
/// matching <c>IngestionSource</c> on top of the global defaults.
/// </summary>
/// <remarks>
/// Per the locked feedback memory <c>feedback_polite_scraping.md</c>
/// and the existing <c>PolitenessOptions</c> docstring, per-source
/// overrides are part of the politeness contract — fragile sites can
/// be told to scrape slower without a redeploy. The Cosmos-backed
/// implementation reads <c>IngestionSource.PolitenessOverrides</c>
/// at first lookup and caches the resulting host → effective-options
/// map for the process lifetime; the default implementation always
/// returns the unmodified global defaults (used when Cosmos isn't
/// wired, e.g., the standalone CLI without Aspire).
/// <para>
/// Implementations MUST be thread-safe — the politeness gate is a
/// process-wide singleton consulted from every scraper.
/// </para>
/// </remarks>
public interface IPerSourcePolitenessResolver
{
    /// <summary>
    /// Returns the effective <see cref="PolitenessOptions"/> for the
    /// given request URL. Implementations look up by
    /// <see cref="Uri.Host"/> against the configured ingestion-source
    /// records; if no source matches, the global defaults are returned
    /// unchanged.
    /// </summary>
    ValueTask<PolitenessOptions> ResolveAsync(Uri url, CancellationToken cancellationToken);
}
