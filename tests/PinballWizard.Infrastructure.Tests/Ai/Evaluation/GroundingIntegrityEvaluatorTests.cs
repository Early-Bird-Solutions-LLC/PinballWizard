using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// Verifies the citation-source-type gate introduced for issue #532: a Rules or
// Repair answer grounded only by a getMachineByTitle identity record (MachineRecord)
// is NOT backed by corpus content. The evaluator scores it 0.0 so the harness
// flags the citation-provenance gap.
//
// The harness controls applicability (only calls Compute for non-refused
// Rules/Repair rows); these tests exercise Compute directly.
public sealed class GroundingIntegrityEvaluatorTests
{
    private readonly GroundingIntegrityEvaluator _evaluator = new();

    [Fact]
    public void Compute_EmptyCitations_Returns0()
    {
        // No citations at all → no corpus chunk → ungrounded.
        Assert.Equal(0.0, _evaluator.Compute([]));
    }

    [Fact]
    public void Compute_OnlyMachineRecord_Returns0()
    {
        // An OPDB identity record (from getMachineByTitle) confirms the machine
        // exists but is NOT a source citation for gameplay or repair content —
        // the core failure mode from issue #532.
        var citations = new List<Citation>
        {
            new("Iron Maiden", "https://opdb.org/search?q=IMwi-M1001",
                MachineId: "IMwi-M1001",
                SourceType: CitationSourceType.MachineRecord),
        };
        Assert.Equal(0.0, _evaluator.Compute(citations));
    }

    [Fact]
    public void Compute_OneCorpusChunk_Returns1()
    {
        // A single searchCorpus hit satisfies the grounding-integrity requirement.
        var citations = new List<Citation>
        {
            new("Iron Maiden Rulesheet", "https://sternpinball.com/manuals/iron-maiden-rules.pdf",
                DocumentChunkId: "doc_abc123",
                SourceType: CitationSourceType.CorpusChunk),
        };
        Assert.Equal(1.0, _evaluator.Compute(citations));
    }

    [Fact]
    public void Compute_MachineRecordPlusCorpusChunk_Returns1()
    {
        // Typical production answer: getMachineByTitle gives the MachineRecord,
        // searchCorpus gives the CorpusChunk. Integrity passes — the corpus chunk
        // is what actually grounds the gameplay content.
        var citations = new List<Citation>
        {
            new("Iron Maiden", "https://opdb.org/search?q=IMwi-M1001",
                MachineId: "IMwi-M1001",
                SourceType: CitationSourceType.MachineRecord),
            new("Iron Maiden Rulesheet", "https://sternpinball.com/manuals/iron-maiden-rules.pdf",
                DocumentChunkId: "doc_abc123",
                SourceType: CitationSourceType.CorpusChunk),
        };
        Assert.Equal(1.0, _evaluator.Compute(citations));
    }

    [Fact]
    public void Compute_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _evaluator.Compute(null!));
    }
}
