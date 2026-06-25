using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Scraping.WooCommerce;

internal sealed class WooCommerceStoreProductDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("permalink")]
    public string Permalink { get; set; } = string.Empty;

    [JsonPropertyName("prices")]
    public WooCommerceStorePricesDto? Prices { get; set; }

    [JsonPropertyName("is_in_stock")]
    public bool IsInStock { get; set; }

    [JsonPropertyName("is_purchasable")]
    public bool IsPurchasable { get; set; }

    [JsonPropertyName("short_description")]
    public string? ShortDescription { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("images")]
    public List<WooCommerceStoreImageDto> Images { get; set; } = [];
}

internal sealed class WooCommerceStorePricesDto
{
    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("currency_code")]
    public string? CurrencyCode { get; set; }

    [JsonPropertyName("currency_minor_unit")]
    public int CurrencyMinorUnit { get; set; }
}

internal sealed class WooCommerceStoreImageDto
{
    [JsonPropertyName("src")]
    public string? Src { get; set; }
}
