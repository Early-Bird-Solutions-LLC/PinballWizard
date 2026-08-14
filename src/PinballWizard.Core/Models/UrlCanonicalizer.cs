namespace PinballWizard.Core.Models;

/// <summary>
/// Canonicalizes file URLs for stable document identity across known host aliases.
///
/// Only the <em>identity hash</em> is normalized — the actual URL stored in
/// <see cref="DocumentRecord.Source"/> is always the URL that was fetched.
/// Provenance is preserved; only the input to <see cref="DocumentRecord.GenerateId"/>
/// changes so that the same physical file resolving under two hostnames hashes to
/// one document id.
/// </summary>
public static class UrlCanonicalizer
{
    /// <summary>
    /// Explicit host-alias map: alias host → canonical host.
    ///
    /// Rules for adding an entry:
    /// - Both hosts must serve byte-for-byte identical content at the same path.
    /// - The mapping must be verified empirically, not inferred by pattern.
    /// - Each entry must carry a comment stating the evidence and the date observed.
    ///
    /// This is deliberately NOT a blanket "strip any leading subdomain" rule.
    /// Such a rule would silently merge genuinely distinct hosts across manufacturers
    /// (e.g. docs.jerseyjackpinball.com vs jerseyjackpinball.com) — a data-corruption
    /// risk that outweighs the convenience.
    /// </summary>
    private static readonly Dictionary<string, string> HostAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // wp.sternpinball.com is a WordPress-internal CDN alias for sternpinball.com.
            // Both hosts serve the same files under /wp-content/uploads/ — same bytes,
            // same path, same filename. Observed 2026-08-13 (issue #843): 22 of 30
            // needs_review docs were duplicate pairs caused by July scrape runs hitting
            // sternpinball.com and August runs hitting wp.sternpinball.com for the same PDFs.
            ["wp.sternpinball.com"] = "sternpinball.com",
        };

    /// <summary>
    /// Returns a URL with any known host alias replaced by its canonical host.
    /// All other URL components (scheme, port, path, query, fragment) are preserved
    /// exactly. Returns the original URL unchanged when no alias applies or when the
    /// URL cannot be parsed as an absolute URI.
    /// </summary>
    /// <param name="url">The file URL to canonicalize.</param>
    public static string Canonicalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (!HostAliases.TryGetValue(uri.Host, out var canonicalHost))
            return url;

        var builder = new UriBuilder(uri) { Host = canonicalHost };
        return builder.Uri.ToString();
    }
}
