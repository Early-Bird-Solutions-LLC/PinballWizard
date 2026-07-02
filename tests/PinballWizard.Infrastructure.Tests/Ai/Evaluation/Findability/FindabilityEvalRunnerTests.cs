using NSubstitute;
using PinballWizard.Application.Ai.Evaluation.Evaluators;
using PinballWizard.Application.Ai.Evaluation.Findability;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation.Findability;

public sealed class FindabilityEvalRunnerTests
{
    // Evaluators are pure math; instantiate directly rather than mocking.
    private readonly RecallAtKEvaluator _recall = new();
    private readonly MrrEvaluator _mrr = new();
    private readonly NdcgAtKEvaluator _ndcg = new();

    private FindabilityEvalRunner BuildRunner(IFindabilityLookup lookup) =>
        new(lookup, _recall, _mrr, _ndcg);

    // ── Happy-path aggregation ────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PerfectLookup_AllMetricsAre1()
    {
        // Lookup always returns the expected machine first
        var probe = new FindabilityProbe("find-t001", "alpha bravo", ["FAKE-AAAA1"]);
        var lookup = Substitute.For<IFindabilityLookup>();
        lookup.GetRankedCandidatesAsync("alpha bravo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["FAKE-AAAA1", "OTHER-X"]));

        var runner = BuildRunner(lookup);
        var result = await runner.RunAsync([probe], k: 3);

        Assert.Equal(1, result.ProbeCount);
        Assert.Equal(3, result.K);
        Assert.Equal(1.0, result.RecallAt1Mean);
        Assert.Equal(1.0, result.RecallAtKMean);
        Assert.Equal(1.0, result.MrrMean);
        Assert.Equal(1.0, result.NdcgAtKMean);
    }

    [Fact]
    public async Task RunAsync_ZeroLookup_AllMetricsAre0()
    {
        // Lookup returns only irrelevant candidates
        var probe = new FindabilityProbe("find-t001", "alpha bravo", ["FAKE-AAAA1"]);
        var lookup = Substitute.For<IFindabilityLookup>();
        lookup.GetRankedCandidatesAsync("alpha bravo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["X", "Y", "Z"]));

        var runner = BuildRunner(lookup);
        var result = await runner.RunAsync([probe], k: 3);

        Assert.Equal(0.0, result.RecallAt1Mean);
        Assert.Equal(0.0, result.RecallAtKMean);
        Assert.Equal(0.0, result.MrrMean);
        Assert.Equal(0.0, result.NdcgAtKMean);
    }

    [Fact]
    public async Task RunAsync_MixedProbes_MeanIsArithmeticMean()
    {
        // Probe 1: perfect (RR = 1.0). Probe 2: complete miss (RR = 0.0). MRR = 0.5.
        var probes = new[]
        {
            new FindabilityProbe("find-t001", "alpha bravo", ["A"]),
            new FindabilityProbe("find-t002", "charlie delta", ["B"]),
        };
        var lookup = Substitute.For<IFindabilityLookup>();
        lookup.GetRankedCandidatesAsync("alpha bravo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["A", "X"]));
        lookup.GetRankedCandidatesAsync("charlie delta", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["X", "Y"]));  // miss

        var runner = BuildRunner(lookup);
        var result = await runner.RunAsync(probes, k: 3);

        Assert.Equal(2, result.ProbeCount);
        Assert.Equal(0.5, result.MrrMean);
        Assert.Equal(0.5, result.RecallAt1Mean);
    }

    [Fact]
    public async Task RunAsync_GradedProbe_UsesGradedNdcg()
    {
        // Probe with graded relevance: A=3, B=1; lookup returns [A, B] → NDCG@2 = 1.0
        var probe = new FindabilityProbe(
            "find-t003", "echo foxtrot", ["A"],
            Graded: new Dictionary<string, int> { ["A"] = 3, ["B"] = 1 });
        var lookup = Substitute.For<IFindabilityLookup>();
        lookup.GetRankedCandidatesAsync("echo foxtrot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["A", "B"]));

        var runner = BuildRunner(lookup);
        var result = await runner.RunAsync([probe], k: 2);

        Assert.Equal(1.0, result.NdcgAtKMean);
    }

    [Fact]
    public async Task RunAsync_GradedProbe_ImperfectRanking_NdcgLessThan1()
    {
        // A=3, B=1; lookup returns [B, A] → sub-optimal order; NDCG < 1.0
        var probe = new FindabilityProbe(
            "find-t004", "golf hotel", ["A"],
            Graded: new Dictionary<string, int> { ["A"] = 3, ["B"] = 1 });
        var lookup = Substitute.For<IFindabilityLookup>();
        lookup.GetRankedCandidatesAsync("golf hotel", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["B", "A"]));

        var runner = BuildRunner(lookup);
        var result = await runner.RunAsync([probe], k: 2);

        // Must be strictly less than 1.0 and greater than 0.0
        Assert.True(result.NdcgAtKMean > 0.0 && result.NdcgAtKMean < 1.0,
            $"Expected NDCG in (0, 1); got {result.NdcgAtKMean}");
    }

    // ── Per-probe results ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ProbeResults_CarryPerProbeDetails()
    {
        var probe = new FindabilityProbe("find-t001", "alpha bravo", ["A"]);
        var lookup = Substitute.For<IFindabilityLookup>();
        lookup.GetRankedCandidatesAsync("alpha bravo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["A", "B"]));

        var runner = BuildRunner(lookup);
        var result = await runner.RunAsync([probe], k: 2);

        Assert.Single(result.ProbeResults);
        var pr = result.ProbeResults[0];
        Assert.Equal("find-t001", pr.ProbeId);
        Assert.Equal("alpha bravo", pr.Query);
        Assert.Contains("A", pr.RankedCandidates);
    }

    // ── Edge cases ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EmptyProbeList_ReturnsZeroCountAndZeroMeans()
    {
        var lookup = Substitute.For<IFindabilityLookup>();
        var runner = BuildRunner(lookup);

        var result = await runner.RunAsync([], k: 5);

        Assert.Equal(0, result.ProbeCount);
        Assert.Empty(result.ProbeResults);
        Assert.Equal(0.0, result.RecallAt1Mean);
        Assert.Equal(0.0, result.RecallAtKMean);
        Assert.Equal(0.0, result.MrrMean);
        Assert.Equal(0.0, result.NdcgAtKMean);
    }

    [Fact]
    public async Task RunAsync_NullProbes_Throws()
    {
        var lookup = Substitute.For<IFindabilityLookup>();
        var runner = BuildRunner(lookup);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            runner.RunAsync(null!, k: 3));
    }

    [Fact]
    public async Task RunAsync_KZero_Throws()
    {
        var lookup = Substitute.For<IFindabilityLookup>();
        var runner = BuildRunner(lookup);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync([], k: 0));
    }
}
