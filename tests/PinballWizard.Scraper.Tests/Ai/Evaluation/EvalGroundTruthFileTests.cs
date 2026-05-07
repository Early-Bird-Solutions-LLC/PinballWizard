using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai.Evaluation;

// Sanity-check on the committed data/eval/wizard.v1.jsonl: it must
// parse cleanly, every row's expected_sub_agent must be a valid
// AgentName, and ids must be unique. Catches a curator-introduced
// regression (e.g., copy-paste duplicate id, typo'd sub-agent) at
// build time rather than the next time the harness runs.
public sealed class EvalGroundTruthFileTests
{
    private static readonly HashSet<string> ValidSubAgents =
        new(AgentName.All, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void GroundTruthFile_ParsesCleanly_AndAllSubAgentsValid()
    {
        // The test runs from the test project's bin output directory;
        // walk up to the repo root to find data/eval/wizard.v1.jsonl.
        var path = LocateGroundTruthFile();
        Assert.True(File.Exists(path), $"expected ground-truth file at {path}");

        var questions = EvalQuestionParser.ParseFile(path);

        Assert.NotEmpty(questions);
        Assert.True(questions.Count >= 30,
            $"ADR-0016 calls for ~30 questions; found {questions.Count}");

        foreach (var q in questions)
        {
            Assert.True(ValidSubAgents.Contains(q.ExpectedSubAgent),
                $"row '{q.Id}' has expected_sub_agent='{q.ExpectedSubAgent}' which is not a known AgentName");

            // acceptable_refusal=true rows should have an empty
            // citation set, otherwise the curator-intent isn't
            // legible. (precision/recall on a refusal-flow row
            // with non-empty expected citations is ill-defined.)
            if (q.AcceptableRefusal)
            {
                Assert.Empty(q.ExpectedCitationSet);
            }
        }
    }

    [Fact]
    public void GroundTruthFile_HasOutOfScopeRows_ForRefusalSymmetry()
    {
        // Refusal-correctness has signal in both directions only when
        // the eval set contains at least one acceptable_refusal=true
        // row AND at least one acceptable_refusal=false row. Without
        // both, the metric collapses to a constant.
        var path = LocateGroundTruthFile();
        var questions = EvalQuestionParser.ParseFile(path);

        Assert.Contains(questions, q => q.AcceptableRefusal);
        Assert.Contains(questions, q => !q.AcceptableRefusal);
    }

    private static string LocateGroundTruthFile()
    {
        // Search upward from the test binary's directory until we find
        // data/eval/wizard.v1.jsonl. Mirrors the strategy in other
        // tests that read repo-root data files.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "eval", "wizard.v1.jsonl");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate data/eval/wizard.v1.jsonl walking up from the test binary directory.");
    }
}
