using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Infrastructure.Scraping.OpenGraph;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.OpenGraph;

/// <summary>
/// Tests for <see cref="OpenGraphExtractor"/>. Pin the byte-for-byte
/// behaviour the previously-private <c>GetMetaContent</c> copies in
/// JJP / BoF / Multimorphic had so the refactor cannot silently drift
/// the consumer fallback chains.
/// </summary>
public sealed class OpenGraphExtractorTests
{
    private static readonly HtmlParser Parser = new();

    [Fact]
    public void GetMetaContent_PropertyAttribute_ReturnsContent()
    {
        // Spec form (OpenGraph + RDFa) — the primary path every storefront
        // hits when the page is well-formed.
        var doc = Parse("""<html><head><meta property="og:title" content="Hello World"></head></html>""");

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void GetMetaContent_NameAttribute_FallsBack()
    {
        // Loose form — some sites publish OG keys under name= instead of
        // property=. The fallback must work or we lose the title on those
        // sites.
        var doc = Parse("""<html><head><meta name="og:title" content="From Name"></head></html>""");

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Equal("From Name", result);
    }

    [Fact]
    public void GetMetaContent_BothAttributes_PrefersProperty()
    {
        // When both forms exist on the same page, property= wins. Pinning
        // this so a future re-ordering of the QuerySelector chain doesn't
        // silently swap the precedence — sites occasionally publish
        // contradictory values under each form, and OG spec says property
        // is canonical.
        var doc = Parse("""
            <html><head>
              <meta property="og:title" content="Property Wins">
              <meta name="og:title" content="Name Loses">
            </head></html>
            """);

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Equal("Property Wins", result);
    }

    [Fact]
    public void GetMetaContent_Missing_ReturnsNull()
    {
        var doc = Parse("""<html><head></head></html>""");

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Null(result);
    }

    [Fact]
    public void GetMetaContent_PropertyExistsButNoContentAttribute_ReturnsNull()
    {
        // Malformed but possible: <meta property="og:title"> with no
        // content attribute. AngleSharp's GetAttribute returns null;
        // the helper must propagate null without throwing.
        var doc = Parse("""<html><head><meta property="og:title"></head></html>""");

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Null(result);
    }

    [Fact]
    public void GetMetaContent_ContentWithSurroundingWhitespace_IsTrimmed()
    {
        // Sites occasionally leak indentation into content attributes.
        // The previously-private copies all trimmed; the shared helper
        // must too or downstream IsNullOrWhiteSpace checks behave
        // differently.
        var doc = Parse("""<html><head><meta property="og:title" content="  Trimmed Title  "></head></html>""");

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Equal("Trimmed Title", result);
    }

    [Fact]
    public void GetMetaContent_EmptyContent_ReturnsEmptyString()
    {
        // Behaviour parity with the previous private implementations:
        // content="" returns "" (not null). The consumer fallback chain
        // (`product?.Name ?? GetMetaContent(...) ?? h1.TextContent`)
        // relies on the ?? operator triggering only on null — an empty
        // string short-circuits the chain. Changing this to return null
        // would silently change every consumer's fallback behaviour.
        var doc = Parse("""<html><head><meta property="og:title" content=""></head></html>""");

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:title");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetMetaContent_FirstMatchWins_WhenMultipleSameProperty()
    {
        // Multiple matching <meta> elements (rare but possible — duplicate
        // og:image is the most common case). QuerySelector returns the
        // first; pin it so we don't accidentally switch to QuerySelectorAll
        // and change behaviour.
        var doc = Parse("""
            <html><head>
              <meta property="og:image" content="first.jpg">
              <meta property="og:image" content="second.jpg">
            </head></html>
            """);

        var result = OpenGraphExtractor.GetMetaContent(doc, "og:image");

        Assert.Equal("first.jpg", result);
    }

    [Fact]
    public void GetMetaContent_NullDoc_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OpenGraphExtractor.GetMetaContent(null!, "og:title"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetMetaContent_NullOrEmptyProperty_Throws(string? property)
    {
        var doc = Parse("""<html/>""");

        Assert.ThrowsAny<ArgumentException>(() =>
            OpenGraphExtractor.GetMetaContent(doc, property!));
    }

    private static IHtmlDocument Parse(string html) => Parser.ParseDocument(html);
}
