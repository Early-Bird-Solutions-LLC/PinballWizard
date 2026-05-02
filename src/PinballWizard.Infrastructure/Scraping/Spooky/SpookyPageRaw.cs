using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Minimal projection of a WordPress REST API page object as returned
/// by <c>/wp-json/wp/v2/pages</c>. Only the fields we need are bound.
/// </summary>
/// <remarks>
/// Modeled as a class with init-only accessors (not a positional
/// record) to keep the deserializer's parameterless-construction path
/// happy across all System.Text.Json versions — the same lesson
/// captured by ADR / commit notes from the AP scraper PR.
/// </remarks>
public sealed class SpookyPageRaw
{
    /// <summary>Page id (stable across slug renames; useful as a fallback identifier).</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>WordPress page slug. May be a numeric placeholder like <c>2486-2</c> for older content.</summary>
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
    public WpRenderedField Title { get; init; } = new();

    /// <summary>Full page content (rendered HTML).</summary>
    [JsonPropertyName("content")]
    public WpRenderedField Content { get; init; } = new();
}

/// <summary>
/// WordPress's <c>{ "rendered": "..." }</c> wrapper used for both title
/// and content fields.
/// </summary>
public sealed class WpRenderedField
{
    /// <summary>Rendered HTML string.</summary>
    [JsonPropertyName("rendered")]
    public string Rendered { get; init; } = string.Empty;
}
