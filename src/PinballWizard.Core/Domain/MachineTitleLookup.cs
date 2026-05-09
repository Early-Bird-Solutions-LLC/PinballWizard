using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// Title→OPDB-ID materialized view for the user-delight path per
/// <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 4</see>.
/// One row per normalized pinball-machine title; each row carries the
/// list of OPDB IDs (and parallel manufacturer keys) that share that
/// title. The Wizard's <c>getMachineByTitle</c> function tool replaces
/// its prior cross-partition <c>STRINGEQUALS</c> query with two point
/// reads: first this container by normalized title, then
/// <see cref="Machine"/> by OPDB ID + manufacturer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> equals <see cref="PartitionKey"/> (both are the
/// normalized title) so the read is a pure point-lookup with no
/// secondary index. The single writer is <c>OpdbSyncService</c>
/// (per <see href="../../../docs/adr/0011-scraper-machine-reconciliation.md">ADR-0011</see>);
/// dual-writes from there give read-your-writes via Cosmos session
/// consistency on the same client. When a 2nd writer of `machines`
/// lands, this projection should be rebuilt via the W3-2 Change Feed
/// processor pattern instead — see ADR-0025 § 1.
/// </para>
/// <para>
/// Title collisions (e.g. multiple "Godzilla" releases) are stored as
/// parallel <see cref="OpdbIds"/> + <see cref="Manufacturers"/> arrays;
/// the tool returns the first pair, matching the existing
/// first-OPDB-ordered-hit semantics of
/// <c>MachineRepository.QueryByTitleAsync</c>.
/// </para>
/// </remarks>
public sealed class MachineTitleLookup : IEntity
{
    /// <summary>Document id — the normalized title (= partition key value).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Partition key value — the normalized title. Cosmos partition path
    /// is <c>/normalizedTitle</c>; the JSON property is named
    /// <c>normalizedTitle</c> to match.
    /// </summary>
    [JsonPropertyName("normalizedTitle")]
    public required string PartitionKey { get; init; }

    /// <summary>
    /// OPDB IDs of machines whose normalized title matches
    /// <see cref="Id"/>. Parallel to <see cref="Manufacturers"/> by
    /// index. Use <see cref="UpsertEntry"/> / <see cref="RemoveEntry"/>
    /// to mutate so the two arrays stay in lock-step.
    /// </summary>
    [JsonPropertyName("opdbIds")]
    public List<string> OpdbIds { get; set; } = [];

    /// <summary>
    /// Manufacturer keys (e.g. <c>stern</c>, <c>jjp</c>) for the
    /// machines listed in <see cref="OpdbIds"/>. Index-aligned with
    /// <see cref="OpdbIds"/>.
    /// </summary>
    [JsonPropertyName("manufacturers")]
    public List<string> Manufacturers { get; set; } = [];

    /// <summary>Last time the row was upserted by an OPDB sync run.</summary>
    [JsonPropertyName("lastSyncedUtc")]
    public DateTimeOffset LastSyncedUtc { get; set; }

    /// <summary>Cosmos system-managed _etag, populated on read.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    /// <summary>
    /// Normalize a title for lookup-row addressing. Applies
    /// case-folding (<see cref="string.ToLowerInvariant"/> after a
    /// <see cref="string.Trim()"/>) so the lookup matches Wizard inputs
    /// regardless of case, and replaces the four characters Cosmos
    /// rejects in document ids (<c>/</c>, <c>\</c>, <c>?</c>, <c>#</c>)
    /// with <c>_</c> so the normalized title is safe to use as both id
    /// and partition-key value.
    /// </summary>
    /// <remarks>
    /// The character substitution is deliberately one-way (no reverse
    /// transform) because the lookup container is a derived projection
    /// — the canonical title lives on <see cref="Machine.Title"/>; the
    /// lookup never needs to reconstruct the original. Two distinct
    /// titles that collide under the substitution (e.g. <c>AC/DC</c>
    /// vs <c>AC_DC</c>) are stored as two entries on the same row,
    /// matching the same-row-different-machines collision pattern the
    /// schema already supports.
    /// </remarks>
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

    /// <summary>
    /// Add or replace an entry for <paramref name="opdbId"/>. If the
    /// id is already present, the existing pair is removed and the
    /// new pair appended (so the <see cref="OpdbIds"/> ordering
    /// reflects insertion-order — first-seen first). Keeps the two
    /// parallel arrays consistent.
    /// </summary>
    public void UpsertEntry(string opdbId, string manufacturer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);
        var idx = OpdbIds.IndexOf(opdbId);
        if (idx >= 0)
        {
            OpdbIds.RemoveAt(idx);
            Manufacturers.RemoveAt(idx);
        }
        OpdbIds.Add(opdbId);
        Manufacturers.Add(manufacturer);
    }

    /// <summary>
    /// Remove an entry for <paramref name="opdbId"/>. Returns
    /// <c>true</c> if a pair was removed, <c>false</c> if the id was
    /// not present.
    /// </summary>
    public bool RemoveEntry(string opdbId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opdbId);
        var idx = OpdbIds.IndexOf(opdbId);
        if (idx < 0)
        {
            return false;
        }
        OpdbIds.RemoveAt(idx);
        Manufacturers.RemoveAt(idx);
        return true;
    }
}
