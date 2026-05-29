using PinballWizard.Application.Ai.Evaluation;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai.Evaluation;

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
