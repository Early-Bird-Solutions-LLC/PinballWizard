using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Minimal projection of a WordPress REST API page object as returned
/// by <c>/wp-json/wp/v2/pages</c>. Only the fields we need are bound.
/// </summary>
/// <remarks>
/// Modeled as a class with init-only accessors (not a positional
/// record) for the same reason as <c>SpookyPageRaw</c> — keeps the
/// deserializer's parameterless-construction path happy across all
/// System.Text.Json versions. A dedicated DTO type per scraper avoids
/// cross-manufacturer coupling; promoting to a shared <c>Common/Wp/</c>
/// type is a follow-up once a third or fourth WP scraper lands.
/// </remarks>
public sealed class PbPageRaw
{
    /// <summary>Page id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>WordPress page slug (e.g., <c>queen-pinball</c>).</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    /// <summary>Canonical link as published.</summary>
    [JsonPropertyName("link")]
    public string Link { get; init; } = string.Empty;

    /// <summary>Parent page id; <c>0</c> means top-level.</summary>
    [JsonPropertyName("parent")]
    public int Parent { get; init; }

    /// <summary>Last-modified timestamp (ISO 8601, site-local).</summary>
    [JsonPropertyName("modified")]
    public string? Modified { get; init; }

    /// <summary>Page title (rendered HTML; may contain entities).</summary>
    [JsonPropertyName("title")]
    public PbRenderedField Title { get; init; } = new();

    /// <summary>
    /// Full page content (rendered HTML + raw shortcode markup).
    /// Only populated when <c>content</c> is included in the WP REST
    /// <c>_fields</c> projection; defaults to empty when absent.
    /// </summary>
    [JsonPropertyName("content")]
    public PbRenderedField Content { get; init; } = new();
}

/// <summary>
/// WordPress's <c>{ "rendered": "..." }</c> wrapper used for the title
/// field. Local to this scraper to avoid cross-manufacturer coupling.
/// </summary>
public sealed class PbRenderedField
{
    /// <summary>Rendered HTML string.</summary>
    [JsonPropertyName("rendered")]
    public string Rendered { get; init; } = string.Empty;
}
