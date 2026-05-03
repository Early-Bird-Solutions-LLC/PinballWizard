using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Reads the host → effective-options map from
/// <see cref="IIngestionSourceRepository"/> on first lookup, applying
/// each source's <see cref="PolitenessOverrides"/> on top of the
/// global defaults. Caches the map for the process lifetime — the
/// PolitenessOverrides are stable runtime config (Admin UI updates
/// require a scraper redeploy to pick up by ADR 0007 design).
/// </summary>
/// <remarks>
/// Degrades safely when the Cosmos repository is unreachable — logs a
/// warning and falls back to global defaults, so a transient Cosmos
/// outage during scraper startup never blocks scraping. The
/// per-request fast path is a single dictionary lookup against the
/// pre-built map.
/// </remarks>
public sealed class IngestionSourcePolitenessResolver : IPerSourcePolitenessResolver, IDisposable
{
    private readonly IIngestionSourceRepository _repository;
    private readonly PolitenessOptions _defaults;
    private readonly ILogger<IngestionSourcePolitenessResolver> _logger;
    private readonly ConcurrentDictionary<string, PolitenessOptions> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>Initializes a new Cosmos-backed resolver.</summary>
    public IngestionSourcePolitenessResolver(
        IIngestionSourceRepository repository,
        IOptions<PolitenessOptions> defaults,
        ILogger<IngestionSourcePolitenessResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _defaults = defaults.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PolitenessOptions> ResolveAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!_initialized)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        return _byHost.TryGetValue(url.Host, out var effective) ? effective : _defaults;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            try
            {
                await foreach (var source in _repository.StreamAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var baseUri))
                    {
                        _logger.LogWarning(
                            "IngestionSource {Id} has invalid BaseUrl '{BaseUrl}' — skipping politeness override.",
                            source.Id, source.BaseUrl);
                        continue;
                    }

                    var effective = ApplyOverrides(_defaults, source.PolitenessOverrides);
                    _byHost[baseUri.Host] = effective;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to load IngestionSource records for politeness override resolution; falling back to global defaults for every host.");
                // Map remains empty — every ResolveAsync call returns _defaults.
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _initLock.Dispose();

    /// <summary>
    /// Returns a new <see cref="PolitenessOptions"/> with each
    /// non-null field of <paramref name="overrides"/> applied on top
    /// of <paramref name="defaults"/>. Pure function — defaults and
    /// overrides are not mutated.
    /// </summary>
    public static PolitenessOptions ApplyOverrides(PolitenessOptions defaults, PolitenessOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (overrides is null) return defaults;

        var ua = string.IsNullOrWhiteSpace(overrides.UserAgentSuffix)
            ? defaults.UserAgent
            : $"{defaults.UserAgent} {overrides.UserAgentSuffix}";

        return new PolitenessOptions
        {
            UserAgent = ua,
            RequestDelayMs = overrides.RequestDelayMs ?? defaults.RequestDelayMs,
            Max429Streak = overrides.Max429Streak ?? defaults.Max429Streak,
            RespectRobotsTxt = defaults.RespectRobotsTxt,
            RobotsTxtPath = string.IsNullOrWhiteSpace(overrides.RobotsTxtPath) ? defaults.RobotsTxtPath : overrides.RobotsTxtPath,
            RobotsTxtTtlSeconds = defaults.RobotsTxtTtlSeconds,
        };
    }
}
