using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

// Unit tests for IndexStateDocument row-id composition. The re-attribution
// fix (Phase 3) re-keys the rag_index_state row on (document_id, machine_id)
// so a document re-attributed to a different machine is a fresh key and
// re-indexes instead of short-circuiting on the stale document-only hash.
public sealed class IndexStateDocumentTests
{
    [Fact]
    public void ComposeRowId_IncludesBothDocumentAndMachine()
    {
        var id = IndexStateDocument.ComposeRowId("doc_abc", "mch_xyz");
        Assert.Equal("idx_doc_abc_mch_xyz", id);
    }

    [Fact]
    public void ComposeRowId_SameDocumentDifferentMachine_ProducesDistinctIds()
    {
        // The whole point of the re-key: one document fanned out to two
        // machines must occupy two independent state rows so a
        // re-attribution (same content hash, new machine_id) is a fresh
        // key rather than a stale-hash short-circuit.
        var a = IndexStateDocument.ComposeRowId("doc_1", "mch_a");
        var b = IndexStateDocument.ComposeRowId("doc_1", "mch_b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComposeRowId_NullDocumentId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => IndexStateDocument.ComposeRowId(null!, "mch_a"));
    }

    [Fact]
    public void ComposeRowId_NullMachineId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => IndexStateDocument.ComposeRowId("doc_1", null!));
    }
}
