namespace PinballWizard.Infrastructure.Scraping.JsonLd;

/// <summary>
/// Storefront-agnostic projection of a <c>schema.org/Product</c>
/// JSON-LD block. Built by <see cref="JsonLdProductParser.FindFirstProduct"/>
/// and consumed by per-manufacturer extractors that map it onto a
/// <c>GameRecord</c>.
/// </summary>
public sealed class JsonLdProduct
{
    /// <summary>Product name (<c>schema.org/name</c>).</summary>
    public string? Name { get; init; }

    /// <summary>Product description (<c>schema.org/description</c>).</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Product images. Schema.org allows the <c>image</c> property to
    /// be a single URL or an array; both shapes collapse to this list.
    /// </summary>
    public List<string> Images { get; init; } = [];

    /// <summary>
    /// First Offer entry on the product (or null if none). Schema.org
    /// allows <c>offers</c> to be a single Offer or an array; the
    /// parser unwraps to the first object-typed entry. The schema
    /// property name is plural (<c>offers</c>) but the value here is
    /// always a single offer.
    /// </summary>
    public JsonLdOffer? Offers { get; init; }
}

/// <summary>
/// Storefront-agnostic projection of a single <c>schema.org/Offer</c>.
/// Captures the two fields manufacturer extractors actually use today.
/// </summary>
public sealed class JsonLdOffer
{
    /// <summary>
    /// Price as a culture-invariant string (<c>schema.org/price</c>).
    /// Read from the flat <c>offers.price</c> when present, falling
    /// back to the nested <c>offers.priceSpecification.price</c>
    /// (object or array) for WooCommerce-shape data.
    /// </summary>
    public string? Price { get; init; }

    /// <summary>
    /// Raw <c>schema.org/availability</c> URL or token (e.g.,
    /// <c>https://schema.org/InStock</c>,
    /// <c>http://schema.org/PreOrder</c>, or a bare <c>InStock</c>
    /// token). Per-manufacturer extractors normalise this to a
    /// short-form string for <c>GameRecord.Status</c>.
    /// </summary>
    public string? Availability { get; init; }
}
