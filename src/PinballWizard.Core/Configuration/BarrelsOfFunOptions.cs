using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the Barrels of Fun scraper. The marketing site
/// (<c>www.barrelsoffun.com</c>) and the storefront
/// (<c>shop.kollectfun.com</c>) are separate domains; the actual
/// machine catalog and JSON-LD product schema live on the shop
/// subdomain. <see cref="BaseUrl"/> targets the shop.
/// </summary>
/// <remarks>
/// Phase 1.3 of the manufacturer-scraper fan-out. Discovery is
/// machine-consumer-metadata-first per
/// <c>feedback_machine_consumer_metadata_first.md</c>: the
/// <c>/product-category/machines/</c> page is the canonical filter
/// — only WooCommerce products in that category count as machines
/// (everything else under <c>/product/*</c> is apparel / parts /
/// accessories). Per-product extraction prefers JSON-LD
/// <c>schema.org/Product</c> over rendered-DOM scraping.
/// </remarks>
public sealed class BarrelsOfFunOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BarrelsOfFun";

    /// <summary>Storefront root URL. Defaults to the production shop subdomain.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://shop.kollectfun.com";

    /// <summary>
    /// Path to the WooCommerce product category that lists pinball
    /// machines. The scraper fetches this page, extracts every
    /// <c>/product/{slug}/</c> link, and treats those as the machine
    /// set. Apparel / parts / accessories live in other categories
    /// and are excluded.
    /// </summary>
    [Required]
    public string MachinesCategoryPath { get; set; } = "/product-category/machines/";

    /// <summary>
    /// URL path prefix that identifies a WooCommerce product page.
    /// Used to filter anchors on the machines category page down to
    /// product links.
    /// </summary>
    [Required]
    public string ProductPathPrefix { get; set; } = "/product/";
}
