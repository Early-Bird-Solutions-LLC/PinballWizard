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
}
