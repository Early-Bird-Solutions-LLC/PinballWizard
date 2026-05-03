using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace PinballWizard.Infrastructure.Scraping.OpenGraph;

/// <summary>
/// Reads <c>&lt;meta&gt;</c> content values from a parsed HTML document
/// using the OpenGraph / RDFa <c>property=</c> attribute first and the
/// HTML5 / Twitter-card <c>name=</c> attribute as a fallback. The
/// fallback ordering matches the convention used across the storefront
/// extractors before this helper existed: try the spec-correct form
/// first, accept the loose form when sites publish it under
/// <c>name=</c>.
/// </summary>
/// <remarks>
/// Extracted to a shared helper after three storefronts (JJP, BoF,
/// Multimorphic) shipped byte-identical private copies of the same
/// method. The threshold called out in PR #38's review and PR #43's
/// CHANGELOG note — three consumers is enough to share. Each
/// extractor keeps its own DOM-fallback chain (which OG keys to read,
/// and in what order they fall back to the JSON-LD value); what they
/// share is the meta-content read.
/// <para>
/// Pure functions — no I/O, no allocation beyond the trimmed string.
/// </para>
/// </remarks>
public static class OpenGraphExtractor
{
    /// <summary>
    /// Returns the trimmed <c>content</c> attribute of the first
    /// <c>&lt;meta property='{property}'&gt;</c> in the document, or
    /// — if no such element exists — the first
    /// <c>&lt;meta name='{property}'&gt;</c>. Returns null when neither
    /// form is present.
    /// </summary>
    /// <remarks>
    /// Behaviour is byte-equivalent to the previously-private
    /// <c>GetMetaContent</c> implementations in
    /// <c>JjpProductExtractor</c>, <c>BofProductExtractor</c>, and
    /// <c>MultimorphicProductExtractor</c>: an empty / whitespace-only
    /// <c>content=""</c> attribute returns the empty string (the call
    /// sites then handle empty values via downstream
    /// <see cref="string.IsNullOrWhiteSpace(string?)"/> checks). Do not
    /// change this without auditing every caller.
    /// </remarks>
    /// <param name="doc">The parsed HTML document.</param>
    /// <param name="property">
    /// The property identifier, e.g. <c>og:title</c>, <c>og:description</c>,
    /// <c>og:image</c>, <c>twitter:card</c>. Callers must pass a literal
    /// (not user input) — the value is interpolated into a CSS selector
    /// without escaping, which is safe for known-literal property names
    /// but would be unsafe for arbitrary input.
    /// </param>
    public static string? GetMetaContent(IHtmlDocument doc, string property)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);

        var meta = doc.QuerySelector($"meta[property='{property}']")
            ?? doc.QuerySelector($"meta[name='{property}']");
        return meta?.GetAttribute("content")?.Trim();
    }
}
