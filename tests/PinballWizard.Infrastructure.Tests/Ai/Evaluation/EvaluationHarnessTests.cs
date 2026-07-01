using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Ai.Evaluation.Evaluators;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Foundry;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

public sealed class EvaluationHarnessTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RunAsync_ScoresGroundedQuestion_Correctly()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-001","question":"What's the wizard mode in Foo Fighters?","expected_sub_agent":"Rules","expected_citation_set":["GRBN-MQR4P"],"acceptable_refusal":false}""");

        // Agent answers with the correct citation against an
        // already-routed sub-agent — every score should be 1.0.
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "The wizard mode is Hairway to Steven (https://opdb.org/machines/GRBN-MQR4P).",
                Citations: new List<Citation>
                {
                    new("OPDB record GRBN-MQR4P", "https://opdb.org/machines/GRBN-MQR4P", MachineId: "GRBN-MQR4P"),
                },
                SubAgentUsed: "Rules",
                Confidence: 0.92,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Aggregate.QuestionCount);
        Assert.Equal(0, result.Aggregate.ErrorCount);
        Assert.Equal(1.0, result.Aggregate.CitationPrecisionMean);
        Assert.Equal(1.0, result.Aggregate.CitationRecallMean);
        Assert.Equal(1.0, result.Aggregate.SubagentAccuracyMean);
        Assert.Equal(1.0, result.Aggregate.RefusalCorrectnessMean);
        Assert.True(File.Exists(result.ResultsPath));
    }

    [Fact]
    public async Task RunAsync_HallucinatedCitation_TanksPrecision()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-001","question":"What's the wizard mode in Foo Fighters?","expected_sub_agent":"Rules","expected_citation_set":["GRBN-MQR4P"],"acceptable_refusal":false}""");

        // Agent cites the wrong machine — precision should be 0,
        // recall should be 0 (the expected citation wasn't in
        // predicted), subagent still matches, refusal still correct.
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "Some wrong answer.",
                Citations: new List<Citation>
                {
                    new("OPDB record WRONG-XXXX", "https://opdb.org/machines/WRONG-XXXX", MachineId: "WRONG-XXXX"),
                },
                SubAgentUsed: "Rules",
                Confidence: 0.4,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        Assert.Equal(0.0, result.Aggregate.CitationPrecisionMean);
        Assert.Equal(0.0, result.Aggregate.CitationRecallMean);
        Assert.Equal(1.0, result.Aggregate.SubagentAccuracyMean);
        Assert.Equal(1.0, result.Aggregate.RefusalCorrectnessMean);
    }

    [Fact]
    public async Task RunAsync_RulesAnswerWithNoCorpusChunk_SetsGroundingIntegrity0()
    {
        // Harness wiring test for issue #532: a Rules answer backed only by a
        // MachineRecord citation (no CorpusChunk) must score 0.0 on
        // grounding_integrity — not null (which would exclude the row from the
        // aggregate and hide the gap from the eval summary).
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-gi-001","question":"How do I play Iron Maiden?","expected_sub_agent":"Rules","expected_citation_set":["GRBN-MQR4P"],"acceptable_refusal":false}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "Iron Maiden features...",
                Citations: new List<Citation>
                {
                    new("Iron Maiden", "https://opdb.org/machines/GRBN-MQR4P",
                        MachineId: "GRBN-MQR4P",
                        SourceType: CitationSourceType.MachineRecord),
                },
                SubAgentUsed: "Rules",
                Confidence: 0.8,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        Assert.Equal(0.0, result.Questions[0].Scores.GroundingIntegrity);
        Assert.Equal(1, result.Aggregate.GroundingIntegrityCount);
        Assert.Equal(0.0, result.Aggregate.GroundingIntegrityMean);
    }

    [Fact]
    public async Task RunAsync_RequiredRefusalQuestion_RefusedCorrectly_AllScoresPerfect()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-oos-001","question":"What's the weather like in Tokyo?","expected_sub_agent":"Wizard","expected_citation_set":[],"acceptable_refusal":true,"refusal_required":true}""");

        // Out-of-scope question: agent refuses (IsRefusal=true,
        // empty citations). Refusal-correctness=1, precision=1
        // (both empty), recall=1 (no expected to recall). Coverage is
        // null — a refusal is not an answer, so the metric is undefined
        // and the row drops out of the coverage denominator.
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "I don't know — that's outside the pinball domain.",
                Citations: [],
                SubAgentUsed: "Wizard",
                Confidence: 0.1,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.OutOfScope,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        Assert.Equal(1.0, result.Aggregate.CitationPrecisionMean);
        Assert.Equal(1.0, result.Aggregate.CitationRecallMean);
        Assert.Equal(1.0, result.Aggregate.SubagentAccuracyMean);
        Assert.Equal(1.0, result.Aggregate.RefusalCorrectnessMean);
        Assert.Equal(1, result.Aggregate.RefusalCorrectnessCount);
        Assert.Null(result.Questions[0].Scores.CitationCoverage);
        Assert.Null(result.Aggregate.CitationCoverageMean);
        Assert.Equal(0, result.Aggregate.CitationCoverageCount);
    }

    [Fact]
    public async Task RunAsync_OverEagerAnswerOnRequiredRefusalQuestion_ScoresZeroOnRefusal()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-oos-001","question":"What's the weather like?","expected_sub_agent":"Wizard","expected_citation_set":[],"acceptable_refusal":true,"refusal_required":true}""");

        // Out-of-scope question, agent fabricated an answer instead
        // of refusing. Refusal-correctness = 0; precision = 0
        // (hallucinated citation); recall still 1 (no expected).
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "It's sunny.",
                Citations: new List<Citation>
                {
                    new("OPDB record HALLUCINATED", "https://opdb.org/machines/HALLU", MachineId: "HALLU"),
                },
                SubAgentUsed: "Wizard",
                Confidence: 0.8,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        Assert.Equal(0.0, result.Aggregate.RefusalCorrectnessMean);
        Assert.Equal(0.0, result.Aggregate.CitationPrecisionMean);
    }

    [Fact]
    public async Task RunAsync_AcceptableRefusalGapRow_Refused_CarriesNoSignal()
    {
        using var fixture = new HarnessFixture();
        // Content-gap row (JJP Toy Story 4 shape): refusal is acceptable
        // but NOT required, and expected_citation_set holds the
        // answer-path ground truth. A refusal must contribute neither
        // refusal nor citation signal — the two-state evaluator scored
        // citations 0 here, dragging the means for a correct behavior.
        fixture.WriteGroundTruth(
            """{"id":"ev-gap-001","question":"Where can I find the Toy Story 4 manual?","expected_sub_agent":"Repair","expected_citation_set":["GJ2o0-MrRye"],"acceptable_refusal":true,"acceptable_sub_agents":["Wizard"]}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "I don't have enough grounded content to answer that.",
                Citations: [],
                SubAgentUsed: "Wizard",
                Confidence: 0.2,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.InsufficientGrounding,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        var scores = result.Questions[0].Scores;
        Assert.Null(scores.RefusalCorrectness);
        Assert.Null(scores.CitationPrecision);
        Assert.Null(scores.CitationRecall);
        Assert.Null(scores.CitationCoverage);
        Assert.Equal(1.0, scores.SubagentAccuracy);
        Assert.Null(result.Aggregate.RefusalCorrectnessMean);
        Assert.Equal(0, result.Aggregate.RefusalCorrectnessCount);
        Assert.Null(result.Aggregate.CitationPrecisionMean);
        Assert.Equal(0, result.Aggregate.CitationPrecisionCount);
    }

    [Fact]
    public async Task RunAsync_AcceptableRefusalGapRow_AnsweredWithCorrectCitation_GradesCitationsNotRefusal()
    {
        using var fixture = new HarnessFixture();
        // The strike-one artifact: a gap row answered with the EXACT
        // expected citation must score full citation marks and carry no
        // refusal penalty (the two-state evaluator scored
        // refusal_correctness=0 for this correct behavior).
        fixture.WriteGroundTruth(
            """{"id":"ev-gap-001","question":"Is the JJP Toy Story 4 still available new?","expected_sub_agent":"Valuation","expected_citation_set":["GJ2o0-MrRye"],"acceptable_refusal":true,"acceptable_sub_agents":["Wizard"]}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "Per the OPDB record, it shipped in 2022 (https://opdb.org/machines/GJ2o0-MrRye).",
                Citations: new List<Citation>
                {
                    new("OPDB record GJ2o0-MrRye", "https://opdb.org/machines/GJ2o0-MrRye", MachineId: "GJ2o0-MrRye"),
                },
                SubAgentUsed: "Wizard",
                Confidence: 0.85,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        var scores = result.Questions[0].Scores;
        Assert.Equal(1.0, scores.CitationPrecision);
        Assert.Equal(1.0, scores.CitationRecall);
        Assert.Equal(1.0, scores.SubagentAccuracy);
        Assert.Null(scores.RefusalCorrectness);
        Assert.Equal(1.0, result.Aggregate.CitationPrecisionMean);
        Assert.Equal(1, result.Aggregate.CitationPrecisionCount);
        Assert.Null(result.Aggregate.RefusalCorrectnessMean);
        Assert.Equal(0, result.Aggregate.RefusalCorrectnessCount);
    }

    [Fact]
    public async Task RunAsync_RunLevelAbort_WritesPartialResults()
    {
        // #362: the 2026-06-11 credential-timeout runs aborted at the
        // run level and lost the scorecard for every healthy question
        // already evaluated. The salvage contract: a '.partial' results
        // file with the completed questions, clearly marked, never
        // presented as a finished run.
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-001","question":"q1","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
            """{"id":"ev-002","question":"q2","expected_sub_agent":"Rules","expected_citation_set":["Y"],"acceptable_refusal":false}""");

        using var callerCts = new CancellationTokenSource();
        var callCount = 0;
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult(new WizardAnswer(
                        Text: "ok",
                        Citations: new List<Citation> { new("t", "u", MachineId: "X") },
                        SubAgentUsed: "Rules",
                        Confidence: 0.9,
                        Escalated: false,
                        IsRefusal: false,
                        RefusalCategory: null,
                        PromptVersion: "v-test",
                        FoundryThreadId: null));
                }

                // Question 2: the caller's token gets cancelled mid-flight —
                // the run-level fatal path (EvaluateOneAsync rethrows
                // caller-driven cancellation).
                callerCts.Cancel();
                throw new OperationCanceledException(callerCts.Token);
            });

        var harness = fixture.BuildHarness();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(callerCts.Token));

        var partial = Directory.GetFiles(fixture.ResultsDirectory, "*.partial");
        var path = Assert.Single(partial);
        var json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);

        Assert.EndsWith("-PARTIAL", doc.RootElement.GetProperty("evaluation_id").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("questions").GetArrayLength());
    }

    [Fact]
    public async Task RunAsync_RouterThrows_RecordsErrorAndContinues()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-001","question":"q1","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
            """{"id":"ev-002","question":"q2","expected_sub_agent":"Rules","expected_citation_set":["Y"],"acceptable_refusal":false}""");

        var callCount = 0;
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    throw new InvalidOperationException("router exploded");
                }
                return Task.FromResult(new WizardAnswer(
                    Text: "ok",
                    Citations: new List<Citation> { new("t", "u", MachineId: "Y") },
                    SubAgentUsed: "Rules",
                    Confidence: 0.9,
                    Escalated: false,
                    IsRefusal: false,
                    RefusalCategory: null,
                    PromptVersion: "v-test",
                    FoundryThreadId: null));
            });

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Aggregate.QuestionCount);
        Assert.Equal(1, result.Aggregate.ErrorCount);
        Assert.NotNull(result.Questions[0].Error);
        Assert.Contains("router exploded", result.Questions[0].Error);
        Assert.Null(result.Questions[1].Error);
    }

    [Fact]
    public async Task RunAsync_AnsweredAllEditionsRow_DispatchesR2Evaluator_AndAggregates()
    {
        using var fixture = new HarnessFixture();
        // R2: edition-unspecified, answer differs — must name BOTH editions and
        // carry one citation per required edition. expected_outcome routes to the
        // AnsweredAllEditions evaluator; the mean must aggregate only this row.
        fixture.WriteGroundTruth(
            """{"id":"ev-r2","question":"How does multiball work in Stern Godzilla?","expected_sub_agent":"Rules","expected_citation_set":[],"acceptable_refusal":false,"expected_outcome":"answered_all_editions","required_editions":["Pro","Premium/LE"]}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "For the Pro edition, multiball works like X; for the Premium/LE edition, it works like Y.",
                Citations: new List<Citation>
                {
                    new("Godzilla Pro Manual", "https://opdb.org/machines/GweeP-MW95j", MachineId: "GweeP-MW95j"),
                    new("Godzilla Premium/LE Manual", "https://opdb.org/machines/GweeP-Ml9pZ", MachineId: "GweeP-Ml9pZ"),
                },
                SubAgentUsed: "Rules",
                Confidence: 0.9,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        // R2 dispatched and passed; R3 untouched (null mean / zero count).
        Assert.Equal(1, result.Aggregate.AnsweredAllEditionsCount);
        Assert.Equal(1.0, result.Aggregate.AnsweredAllEditionsMean);
        Assert.Equal(0, result.Aggregate.HonestSubstitutionCount);
        Assert.Null(result.Aggregate.HonestSubstitutionMean);
        Assert.Equal(1.0, result.Questions[0].Scores.AnsweredAllEditions);
        Assert.Null(result.Questions[0].Scores.HonestSubstitution);
    }

    [Fact]
    public async Task RunAsync_HonestSubstitutionRow_DispatchesR3Evaluator_UsesFirstRequiredEdition()
    {
        using var fixture = new HarnessFixture();
        // R3: user named LE, only Pro data — answer must disclose the LE gap and
        // cite the Pro substitute. expected_outcome routes to HonestSubstitution;
        // the harness uses required_editions[0] ("LE") as the named edition.
        fixture.WriteGroundTruth(
            """{"id":"ev-r3","question":"How do the Godzilla LE flippers behave?","expected_sub_agent":"Rules","expected_citation_set":[],"acceptable_refusal":false,"expected_outcome":"honest_substitution","required_editions":["LE"]}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "I don't have LE-specific details for that, but here's what the Pro manual says about the flippers.",
                Citations: new List<Citation>
                {
                    new("Godzilla Pro Manual", "https://opdb.org/machines/GweeP-MW95j", MachineId: "GweeP-MW95j"),
                },
                SubAgentUsed: "Rules",
                Confidence: 0.8,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        // R3 dispatched and passed (names "LE" as a whole word + discloses the gap
        // + cites a substitute); R2 untouched.
        Assert.Equal(1, result.Aggregate.HonestSubstitutionCount);
        Assert.Equal(1.0, result.Aggregate.HonestSubstitutionMean);
        Assert.Equal(0, result.Aggregate.AnsweredAllEditionsCount);
        Assert.Null(result.Aggregate.AnsweredAllEditionsMean);
    }

    [Fact]
    public async Task RunAsync_AcceptableCitationSets_TakesAnyOfPath_RewardsEitherEditionBase()
    {
        using var fixture = new HarnessFixture();
        // R1/any-of: when acceptable_citation_sets is present, the harness scores
        // against the most-favorable set. Citing ONLY the Premium/LE base must score
        // 1.0 precision against [[Pro],[Premium/LE]] — the single-expected path would
        // have penalized it.
        fixture.WriteGroundTruth(
            """{"id":"ev-anyof","question":"What is Stern Godzilla's playfield size?","expected_sub_agent":"Rules","expected_citation_set":["GweeP-MW95j"],"acceptable_refusal":false,"acceptable_citation_sets":[["GweeP-MW95j"],["GweeP-Ml9pZ"]]}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "The playfield is standard size (cited: Godzilla Premium/LE Manual).",
                Citations: new List<Citation>
                {
                    new("Godzilla Premium/LE Manual", "https://opdb.org/machines/GweeP-Ml9pZ", MachineId: "GweeP-Ml9pZ"),
                },
                SubAgentUsed: "Rules",
                Confidence: 0.9,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        // Any-of path: citing the OTHER acceptable base still scores 1.0. Without the
        // any-of branch this would be 0.0 (predicted GweeP-Ml9pZ ∉ expected [GweeP-MW95j]).
        Assert.Equal(1.0, result.Aggregate.CitationPrecisionMean);
        Assert.Equal(1.0, result.Aggregate.CitationRecallMean);
    }

    [Fact]
    public async Task RunAsync_WritesValidJson_RoundTrippable()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-001","question":"q1","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "Cited X.",
                Citations: new List<Citation> { new("X", "https://opdb.org/machines/X", MachineId: "X") },
                SubAgentUsed: "Rules",
                Confidence: 0.9,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        var json = await File.ReadAllTextAsync(result.ResultsPath);
        var roundtrip = JsonSerializer.Deserialize<EvalRunResult>(json, WebJsonOptions);

        Assert.NotNull(roundtrip);
        Assert.Equal(result.EvaluationId, roundtrip!.EvaluationId);
        Assert.Equal(1, roundtrip.Aggregate.QuestionCount);
    }

    [Fact]
    public async Task RunAsync_SlicedRows_ProducesPerSliceAggregates_AndPreservesOverallAggregate()
    {
        // Two rows tagged "easy" (precision=1.0) and two tagged
        // "reranker-sensitive" (precision=0.0 — wrong citation) are scored.
        // Assertions:
        //   - BySlice["easy"].CitationPrecisionMean == 1.0
        //   - BySlice["reranker-sensitive"].CitationPrecisionMean == 0.0
        //   - top-level Aggregate.CitationPrecisionMean == 0.5 (overall mean)
        //   - top-level Aggregate.QuestionCount == 4 (unchanged)
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-e-001","question":"q-easy-1","expected_sub_agent":"Rules","expected_citation_set":["CORRECT-1"],"acceptable_refusal":false,"slice":"easy"}""",
            """{"id":"ev-e-002","question":"q-easy-2","expected_sub_agent":"Rules","expected_citation_set":["CORRECT-2"],"acceptable_refusal":false,"slice":"easy"}""",
            """{"id":"ev-r-001","question":"q-rerank-1","expected_sub_agent":"Rules","expected_citation_set":["CORRECT-3"],"acceptable_refusal":false,"slice":"reranker-sensitive"}""",
            """{"id":"ev-r-002","question":"q-rerank-2","expected_sub_agent":"Rules","expected_citation_set":["CORRECT-4"],"acceptable_refusal":false,"slice":"reranker-sensitive"}""");

        var callIndex = 0;
        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var idx = Interlocked.Increment(ref callIndex);
                // Questions 1+2 ("easy"): cite the correct expected citation.
                // Questions 3+4 ("reranker-sensitive"): cite a wrong citation.
                var (citation, id) = idx <= 2
                    ? ($"CORRECT-{idx}", $"CORRECT-{idx}")
                    : ("WRONG", "WRONG");
                return Task.FromResult(new WizardAnswer(
                    Text: $"Answer {idx}.",
                    Citations: new List<Citation> { new(citation, $"https://example.com/{id}", MachineId: id) },
                    SubAgentUsed: "Rules",
                    Confidence: 0.9,
                    Escalated: false,
                    IsRefusal: false,
                    RefusalCategory: null,
                    PromptVersion: "v-test",
                    FoundryThreadId: null));
            });

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        // Per-slice breakdown must exist for both slices.
        Assert.True(result.BySlice.ContainsKey("easy"), "Expected 'easy' key in BySlice");
        Assert.True(result.BySlice.ContainsKey("reranker-sensitive"), "Expected 'reranker-sensitive' key in BySlice");

        // easy slice: both rows cited correctly → precision mean = 1.0.
        var easyAgg = result.BySlice["easy"];
        Assert.Equal(2, easyAgg.QuestionCount);
        Assert.Equal(1.0, easyAgg.CitationPrecisionMean);
        Assert.Equal(1.0, easyAgg.CitationRecallMean);

        // reranker-sensitive slice: both rows cited wrong → precision mean = 0.0.
        var rerankAgg = result.BySlice["reranker-sensitive"];
        Assert.Equal(2, rerankAgg.QuestionCount);
        Assert.Equal(0.0, rerankAgg.CitationPrecisionMean);
        Assert.Equal(0.0, rerankAgg.CitationRecallMean);

        // Top-level Aggregate must be the overall mean across all four rows
        // (unchanged: this is the invariant that no slice tag may perturb it).
        Assert.Equal(4, result.Aggregate.QuestionCount);
        Assert.Equal(0.5, result.Aggregate.CitationPrecisionMean);
        Assert.Equal(0.5, result.Aggregate.CitationRecallMean);
    }

    [Fact]
    public async Task RunAsync_UnslicedRows_GoToUnslicedBucket()
    {
        // A standard v2 ground-truth row (no slice field) must land in the
        // "(unsliced)" bucket so BySlice covers all questions. The top-level
        // Aggregate must be identical to the single-question result.
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-001","question":"What's the wizard mode?","expected_sub_agent":"Rules","expected_citation_set":["GRBN-MQR4P"],"acceptable_refusal":false}""");

        fixture.Router.AnswerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardAnswer(
                Text: "Cited correctly.",
                Citations: new List<Citation> { new("OPDB record", "https://opdb.org/machines/GRBN-MQR4P", MachineId: "GRBN-MQR4P") },
                SubAgentUsed: "Rules",
                Confidence: 0.9,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: "v-test",
                FoundryThreadId: null));

        var harness = fixture.BuildHarness();
        var result = await harness.RunAsync(CancellationToken.None);

        // The one unsliced question lands in "(unsliced)".
        var unslicedAgg = Assert.Single(result.BySlice).Value;
        Assert.Equal("(unsliced)", Assert.Single(result.BySlice).Key);
        Assert.Equal(1, unslicedAgg.QuestionCount);
        // Top-level aggregate is unchanged.
        Assert.Equal(result.Aggregate.QuestionCount, unslicedAgg.QuestionCount);
        Assert.Equal(result.Aggregate.CitationPrecisionMean, unslicedAgg.CitationPrecisionMean);
    }

    private sealed class HarnessFixture : IDisposable
    {
        public string Root { get; }
        public string GroundTruthPath { get; }
        public string ResultsDirectory { get; }
        public IAiRouter Router { get; }
        public IAgentPromptProvider PromptProvider { get; }

        public HarnessFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"eval-harness-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            GroundTruthPath = Path.Combine(Root, "wizard.test.jsonl");
            ResultsDirectory = Path.Combine(Root, "results");
            Router = Substitute.For<IAiRouter>();
            PromptProvider = Substitute.For<IAgentPromptProvider>();
            PromptProvider.PromptVersion.Returns("v-test");
        }

        public void WriteGroundTruth(params string[] lines)
        {
            File.WriteAllLines(GroundTruthPath, lines);
        }

        public EvaluationHarness BuildHarness()
        {
            var evalOptions = Options.Create(new EvalHarnessOptions
            {
                GroundTruthPath = GroundTruthPath,
                ResultsDirectory = ResultsDirectory,
                RegisterEvaluatorsOnRun = false,
                RunTimeoutSeconds = 60,
                PerQuestionTimeoutSeconds = 30,
            });

            return new EvaluationHarness(
                Router,
                PromptProvider,
                new CitationPrecisionEvaluator(),
                new CitationRecallEvaluator(),
                new CitationCoverageEvaluator(),
                new SubagentAccuracyEvaluator(),
                new RefusalCorrectnessEvaluator(),
                new AnsweredAllEditionsEvaluator(),
                new HonestSubstitutionEvaluator(),
                new GroundingIntegrityEvaluator(),
                evalOptions,
                NullLogger<EvaluationHarness>.Instance);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; another process may still
                // hold a handle to the results file we just wrote.
            }
        }
    }
}
