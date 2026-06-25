using PinballWizard.Infrastructure.Scraping.WooCommerce;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.WooCommerce;

public sealed class WooCommerceProductMapperTests
{
    [Fact]
    public void MapToGameRecord_WithPrice_BuildsCorrectRecord()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 243,
            Name = "Jim Henson's Labyrinth",
            Permalink = "https://shop.kollectfun.com/product/jim-hensons-labyrinth/",
            Prices = new WooCommerceStorePricesDto { Price = "1060000", CurrencyMinorUnit = 2 },
            IsInStock = true,
            ShortDescription = "Limited edition game.",
            Images = [new WooCommerceStoreImageDto { Src = "https://shop.kollectfun.com/lab.png" }],
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_barrelsoffun_", "barrelsoffun_machines_category");

        Assert.NotNull(record);
        Assert.Equal("jim-hensons-labyrinth", record.Slug);
        Assert.Equal("game_barrelsoffun_jim-hensons-labyrinth", record.GameId);
        Assert.Contains("barrelsoffun_machines_category", record.DiscoveredOn);
        Assert.Equal("in_stock", record.Status);
        Assert.Single(record.Editions);
        Assert.Equal("10600.00", record.Editions[0].Msrp);
    }

    [Fact]
    public void MapToGameRecord_PriceZero_EmitsEmptyEditions()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 1,
            Name = "Elemental Pinball",
            Permalink = "https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/elemental/",
            Prices = new WooCommerceStorePricesDto { Price = "0", CurrencyMinorUnit = 2 },
            IsInStock = true,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_multimorphic_", "multimorphic_game_kits");

        Assert.NotNull(record);
        Assert.Empty(record.Editions);
    }

    [Fact]
    public void MapToGameRecord_IsInStockFalse_StatusIsOutOfStock()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 2,
            Name = "Godzilla",
            Permalink = "https://shop.kollectfun.com/product/godzilla/",
            Prices = new WooCommerceStorePricesDto { Price = "800000", CurrencyMinorUnit = 2 },
            IsInStock = false,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_barrelsoffun_", "barrelsoffun_machines_category");

        Assert.NotNull(record);
        Assert.Equal("out_of_stock", record.Status);
        Assert.Equal("out_of_stock", record.Editions[0].Availability);
    }

    [Fact]
    public void MapToGameRecord_PriceMinorUnit0_CorrectConversion()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 3,
            Name = "Test Machine",
            Permalink = "https://example.com/product/test-machine/",
            Prices = new WooCommerceStorePricesDto { Price = "30", CurrencyMinorUnit = 0 },
            IsInStock = true,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_test_", "test_tag");

        Assert.NotNull(record);
        Assert.Single(record.Editions);
        Assert.Equal("30.00", record.Editions[0].Msrp);
    }

    [Fact]
    public void MapToGameRecord_SlugFromPermalink()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 4,
            Name = "Portal Extended Game Kit",
            Permalink = "https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/portal-extended-game-kit/",
            Prices = new WooCommerceStorePricesDto { Price = "150000", CurrencyMinorUnit = 2 },
            IsInStock = true,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_multimorphic_", "multimorphic_game_kits");

        Assert.NotNull(record);
        Assert.Equal("portal-extended-game-kit", record.Slug);
        Assert.Equal("game_multimorphic_portal-extended-game-kit", record.GameId);
    }

    [Fact]
    public void MapToGameRecord_HtmlDescription_IsStripped()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 5,
            Name = "Some Machine",
            Permalink = "https://example.com/product/some-machine/",
            Prices = new WooCommerceStorePricesDto { Price = "500", CurrencyMinorUnit = 2 },
            ShortDescription = "<p><em>Bold</em> text</p>",
            IsInStock = true,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_test_", "test_tag");

        Assert.NotNull(record);
        Assert.Single(record.Editions);
        Assert.Equal("Bold text", record.Editions[0].Description);
    }

    [Fact]
    public void MapToGameRecord_NullPermalink_ReturnsNull()
    {
        var product = new WooCommerceStoreProductDto
        {
            Id = 6,
            Name = "Missing Permalink",
            Permalink = "",
            IsInStock = true,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_test_", "test_tag");

        Assert.Null(record);
    }

    [Fact]
    public void MapToGameRecord_FullProvenance()
    {
        var permalink = "https://shop.kollectfun.com/product/labyrinth/";
        var product = new WooCommerceStoreProductDto
        {
            Id = 7,
            Name = "Labyrinth",
            Permalink = permalink,
            Prices = new WooCommerceStorePricesDto { Price = "1000", CurrencyMinorUnit = 2 },
            IsInStock = true,
        };

        var record = WooCommerceProductMapper.MapToGameRecord(product, "game_barrelsoffun_", "barrelsoffun_machines_category");

        Assert.NotNull(record);
        Assert.Equal(permalink, record.Source!.ScrapedFrom);
        Assert.Equal("barrelsoffun_machines_category", record.DiscoveredOn[0]);
        Assert.Equal("game_barrelsoffun_labyrinth", record.GameId);
        Assert.NotEqual(default, record.Source.ScrapedAt);
    }
}
