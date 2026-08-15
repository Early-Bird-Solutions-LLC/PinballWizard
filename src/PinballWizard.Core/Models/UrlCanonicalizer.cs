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

        // Replace ONLY the host span in the original string, leaving every other byte
        // untouched.
        //
        // A UriBuilder round-trip (`new UriBuilder(uri) { Host = ... }.Uri.ToString()`)
        // looks equivalent and is not: it re-encodes the path, turning
        // ".../My%20Manual.pdf" into ".../My Manual.pdf". The plain-host URL takes the
        // early return above and is NOT re-encoded, so the two forms would produce
        // different strings — and therefore different ids — and this whole fix would
        // silently do nothing for any percent-encoded URL. Byte-identity with the
        // plain-host form is the property the fix rests on.
        // Pinned by UrlCanonicalizerTests.Canonicalize_AwkwardUrlShapes_MatchThePlainHostFormExactly.
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return url;

        // Search from the start of the authority so a host-shaped substring later in the
        // path cannot be rewritten; the host is the first authority component (after any
        // userinfo@, which these scraped file URLs never carry).
        var hostIndex = url.IndexOf(uri.Host, schemeEnd + 3, StringComparison.OrdinalIgnoreCase);
        if (hostIndex < 0)
            return url;

        return string.Concat(
            url.AsSpan(0, hostIndex),
            canonicalHost,
            url.AsSpan(hostIndex + uri.Host.Length));
    }
}
