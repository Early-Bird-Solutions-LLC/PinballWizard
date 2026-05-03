namespace PinballWizard.Application.Sync;

/// <summary>
/// Derives the canonical manufacturer partition key for a
/// <c>GameRecord</c> based on the prefix of its <c>GameId</c>. The
/// keys returned here match
/// <c>OpdbMachineMapper.NormalizeManufacturerKey</c> exactly so a
/// scraped record lands in the same Cosmos partition the OPDB sync
/// wrote that manufacturer's records to.
/// </summary>
/// <remarks>
/// The Stern scraper predates the multi-manufacturer fan-out and
/// uses an unprefixed <c>game_{slug}</c> — it is the implicit
/// default when no other prefix matches. Per ADR 0011.
/// </remarks>
public static class ScraperManufacturerKey
{
    /// <summary>Manufacturer key for Stern (the original / default).</summary>
    public const string Stern = "stern";

    /// <summary>Manufacturer key for Jersey Jack Pinball.</summary>
    public const string Jjp = "jjp";

    /// <summary>Manufacturer key for American Pinball.</summary>
    public const string AmericanPinball = "americanpinball";

    /// <summary>Manufacturer key for Spooky Pinball.</summary>
    public const string Spooky = "spooky";

    /// <summary>Manufacturer key for Pinball Brothers.</summary>
    public const string PinballBrothers = "pinballbrothers";

    /// <summary>Manufacturer key for Barrels of Fun.</summary>
    public const string BarrelsOfFun = "barrelsoffun";

    /// <summary>
    /// Returns the manufacturer key for a <c>GameRecord</c> id.
    /// Returns null only if <paramref name="gameId"/> does not start
    /// with the <c>game_</c> sentinel prefix.
    /// </summary>
    public static string? FromGameId(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        const string prefix = "game_";
        if (!gameId.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = gameId[prefix.Length..];
        if (rest.StartsWith("jjp_", StringComparison.Ordinal)) return Jjp;
        if (rest.StartsWith("ap_", StringComparison.Ordinal)) return AmericanPinball;
        if (rest.StartsWith("spooky_", StringComparison.Ordinal)) return Spooky;
        if (rest.StartsWith("pinballbrothers_", StringComparison.Ordinal)) return PinballBrothers;
        if (rest.StartsWith("barrelsoffun_", StringComparison.Ordinal)) return BarrelsOfFun;

        // Stern was the original scraper and uses unprefixed game_{slug}.
        return Stern;
    }
}
