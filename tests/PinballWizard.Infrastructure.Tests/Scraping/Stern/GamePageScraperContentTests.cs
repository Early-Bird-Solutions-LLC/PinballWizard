using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

public sealed class GamePageScraperContentTests
{
    [Fact]
    public void ApplyPageContent_PopulatesOverviewTrailerAccessories()
    {
        var html = """
        <html><body>
          <iframe src="https://www.youtube.com/embed/78q_9-6PBSY"></iframe>
          <p>Players shoot the illuminated Poké Ball to catch Pokémon and battle Team Rocket.</p>
          <a href="https://shop.sternpinball.com/collections/pokemon-accessories-and-parts">View All</a>
          <a href="https://shop.sternpinball.com/products/pokemon-topper"><span>Pokémon Topper</span><span>$1,499.99</span></a>
        </body></html>
        """;
        var doc = new HtmlParser().ParseDocument(html);
        var record = new GameRecord { GameId = "game_pokemon", Title = "Pokémon", Slug = "pokemon", GamePageUrl = "https://sternpinball.com/game/pokemon/" };

        var enriched = GamePageScraper.ApplyPageContent(record, doc);

        Assert.Contains("catch Pokémon", enriched.OverviewProse!, StringComparison.Ordinal);
        Assert.Equal("https://www.youtube.com/watch?v=78q_9-6PBSY", enriched.TrailerUrl);
        Assert.Equal("Pokémon Topper", enriched.Accessories.Single().Name);
        Assert.Equal("https://shop.sternpinball.com/collections/pokemon-accessories-and-parts", enriched.ShopCollectionUrl);
    }
}
