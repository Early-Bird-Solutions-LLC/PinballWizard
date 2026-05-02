using PinballWizard.Application.Sync;
using Xunit;

namespace PinballWizard.Scraper.Tests.Sync;

/// <summary>
/// Tests for <see cref="ScraperManufacturerKey.FromGameId"/>. The
/// returned keys must match
/// <c>OpdbMachineMapper.NormalizeManufacturerKey</c> exactly so a
/// scraped record lands in the same Cosmos partition the OPDB sync
/// wrote that manufacturer's records to. Per ADR 0011.
/// </summary>
public sealed class ScraperManufacturerKeyTests
{
    [Theory]
    [InlineData("game_jjp_dialed-in", ScraperManufacturerKey.Jjp)]
    [InlineData("game_ap_houdini", ScraperManufacturerKey.AmericanPinball)]
    [InlineData("game_spooky_beetlejuice", ScraperManufacturerKey.Spooky)]
    [InlineData("game_stranger-things", ScraperManufacturerKey.Stern)]
    [InlineData("game_jurassic-park", ScraperManufacturerKey.Stern)]
    public void FromGameId_RecognisesAllManufacturerPrefixes(string gameId, string expected)
    {
        Assert.Equal(expected, ScraperManufacturerKey.FromGameId(gameId));
    }

    [Fact]
    public void FromGameId_NoSentinelPrefix_ReturnsNull()
    {
        Assert.Null(ScraperManufacturerKey.FromGameId("foreign_id_format"));
        Assert.Null(ScraperManufacturerKey.FromGameId("xx_jjp_dialed-in"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromGameId_BlankInput_Throws(string? gameId)
    {
        Assert.ThrowsAny<ArgumentException>(() => ScraperManufacturerKey.FromGameId(gameId!));
    }

    [Fact]
    public void Constants_AreLowercaseToMatchPartitionKeys()
    {
        // Cosmos partition keys are case-sensitive at the SDK layer; OPDB
        // sync always writes lower-case. A constant change here that
        // diverged would silently send scraped data to a different
        // partition than OPDB writes to.
        Assert.Equal(ScraperManufacturerKey.Stern, ScraperManufacturerKey.Stern.ToLowerInvariant());
        Assert.Equal(ScraperManufacturerKey.Jjp, ScraperManufacturerKey.Jjp.ToLowerInvariant());
        Assert.Equal(ScraperManufacturerKey.AmericanPinball, ScraperManufacturerKey.AmericanPinball.ToLowerInvariant());
        Assert.Equal(ScraperManufacturerKey.Spooky, ScraperManufacturerKey.Spooky.ToLowerInvariant());
    }
}
