using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

public sealed class MachineTitleLookup : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("normalizedTitle")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("opdbIds")]
    public List<string> OpdbIds { get; set; } = [];

    [JsonPropertyName("manufacturers")]
    public List<string> Manufacturers { get; set; } = [];

    [JsonPropertyName("matchTokens")]
    public List<List<string>>? MatchTokens { get; set; }

    [JsonPropertyName("lastSyncedUtc")]
    public DateTimeOffset LastSyncedUtc { get; set; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public static string NormalizeTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var lowered = title.Trim().ToLowerInvariant();
        return new string([.. lowered.Select(c => c switch
        {
            '/' or '\\' or '?' or '#' => '_',
            _ => c,
        })]);
    }

    public void UpsertEntry(string opdbId, string manufacturer, IReadOnlyList<string> matchTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        ArgumentNullException.ThrowIfNull(matchTokens);

        MatchTokens ??= [];

        var idx = OpdbIds.IndexOf(opdbId);
        if (idx >= 0)
        {
            OpdbIds.RemoveAt(idx);
            Manufacturers.RemoveAt(idx);
            MatchTokens.RemoveAt(idx);
        }
        OpdbIds.Add(opdbId);
        Manufacturers.Add(manufacturer);
        MatchTokens.Add([.. matchTokens]);
    }

    public bool RemoveEntry(string opdbId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
        var idx = OpdbIds.IndexOf(opdbId);
        if (idx < 0)
        {
            return false;
        }
        // Pad MatchTokens if it was null (legacy Cosmos row written before this field existed)
        // so all three arrays stay in sync through remove + any subsequent upsert.
        if (MatchTokens is null && OpdbIds.Count > 0)
        {
            MatchTokens = [.. Enumerable.Repeat(new List<string>(), OpdbIds.Count)];
        }
        OpdbIds.RemoveAt(idx);
        Manufacturers.RemoveAt(idx);
        MatchTokens?.RemoveAt(idx);
        return true;
    }
}
