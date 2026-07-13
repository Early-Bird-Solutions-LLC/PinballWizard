using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation.Evaluators;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

// Behavior tests for MachineIdCoverageEvaluator (issue #719). Verifies that
// searchCorpus calls in the tool-call trace carry a non-null machineId when the
// question names a machine — the regression the trace surface was built to catch.
public sealed class MachineIdCoverageEvaluatorTests
{
    private readonly MachineIdCoverageEvaluator _evaluator = new();

    // ── Undefined (null score) cases ─────────────────────────────────────────

    [Fact]
    public void Compute_NullTrace_ReturnsNull()
    {
        // Null trace means no agent run occurred (cache hit or early-exit).
        // Metric is undefined — the harness should have filtered, but guard.
        Assert.Null(_evaluator.Compute(null));
    }

    [Fact]
    public void Compute_EmptyTrace_ReturnsNull()
    {
        // Agent ran but made no tool calls at all (e.g., out-of-scope refusal
        // answered inline). No searchCorpus calls → metric undefined.
        Assert.Null(_evaluator.Compute([]));
    }

    [Fact]
    public void Compute_TraceWithNoSearchCorpusCalls_ReturnsNull()
    {
        // Agent called getMachineByTitle but not searchCorpus. The machineId
        // scope constraint only applies to searchCorpus; getMachineByTitle
        // is irrelevant for this metric.
        var trace = new List<ToolCallRecord>
        {
            new("getMachineByTitle", new Dictionary<string, string?> { ["title"] = "Godzilla" }),
        };

        Assert.Null(_evaluator.Compute(trace));
    }

    // ── 1.0 cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Compute_SingleSearchCorpusWithMachineId_Returns1()
    {
        // The expected happy path: searchCorpus called with machineId set.
        var trace = new List<ToolCallRecord>
        {
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "multiball rules",
                ["machineId"] = "GweeP-MW95j",
            }),
        };

        Assert.Equal(1.0, _evaluator.Compute(trace));
    }

    [Fact]
    public void Compute_MultipleSearchCorpusCallsAllWithMachineId_Returns1()
    {
        // Two searchCorpus calls, both scoped to the machine.
        var trace = new List<ToolCallRecord>
        {
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "wizard mode rules",
                ["machineId"] = "GweeP-MW95j",
            }),
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "service bulletin GI",
                ["machineId"] = "GweeP-MW95j",
            }),
        };

        Assert.Equal(1.0, _evaluator.Compute(trace));
    }

    [Fact]
    public void Compute_MixedToolCallsWithMachineIdOnSearchCorpus_Returns1()
    {
        // getMachineByTitle without machineId arg (it uses "title" instead)
        // must not affect the score — only searchCorpus calls matter.
        var trace = new List<ToolCallRecord>
        {
            new("getMachineByTitle", new Dictionary<string, string?> { ["title"] = "Godzilla" }),
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "how to fix flipper",
                ["machineId"] = "GweeP-MW95j",
            }),
        };

        Assert.Equal(1.0, _evaluator.Compute(trace));
    }

    // ── 0.0 cases — the regression patterns issue #719 is designed to catch ──

    [Fact]
    public void Compute_SearchCorpusWithNullMachineId_Returns0()
    {
        // LLM passed machineId=null. The key is present but empty — this is
        // the regression: agent retrieved corpus-wide instead of machine-scoped.
        var trace = new List<ToolCallRecord>
        {
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "multiball rules",
                ["machineId"] = null,
            }),
        };

        Assert.Equal(0.0, _evaluator.Compute(trace));
    }

    [Fact]
    public void Compute_SearchCorpusWithEmptyMachineId_Returns0()
    {
        // LLM passed machineId="" — blank is treated as absent.
        var trace = new List<ToolCallRecord>
        {
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "multiball rules",
                ["machineId"] = "   ",
            }),
        };

        Assert.Equal(0.0, _evaluator.Compute(trace));
    }

    [Fact]
    public void Compute_SearchCorpusWithoutMachineIdKey_Returns0()
    {
        // LLM omitted the machineId argument entirely — key not in the dict.
        var trace = new List<ToolCallRecord>
        {
            new("searchCorpus", new Dictionary<string, string?> { ["query"] = "wizard mode" }),
        };

        Assert.Equal(0.0, _evaluator.Compute(trace));
    }

    [Fact]
    public void Compute_OneSearchCorpusMissingMachineIdAmongTwo_Returns0()
    {
        // Two searchCorpus calls: the first is scoped, the second drops the
        // machineId (the retry regression from issue #719 description). Even
        // one unsoped call fails the whole answer.
        var trace = new List<ToolCallRecord>
        {
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "wizard mode",
                ["machineId"] = "GweeP-MW95j",
            }),
            new("searchCorpus", new Dictionary<string, string?>
            {
                ["query"] = "wizard mode retry",
                // machineId omitted on retry — the exact regression pattern
            }),
        };

        Assert.Equal(0.0, _evaluator.Compute(trace));
    }
}
