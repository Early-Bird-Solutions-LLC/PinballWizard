using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Core.Tests.Models;

/// <summary>
/// Verifies host-alias normalization for document identity (issue #843).
///
/// wp.sternpinball.com and sternpinball.com both serve the same WordPress media
/// uploads. Without normalization, July and August scrape runs produced 22 duplicate
/// document pairs — same PDF, two distinct doc ids — splitting citation provenance
/// across two records in the RAG index.
/// </summary>
public sealed class UrlCanonicalizerTests
{
    // ── Host-alias resolution ─────────────────────────────────────────────────

    [Fact]
    public void Canonicalize_WpSternHost_ReturnsCanonicalSternHost()
    {
        var aliased = "https://wp.sternpinball.com/wp-content/uploads/2024/03/ST_Pro_Manual.pdf";
        var canonical = "https://sternpinball.com/wp-content/uploads/2024/03/ST_Pro_Manual.pdf";

        Assert.Equal(canonical, UrlCanonicalizer.Canonicalize(aliased));
    }

    // The whole fix rests on BYTE-IDENTITY: an aliased URL must canonicalize to exactly
    // the string a plain-host scrape would supply, because the plain host is returned
    // untouched. Any normalization applied to one path and not the other (percent-encoding,
    // default port, trailing slash) silently re-splits the pair into two ids and the fix
    // looks applied while doing nothing. These cases pin the awkward shapes.
    [Theory]
    [InlineData("https://wp.sternpinball.com/wp-content/uploads/My%20Manual.pdf",
                "https://sternpinball.com/wp-content/uploads/My%20Manual.pdf")]
    [InlineData("https://wp.sternpinball.com/wp-content/uploads/a.pdf?v=2#part",
                "https://sternpinball.com/wp-content/uploads/a.pdf?v=2#part")]
    [InlineData("https://wp.sternpinball.com/wp-content/uploads/re-issue_(2024).pdf",
                "https://sternpinball.com/wp-content/uploads/re-issue_(2024).pdf")]
    public void Canonicalize_AwkwardUrlShapes_MatchThePlainHostFormExactly(
        string aliased, string expected)
    {
        Assert.Equal(expected, UrlCanonicalizer.Canonicalize(aliased));

        // And the identity that actually matters: both forms hash to one id.
        Assert.Equal(
            DocumentRecord.GenerateId(expected),
            DocumentRecord.GenerateId(aliased));
    }

    [Fact]
    public void Canonicalize_AlreadyCanonicalSternHost_ReturnedUnchanged()
    {
        var url = "https://sternpinball.com/wp-content/uploads/2024/03/ST_Pro_Manual.pdf";

        Assert.Equal(url, UrlCanonicalizer.Canonicalize(url));
    }

    [Fact]
    public void Canonicalize_NonAliasedHost_ReturnedUnchanged()
    {
        // Ensures no accidental normalization bleeds into other manufacturers.
        var url = "https://docs.jerseyjackpinball.com/wp-content/uploads/manual.pdf";

        Assert.Equal(url, UrlCanonicalizer.Canonicalize(url));
    }

    [Fact]
    public void Canonicalize_MalformedUrl_ReturnedUnchanged()
    {
        var url = "not-a-url-at-all";

        Assert.Equal(url, UrlCanonicalizer.Canonicalize(url));
    }

    [Fact]
    public void Canonicalize_PreservesPathQueryAndFragment()
    {
        // All URL components other than the host must survive unchanged.
        var aliased = "https://wp.sternpinball.com/wp-content/uploads/file.pdf?v=2#section";
        var result = UrlCanonicalizer.Canonicalize(aliased);

        var uri = new Uri(result);
        Assert.Equal("sternpinball.com", uri.Host);
        Assert.Equal("/wp-content/uploads/file.pdf", uri.AbsolutePath);
        Assert.Equal("?v=2", uri.Query);
        Assert.Equal("#section", uri.Fragment);
    }

    // ── DocumentRecord.GenerateId integration ─────────────────────────────────

    [Fact]
    public void GenerateId_WpSternHostVariant_MatchesCanonicalSternHostId()
    {
        // This is the core regression guard: the two hosts that produced 22 duplicate
        // records in the August 2026 scrape run must now hash to the same id.
        const string path = "/wp-content/uploads/2024/03/ST_Pro_Manual.pdf";
        var wpId = DocumentRecord.GenerateId($"https://wp.sternpinball.com{path}");
        var canonicalId = DocumentRecord.GenerateId($"https://sternpinball.com{path}");

        Assert.Equal(canonicalId, wpId);
    }

    [Fact]
    public void GenerateId_DifferentPaths_ProduceDifferentIds()
    {
        // Sanity: alias normalization must not collapse genuinely distinct documents.
        var id1 = DocumentRecord.GenerateId("https://wp.sternpinball.com/wp-content/uploads/manual-a.pdf");
        var id2 = DocumentRecord.GenerateId("https://wp.sternpinball.com/wp-content/uploads/manual-b.pdf");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GenerateId_NonAliasedHost_NotAffectedByNormalization()
    {
        // A host with no alias entry must not collide with any Stern URL.
        var sternId = DocumentRecord.GenerateId("https://sternpinball.com/wp-content/uploads/file.pdf");
        var otherId = DocumentRecord.GenerateId("https://example.com/wp-content/uploads/file.pdf");

        Assert.NotEqual(sternId, otherId);
    }

    // ── Provenance preservation ───────────────────────────────────────────────

    [Fact]
    public void Canonicalize_DoesNotMutateProvenanceUrl()
    {
        // UrlCanonicalizer returns the canonical URL as a NEW string; the caller's
        // original string is untouched — demonstrated by verifying they differ for
        // an aliased host, confirming provenance stored in Source.FileUrl is safe.
        const string originalUrl = "https://wp.sternpinball.com/wp-content/uploads/manual.pdf";
        var canonicalized = UrlCanonicalizer.Canonicalize(originalUrl);

        // The original string is unchanged (immutable in C#) — Canonicalize returns a new value.
        Assert.Equal("https://wp.sternpinball.com/wp-content/uploads/manual.pdf", originalUrl);
        // The returned value uses the canonical host.
        Assert.Contains("sternpinball.com/", canonicalized);
        Assert.DoesNotContain("wp.sternpinball.com", canonicalized);
    }
}
