using System.Reflection;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Coverage;
using Xunit;

namespace PinballWizard.Application.Tests.Rag.Coverage;

public sealed class RagSourceCatalogTests
{
    // Drift guard: every IngestionSourceIds constant that produces RAG-indexed
    // content must have a RagSource, so adding a source without registering it
    // for coverage fails here. (PinballMap is data-only, not RAG-indexed — it is
    // the one documented exclusion.)
    [Fact]
    public void Catalog_CoversEveryIngestionSourceId_ExceptDocumentedExclusions()
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            IngestionSourceIds.PinballMap, // location data, never RAG-indexed
            // sub-doc sources indexed under the parent manufacturer's (manufacturer + doc_) recognizer —
            // covered by the parent RagSource, not separately distinguishable in the index
            IngestionSourceIds.JjpSupportDocs,
            IngestionSourceIds.ApBulletins,
            IngestionSourceIds.SpookySupport,
            IngestionSourceIds.PinballBrothersDocuments,
        };

        var allSourceIds = typeof(IngestionSourceIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Where(id => !excluded.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        var covered = RagSourceCatalog.All.Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal);

        var missing = allSourceIds.Except(covered).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0, $"IngestionSourceIds missing from RagSourceCatalog: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Matches_SternScraper_ExcludesKineticistChunkWithSternManufacturer()
    {
        var stern = RagSourceCatalog.All.Single(s => s.SourceId == IngestionSourceIds.Stern);
        Assert.True(stern.Matches("doc_abc123", "Stern"));
        Assert.False(stern.Matches("kineticist_godzilla_GRBN", "Stern")); // synthesized, not scraped
    }

    [Fact]
    public void Matches_Kineticist_MatchesByPrefixRegardlessOfManufacturer()
    {
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == IngestionSourceIds.Kineticist);
        Assert.True(kin.Matches("kineticist_godzilla_GRBN", "Stern"));
        Assert.False(kin.Matches("doc_abc123", "Stern"));
    }

    // MatchesRetrieval deliberately answers a different question than Matches: not
    // "did this chunk come from this ingestion source" but "would a user querying
    // this cell actually receive this chunk". The pair below pins that difference —
    // the manufacturer-backed case is the #842 regression itself.

    [Fact]
    public void MatchesRetrieval_ManufacturerBackedSource_AcceptsSynthesizedChunkForSameManufacturer()
    {
        var jjp = RagSourceCatalog.All.Single(s => s.SourceId == IngestionSourceIds.Jjp);

        // The #842 false positive: a user asking about a JJP game DOES receive this
        // TiltForums chunk, so the probe must not report the cell as unretrievable.
        Assert.True(jjp.MatchesRetrieval("tiltforums_elton_john_abc123", "Jersey Jack Pinball"));
        Assert.True(jjp.MatchesRetrieval("doc_abc123", "Jersey Jack Pinball"));

        // The relaxation is bounded by manufacturer — a Stern chunk is still a miss.
        Assert.False(jjp.MatchesRetrieval("tiltforums_godzilla_xyz", "Stern"));
        Assert.False(jjp.MatchesRetrieval("doc_abc123", "Stern"));
    }

    [Fact]
    public void MatchesRetrieval_PrefixOnlySource_StillRequiresThePrefix()
    {
        var kin = RagSourceCatalog.All.Single(s => s.SourceId == IngestionSourceIds.Kineticist);

        // Synthesized sources carry the game's manufacturer, so the prefix remains
        // the only reliable identifier — the relaxation must NOT apply here.
        Assert.True(kin.MatchesRetrieval("kineticist_godzilla_GRBN", "Stern"));
        Assert.False(kin.MatchesRetrieval("doc_abc123", "Stern"));
    }

    [Fact]
    public void MatchesRetrieval_SourceWithNeitherPrefixNorManufacturer_FailsOpen()
    {
        // Guards the fail-open branch, which no well-formed catalog entry reaches.
        // Fail-open is the deliberate choice: a malformed entry should not manufacture
        // an unconditional coverage miss that looks like a real corpus gap.
        var malformed = new RagSource("malformed", [], DocumentIdPrefix: null, ExpectedNonEmpty: false);

        Assert.True(malformed.MatchesRetrieval("anything_at_all", "Any Manufacturer"));
    }
}
