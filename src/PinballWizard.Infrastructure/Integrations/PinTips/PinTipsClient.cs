using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Integrations.PinTips;

public sealed class PinTipsClient : PoliteScraperBase
{
    private readonly HttpClient _http;
    private readonly PinTipsOptions _options;

    public PinTipsClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<PinTipsOptions> options,
        ILogger<PinTipsClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<PinTip>> FetchAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.ExportUrl, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException(
                $"PinTips:ExportUrl must be an absolute URL; got '{_options.ExportUrl}'.");
        }

        Logger.LogInformation("PinTips: reading export at {Url}", url);
        var json = await GetStringPolitelyAsync(_http, url, cancellationToken).ConfigureAwait(false);
        var tips = PinTipsParser.Parse(json);
        Logger.LogInformation("PinTips: parsed {Count} tip(s).", tips.Count);
        return tips;
    }
}
