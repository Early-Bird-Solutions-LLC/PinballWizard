using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Core.Tests.Models;

public sealed class GameRecordFieldsTests
{
    [Fact]
    public void GameRecord_NewContentFields_DefaultEmpty()
    {
        var g = new GameRecord
        {
            GameId = "game_pokemon", Title = "Pokémon", Slug = "pokemon",
            GamePageUrl = "https://sternpinball.com/game/pokemon/"
        };
        Assert.Null(g.OverviewProse);
        Assert.Null(g.TrailerUrl);
        Assert.Null(g.ShopCollectionUrl);
        Assert.Empty(g.Accessories);
    }

    [Fact]
    public void Machine_NewContentFields_DefaultEmpty()
    {
        var m = new Machine
        {
            Id = "GweeP-MW95j", PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla"
        };
        Assert.Null(m.OverviewProse);
        Assert.Null(m.OverviewSourceUrl);
        Assert.Null(m.TrailerUrl);
        Assert.Empty(m.Accessories);
    }
}
