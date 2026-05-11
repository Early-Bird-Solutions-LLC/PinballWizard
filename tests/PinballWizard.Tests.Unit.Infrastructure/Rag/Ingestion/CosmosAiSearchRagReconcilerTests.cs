using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Rag.Ingestion;

// Pure-function tests for CosmosAiSearchRagReconciler. The end-to-end
// I/O path (Cosmos sample query → SearchClient filter call → telemetry
// emission → result aggregation) is covered by the live-gated tests in
// CosmosChangeFeedHostedServiceLiveTests; this class exhaustively pins
// the drift-classification logic and the OData escape helper without
// requiring a Cosmos emulator or AI Search live endpoint.
public sealed class CosmosAiSearchRagReconcilerTests
{
    [Fact]
    public void ClassifyDrift_NullActualCount_ReturnsVerifyFailed()
    {
        // Verify call itself failed → we don't know the state. Counted
        // as sampled-but-unclassified, NOT as drift (a noisy AI Search
        // outage shouldn't make every document look broken).
        var stateRow = NewStateRow(chunkCount: 5);
        var result = CosmosAiSearchRagReconciler.ClassifyDrift(stateRow, actualCount: null);
        Assert.Equal(CosmosAiSearchRagReconciler.DriftClassification.VerifyFailed, result);
    }

    [Fact]
    public void ClassifyDrift_ZeroActualCount_ReturnsMissing()
    {
        // Recorded chunks but AI Search has none → full write loss.
        var stateRow = NewStateRow(chunkCount: 5);
        var result = CosmosAiSearchRagReconciler.ClassifyDrift(stateRow, actualCount: 0);
        Assert.Equal(CosmosAiSearchRagReconciler.DriftClassification.Missing, result);
    }

    [Fact]
    public void ClassifyDrift_CountMatches_ReturnsMatch()
    {
        var stateRow = NewStateRow(chunkCount: 5);
        var result = CosmosAiSearchRagReconciler.ClassifyDrift(stateRow, actualCount: 5);
        Assert.Equal(CosmosAiSearchRagReconciler.DriftClassification.Match, result);
    }

    [Fact]
    public void ClassifyDrift_CountDiffers_ReturnsCountMismatch()
    {
        // Recorded 5 chunks; AI Search holds 3. Partial write loss.
        var stateRow = NewStateRow(chunkCount: 5);
        var result = CosmosAiSearchRagReconciler.ClassifyDrift(stateRow, actualCount: 3);
        Assert.Equal(CosmosAiSearchRagReconciler.DriftClassification.CountMismatch, result);
    }

    [Fact]
    public void ClassifyDrift_RecordedZeroChunksWithNonZeroActual_ReturnsMatch()
    {
        // The state row records `ChunkCount=0` for the chunker-produced-
        // zero-chunks defensive path; per the pipeline contract this is
        // legitimately empty in the index. Pin that this case is NOT
        // classified as count_mismatch — otherwise every recorded-but-
        // empty document would surface as drift on every reconcile run.
        var stateRow = NewStateRow(chunkCount: 0);
        var result = CosmosAiSearchRagReconciler.ClassifyDrift(stateRow, actualCount: 7);
        Assert.Equal(CosmosAiSearchRagReconciler.DriftClassification.Match, result);
    }

    [Fact]
    public void ClassifyDrift_RecordedZeroAndActualZero_ReturnsMissing()
    {
        // Defensive corner: ChunkCount=0 + actual=0. The actual-is-zero
        // branch fires first and classifies as Missing. This is the
        // *less* common interpretation (the recorded-zero case is
        // intentionally empty) but it surfaces an empty record as a
        // drift signal worth investigating; operators can classify it
        // by-row from the dead-letter / log signal once they see the
        // count.
        var stateRow = NewStateRow(chunkCount: 0);
        var result = CosmosAiSearchRagReconciler.ClassifyDrift(stateRow, actualCount: 0);
        Assert.Equal(CosmosAiSearchRagReconciler.DriftClassification.Missing, result);
    }

    [Theory]
    [InlineData("doc_simple", "doc_simple")]
    [InlineData("doc_with'quote", "doc_with''quote")]
    [InlineData("doc_with''already_doubled", "doc_with''''already_doubled")]
    [InlineData("", "")]
    public void EscapeForOData_DoublesSingleQuotes(string input, string expected)
    {
        // OData V4 string-literal escape: ' → ''. The actual document
        // IDs in this project are deterministic SHA-derived hex prefixed
        // with `doc_`, so single quotes are not expected in production —
        // the escape is defense-in-depth for a future schema change
        // that might relax the ID convention.
        Assert.Equal(expected, CosmosAiSearchRagReconciler.EscapeForOData(input));
    }

    private static IndexStateDocument NewStateRow(int chunkCount) => new()
    {
        Id = IndexStateDocument.RowIdPrefix + "test_doc",
        DocumentId = "test_doc",
        LastIndexedHash = "hash-test",
        ChunkCount = chunkCount,
        FailureCount = 0,
        RecordedUtc = DateTimeOffset.UtcNow,
    };
}
