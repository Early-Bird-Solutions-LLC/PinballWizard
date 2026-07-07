using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// Sanity-check on the committed ground-truth files (wizard.v1.jsonl is
// the historical set; wizard.v2.jsonl is the active one per
// EvalHarnessOptions.GroundTruthPath): they must parse cleanly, every
// row's expected_sub_agent must be a valid AgentName, and ids must be
// unique. Catches a curator-introduced regression (e.g., copy-paste
// duplicate id, typo'd sub-agent, refusal-flag contradiction) at build
// time rather than the next time the harness runs.
public sealed class EvalGroundTruthFileTests
{
    private static readonly HashSet<string> ValidSubAgents =
        new(AgentName.All, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("wizard.v1.jsonl")]
    [InlineData("wizard.v2.jsonl")]
    public void GroundTruthFile_ParsesCleanly_AndAllSubAgentsValid(string fileName)
    {
        // The test runs from the test project's bin output directory;
        // walk up to the repo root to find the data/eval files.
        var path = LocateGroundTruthFile(fileName);
        Assert.True(File.Exists(path), $"expected ground-truth file at {path}");

        var questions = EvalQuestionParser.ParseFile(path);

        Assert.NotEmpty(questions);
        Assert.True(questions.Count >= 30,
            $"ADR-0016 calls for ~30 questions; found {questions.Count}");

        foreach (var q in questions)
        {
            Assert.True(ValidSubAgents.Contains(q.ExpectedSubAgent),
                $"row '{q.Id}' has expected_sub_agent='{q.ExpectedSubAgent}' which is not a known AgentName");

            // refusal_required rows are out-of-scope by definition and
            // must not carry answer-path citations. (The parser enforces
            // this too; asserting here keeps the curator intent legible
            // even if the parser invariant is ever relaxed.)
            // acceptable_refusal-only gap rows MAY carry a non-empty
            // expected_citation_set — it is the answer-path ground truth,
            // graded only when the agent answers.
            if (q.RefusalRequired)
            {
                Assert.Empty(q.ExpectedCitationSet);
            }
        }
    }

    [Fact]
    public void ActiveGroundTruthFile_HasRefusalSignal_InBothDirections()
    {
        // Refusal-correctness has signal in both directions only when
        // the active eval set contains at least one refusal_required=true
        // row AND at least one must-answer row (neither flag). Without
        // both, the metric collapses to a constant.
        // (acceptable_refusal-only gap rows carry no refusal signal by
        // design — they don't count toward either direction.)
        var path = LocateGroundTruthFile("wizard.v2.jsonl");
        var questions = EvalQuestionParser.ParseFile(path);

        Assert.Contains(questions, q => q.RefusalRequired);
        Assert.Contains(questions, q => !q.AcceptableRefusal && !q.RefusalRequired);
    }

    [Fact]
    public void V2_ContainsMachineIdScopeRegressionFixture()
    {
        // Asserts the 2026-07-06 machineId-filter-stability regression fixture
        // is present and well-formed. The row must carry a non-empty
        // expected_citation_set — the whole point of the fixture is that a
        // corpus-wide retry without machineId returns OTHER machines, so a
        // machine-specific expected set is what distinguishes pass from fail.
        var path = LocateGroundTruthFile("wizard.v2.jsonl");
        var questions = EvalQuestionParser.ParseFile(path);

        var scopeRow = Assert.Single(questions, q => q.Slice == "machineId-filter-stability");
        Assert.NotEmpty(scopeRow.ExpectedCitationSet);
    }

    private static string LocateGroundTruthFile(string fileName)
    {
        // Search upward from the test binary's directory until we find
        // the data/eval directory. Mirrors the strategy in other tests
        // that read repo-root data files.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "eval", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate data/eval/{fileName} walking up from the test binary directory.");
    }
}
