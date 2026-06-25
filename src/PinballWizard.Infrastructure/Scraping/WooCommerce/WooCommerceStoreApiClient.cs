using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.WooCommerce;

public sealed class WooCommerceStoreApiClient : PoliteScraperBase
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxPages = 50;
    private const int PerPage = 20;

    public WooCommerceStoreApiClient(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<WooCommerceStoreApiClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    internal async Task<List<WooCommerceStoreProductDto>> FetchProductsByCategoryAsync(
        string baseUrl,
        int categoryId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var all = new List<WooCommerceStoreProductDto>();
        var baseUri = new Uri(baseUrl.TrimEnd('/'));

        for (var page = 1; page <= MaxPages; page++)
        {
            var url = new Uri(baseUri, $"/wp-json/wc/store/v1/products?category={categoryId}&per_page={PerPage}&page={page}");

            string json;
            try
            {
                json = await GetStringPolitelyAsync(_httpClient, url, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
            {
                Logger.LogWarning(ex, "WooCommerce Store API: failed to fetch page {Page} from {Url}; stopping pagination.", page, url);
                break;
            }

            List<WooCommerceStoreProductDto>? page_items;
            try
            {
                page_items = JsonSerializer.Deserialize<List<WooCommerceStoreProductDto>>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "WooCommerce Store API: failed to parse JSON from page {Page} at {Url}; stopping pagination.", page, url);
                break;
            }

            if (page_items is null || page_items.Count == 0)
                break;

            all.AddRange(page_items);

            if (page == MaxPages)
            {
                Logger.LogWarning("WooCommerce Store API: reached max page cap ({Max}) for category {CategoryId} at {BaseUrl}; pagination stopped early.", MaxPages, categoryId, baseUrl);
            }
        }

        return all;
    }
}
