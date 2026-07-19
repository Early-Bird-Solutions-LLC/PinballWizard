namespace PinballWizard.Application.Resolution;

// Canonical manufacturer → match-token mapping for building ManufacturerPrefixed variants.
// Lifted from OpdbMachineMapper (Infrastructure) so Application can build variants without
// depending on Infrastructure (Clean Architecture: Application must not reference Infrastructure).
// OpdbMachineMapper.GetMatchTokens now delegates here; one source of truth, one direction.
//
// Tokens for each key: every natural prefix a user might type for that manufacturer,
// plus the canonical key itself. "jjp" resolves "jjp toy story 4", "jersey toy story 4",
// "jack toy story 4" via the fast point-read path (not the slow cross-partition fallback).
public static class ManufacturerMatchTokens
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Tokens =
        new(StringComparer.Ordinal)
        {
            ["stern"]           = ["stern"],
            ["jjp"]             = ["jjp", "jersey", "jack"],
            ["americanpinball"] = ["americanpinball", "american", "pinball", "ap"],
            ["spooky"]          = ["spooky"],
            ["multimorphic"]    = ["multimorphic"],
            ["cgc"]             = ["cgc", "chicago", "gaming"],
            ["haggis"]          = ["haggis"],
            ["pinballbrothers"] = ["pinballbrothers", "pinball", "brothers", "pb"],
            ["dutch"]           = ["dutch"],
            ["barrelsoffun"]    = ["barrelsoffun", "barrels", "fun", "bof"],
        };

    public static IReadOnlyList<string> GetMatchTokens(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Tokens.TryGetValue(key, out var tokens) ? tokens : [key];
    }
}
