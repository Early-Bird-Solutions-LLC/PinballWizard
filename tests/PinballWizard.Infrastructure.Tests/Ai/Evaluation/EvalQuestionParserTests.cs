using PinballWizard.Application.Ai.Evaluation;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation;

public sealed class EvalQuestionParserTests
{
    private static readonly string[] OneOpdbId = ["GRBN-MQR4P"];

    [Fact]
    public void Parse_ValidLine_ReturnsSingleQuestion()
    {
        var lines = new[]
        {
            """{"id":"ev-rules-0001","question":"What's the wizard mode in Foo Fighters?","expected_sub_agent":"Rules","expected_citation_set":["GRBN-MQR4P"],"acceptable_refusal":false}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.Single(result);
        var q = result[0];
        Assert.Equal("ev-rules-0001", q.Id);
        Assert.Equal("What's the wizard mode in Foo Fighters?", q.Question);
        Assert.Equal("Rules", q.ExpectedSubAgent);
        Assert.Equal(OneOpdbId, q.ExpectedCitationSet);
        Assert.False(q.AcceptableRefusal);
    }

    [Fact]
    public void Parse_BlankLines_AreSkipped()
    {
        var lines = new[]
        {
            string.Empty,
            "   ",
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
            string.Empty,
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.Single(result);
    }

    [Fact]
    public void Parse_CommentLines_AreSkipped()
    {
        var lines = new[]
        {
            "# section: Rules questions",
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
            "  # indented comment also skipped",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.Single(result);
        Assert.Equal("ev-001", result[0].Id);
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
            """{ this is not valid json """,
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Parse_MissingId_Throws()
    {
        var lines = new[]
        {
            """{"question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("'id'", ex.Message);
    }

    [Fact]
    public void Parse_MissingQuestion_Throws()
    {
        var lines = new[]
        {
            """{"id":"ev-001","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("'question'", ex.Message);
    }

    [Fact]
    public void Parse_MissingExpectedSubAgent_Throws()
    {
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_citation_set":["X"],"acceptable_refusal":false}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("'expected_sub_agent'", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateId_Throws()
    {
        var lines = new[]
        {
            """{"id":"ev-001","question":"q1","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
            """{"id":"ev-001","question":"q2","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("duplicate id 'ev-001'", ex.Message);
    }

    [Fact]
    public void Parse_NullCitationSet_NormalizedToEmpty()
    {
        // Curator omitting expected_citation_set on an out-of-scope row
        // is tolerated; the parser fills in an empty list so the
        // refusal-flow scoring works without a NullReferenceException.
        var lines = new[]
        {
            """{"id":"ev-oos-001","question":"What's the weather?","expected_sub_agent":"Wizard","acceptable_refusal":true}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.Single(result);
        Assert.Empty(result[0].ExpectedCitationSet);
        Assert.True(result[0].AcceptableRefusal);
    }

    [Fact]
    public void ParseFile_NonExistent_Throws()
    {
        var bogusPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.jsonl");
        Assert.Throws<FileNotFoundException>(() => EvalQuestionParser.ParseFile(bogusPath));
    }

    // ── Edition-aware invariants (AB#259) ───────────────────────────────

    [Fact]
    public void Parse_InvalidExpectedOutcome_Throws()
    {
        // A curator typo in expected_outcome must fail loudly, not silently
        // skip the R2/R3 evaluator.
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false,"expected_outcome":"answered_all_edition"}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("invalid expected_outcome", ex.Message);
        Assert.Contains("answered_all_edition", ex.Message);
    }

    [Fact]
    public void Parse_DefaultExpectedOutcome_IsGrounded_AndAccepted()
    {
        // Absent expected_outcome defaults to "grounded" and passes.
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.Equal("grounded", result[0].ExpectedOutcome);
    }

    [Fact]
    public void Parse_AnsweredAllEditions_WithoutRequiredEditions_Throws()
    {
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false,"expected_outcome":"answered_all_editions"}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("required_editions", ex.Message);
    }

    [Fact]
    public void Parse_HonestSubstitution_WithoutRequiredEditions_Throws()
    {
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Repair","expected_citation_set":["X"],"acceptable_refusal":false,"expected_outcome":"honest_substitution"}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("required_editions[0]", ex.Message);
    }

    [Fact]
    public void Parse_EditionAwareFields_RoundTrip()
    {
        // franchise_wide_ok is parsed but currently consumed by nothing at
        // runtime (it's the row-level intent until Citation carries
        // edition_scope — see CitationPrecisionEvaluator). This test pins
        // its round-trip so a future reader doesn't delete it as dead code,
        // and proves acceptable_citation_sets / required_editions parse.
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false,"acceptable_citation_sets":[["A"],["B","C"]],"franchise_wide_ok":true,"expected_outcome":"answered_all_editions","required_editions":["Pro","Premium/LE"]}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        var q = result[0];
        Assert.True(q.FranchiseWideOk);
        Assert.Equal("answered_all_editions", q.ExpectedOutcome);
        Assert.NotNull(q.AcceptableCitationSets);
        Assert.Equal(2, q.AcceptableCitationSets!.Count);
        Assert.Equal(["B", "C"], q.AcceptableCitationSets[1]);
        Assert.Equal(["Pro", "Premium/LE"], q.RequiredEditions);
    }

    // ── Three-state refusal invariants (AB#259 metric-hygiene fix) ──────

    [Fact]
    public void Parse_RefusalRequired_RoundTrip_AndDefaultsFalse()
    {
        var lines = new[]
        {
            """{"id":"ev-001","question":"What's the weather?","expected_sub_agent":"Wizard","expected_citation_set":[],"acceptable_refusal":true,"refusal_required":true}""",
            """{"id":"ev-002","question":"Manual location?","expected_sub_agent":"Repair","expected_citation_set":["X"],"acceptable_refusal":true}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.True(result[0].RefusalRequired);
        Assert.False(result[1].RefusalRequired);
    }

    [Fact]
    public void Parse_RefusalRequired_WithoutAcceptableRefusal_Throws()
    {
        // refusal_required=true with acceptable_refusal=false is always a
        // curator typo — a required refusal is trivially acceptable.
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Wizard","expected_citation_set":[],"acceptable_refusal":false,"refusal_required":true}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("refusal_required=true but acceptable_refusal=false", ex.Message);
    }

    [Fact]
    public void Parse_RefusalRequired_WithCitations_Throws()
    {
        // A required-refusal row is out-of-scope by definition; carrying
        // answer-path citations contradicts that.
        var lines = new[]
        {
            """{"id":"ev-001","question":"q","expected_sub_agent":"Wizard","expected_citation_set":["X"],"acceptable_refusal":true,"refusal_required":true}""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => EvalQuestionParser.Parse(lines, "test"));
        Assert.Contains("non-empty expected_citation_set", ex.Message);
    }

    // ── acceptable_sub_agents round-trip (AB#259) ───────────────────────

    [Fact]
    public void Parse_AcceptableSubAgents_RoundTrip()
    {
        // Verifies that acceptable_sub_agents deserialises correctly and
        // that rows without the field default to null (absent = exact-match
        // only; default behavior is preserved).
        var lines = new[]
        {
            // Row with acceptable_sub_agents
            """{"id":"ev-001","question":"What is the theme?","expected_sub_agent":"Rules","expected_citation_set":["GweeP-MW95j"],"acceptable_refusal":false,"acceptable_sub_agents":["Wizard"]}""",
            // Row without acceptable_sub_agents (default null)
            """{"id":"ev-002","question":"How does multiball work?","expected_sub_agent":"Rules","expected_citation_set":["GweeP-MW95j"],"acceptable_refusal":false}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        Assert.Equal(2, result.Count);

        var withAnnotation = result[0];
        Assert.NotNull(withAnnotation.AcceptableSubAgents);
        Assert.Single(withAnnotation.AcceptableSubAgents!);
        Assert.Equal("Wizard", withAnnotation.AcceptableSubAgents![0]);

        var withoutAnnotation = result[1];
        Assert.Null(withoutAnnotation.AcceptableSubAgents);
    }

    // ── Hard-eval slice/source/first_stage_rank fields ──────────────────

    [Fact]
    public void Parse_HardEvalSliceFields_RoundTrip()
    {
        var lines = new[]
        {
            """{"id":"hard-0001","question":"q","expected_sub_agent":"Rules","expected_citation_set":["mch_X"],"acceptable_refusal":false,"slice":"reranker-sensitive","source":"confusable-edition","first_stage_rank":7}""",
        };

        var result = EvalQuestionParser.Parse(lines, "test");

        var q = result[0];
        Assert.Equal("reranker-sensitive", q.Slice);
        Assert.Equal("confusable-edition", q.Source);
        Assert.Equal(7, q.FirstStageRank);
    }

    [Fact]
    public void ParseFile_ValidFile_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eval-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"id":"ev-001","question":"q1","expected_sub_agent":"Rules","expected_citation_set":["X"],"acceptable_refusal":false,"notes":"first"}""",
                """{"id":"ev-002","question":"q2","expected_sub_agent":"Valuation","expected_citation_set":["Y"],"acceptable_refusal":false}""",
            ]);

            var result = EvalQuestionParser.ParseFile(path);

            Assert.Equal(2, result.Count);
            Assert.Equal("first", result[0].Notes);
            Assert.Null(result[1].Notes);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
