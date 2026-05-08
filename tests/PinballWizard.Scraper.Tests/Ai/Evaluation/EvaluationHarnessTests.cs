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

namespace PinballWizard.Scraper.Tests.Ai.Evaluation;

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
    public async Task RunAsync_OutOfScopeQuestion_RefusedCorrectly_AllScoresPerfect()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-oos-001","question":"What's the weather like in Tokyo?","expected_sub_agent":"Wizard","expected_citation_set":[],"acceptable_refusal":true}""");

        // Out-of-scope question: agent refuses (IsRefusal=true,
        // empty citations). Refusal-correctness=1, precision=1
        // (both empty), recall=1 (no expected to recall).
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
    }

    [Fact]
    public async Task RunAsync_OverEagerAnswerOnRefusableQuestion_ScoresZeroOnRefusal()
    {
        using var fixture = new HarnessFixture();
        fixture.WriteGroundTruth(
            """{"id":"ev-oos-001","question":"What's the weather like?","expected_sub_agent":"Wizard","expected_citation_set":[],"acceptable_refusal":true}""");

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
                new SubagentAccuracyEvaluator(),
                new RefusalCorrectnessEvaluator(),
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
