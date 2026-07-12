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
}
