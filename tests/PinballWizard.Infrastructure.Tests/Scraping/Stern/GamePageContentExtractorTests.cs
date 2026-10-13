using AngleSharp.Html.Parser;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

public sealed class GamePageContentExtractorTests
{
    private static AngleSharp.Dom.IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    [Fact]
    public void ExtractTrailerUrl_FromYouTubeIframe_Normalized()
    {
        var doc = Parse("""<div><iframe src="https://www.youtube.com/embed/78q_9-6PBSY?rel=0"></iframe></div>""");
        Assert.Equal("https://www.youtube.com/watch?v=78q_9-6PBSY", GamePageContentExtractor.ExtractTrailerUrl(doc));
    }

    [Fact]
    public void ExtractTrailerUrl_None_ReturnsNull()
    {
        Assert.Null(GamePageContentExtractor.ExtractTrailerUrl(Parse("<div>no video</div>")));
    }

    [Fact]
    public void ExtractAccessories_FromShopSection_NameAndPriceAndUrl()
    {
        var html = """
        <section><h2>Stern Shop</h2>
          <a href="https://shop.sternpinball.com/collections/pokemon-accessories-and-parts">View All</a>
          <a href="https://shop.sternpinball.com/products/pokemon-by-stern-pinball-topper">
            <img src="https://cdn/topper.jpg"/>
            <span>Pokémon by Stern Pinball Topper</span><span>$1,499.99</span>
          </a>
        </section>
        """;
        var doc = Parse(html);
        var items = GamePageContentExtractor.ExtractAccessories(doc);
        var topper = Assert.Single(items);
        Assert.Equal("Pokémon by Stern Pinball Topper", topper.Name);
        Assert.Equal("$1,499.99", topper.Price);
        Assert.Equal("https://shop.sternpinball.com/products/pokemon-by-stern-pinball-topper", topper.ProductUrl);
        Assert.Equal("https://shop.sternpinball.com/collections/pokemon-accessories-and-parts",
            GamePageContentExtractor.ExtractShopCollectionUrl(doc));
    }

    [Fact]
    public void ExtractOverviewProse_JoinsDescriptiveParagraphs()
    {
        var html = """
        <div class="game-content">
          <p>Players shoot the Poké Ball to catch Pokémon.</p>
          <p>Premium and Limited Edition games include an interactive electromagnet.</p>
        </div>
        """;
        var prose = GamePageContentExtractor.ExtractOverviewProse(Parse(html));
        Assert.Contains("catch Pokémon", prose, StringComparison.Ordinal);
        Assert.Contains("electromagnet", prose, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractOverviewProse_ScopesToMain_ExcludesCookieAndNavBoilerplate()
    {
        var html = """
        <html><body>
          <div class="cookie-banner"><p>With your consent, we and other third-party service providers may store cookies on your browser to personalize your experience.</p></div>
          <main>
            <p>Today, Stern Pinball revealed Pokémon by Stern Pinball, bringing the thrill of catching and battling Pokémon to the silverball arena.</p>
            <p>Premium and Limited Edition games include an interactive electromagnet that adds chaos to the battle arena.</p>
          </main>
          <footer><p>Sign up for Pokémon by Stern Pinball updates and never miss a new release announcement from us.</p></footer>
        </body></html>
        """;
        var prose = GamePageContentExtractor.ExtractOverviewProse(Parse(html));
        Assert.Contains("catching and battling Pokémon", prose, StringComparison.Ordinal);   // game prose kept
        Assert.Contains("interactive electromagnet", prose, StringComparison.Ordinal);       // edition delta kept
        Assert.DoesNotContain("consent", prose, StringComparison.Ordinal);                   // cookie text excluded
        Assert.DoesNotContain("Sign up", prose, StringComparison.Ordinal);                   // footer excluded
    }

    [Fact]
    public void ExtractOverviewProse_DropsConsentBannerInsideMain()
    {
        // The live Iron Maiden overview was the CMP banner, which sits in the
        // same content root as the game copy. Scoping to <main> is not enough.
        var html = """
        <html><body><main>
          <p>With your consent, we and other third-party service providers may store cookies on your browser to personalize your experience.</p>
          <p>Iron Maiden: Legacy of the Beast brings the band's iconography to the playfield in a SPIKE-2 machine.</p>
        </main></body></html>
        """;
        var prose = GamePageContentExtractor.ExtractOverviewProse(Parse(html));
        Assert.Contains("Legacy of the Beast", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("consent", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("cookies", prose, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractOverviewProse_BannerOnly_ReturnsNull()
    {
        var html = """
        <html><body><main>
          <p>With your consent, we and other third-party service providers may store cookies on your browser to personalize your experience.</p>
          <p>Your privacy choices and manage preferences are available in the cookie settings panel on this page.</p>
        </main></body></html>
        """;
        Assert.Null(GamePageContentExtractor.ExtractOverviewProse(Parse(html)));
    }
}
