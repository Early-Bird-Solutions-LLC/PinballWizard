using PinballWizard.Application.Ai.Evaluation.Findability;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation.Findability;

public sealed class FindabilityProbeParserTests
{
    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidUngradedLine_ReturnsSingleProbe()
    {
        var lines = new[]
        {
            """{"id":"find-001","query":"alpha bravo","expected_opdb_ids":["FAKE-AAAA1"]}""",
        };

        var result = FindabilityProbeParser.Parse(lines, "test");

        Assert.Single(result);
        var p = result[0];
        Assert.Equal("find-001", p.Id);
        Assert.Equal("alpha bravo", p.Query);
        Assert.Equal(["FAKE-AAAA1"], p.ExpectedOpdbIds);
        Assert.Null(p.Graded);
    }

    [Fact]
    public void Parse_GradedLine_PopulatesGradedMap()
    {
        var lines = new[]
        {
            """{"id":"find-002","query":"charlie delta","expected_opdb_ids":["FAKE-BBBB2"],"graded":{"FAKE-BBBB2":3,"FAKE-CCCC3":1}}""",
        };

        var result = FindabilityProbeParser.Parse(lines, "test");

        Assert.Single(result);
        var p = result[0];
        Assert.NotNull(p.Graded);
        Assert.Equal(3, p.Graded["FAKE-BBBB2"]);
        Assert.Equal(1, p.Graded["FAKE-CCCC3"]);
    }

    [Fact]
    public void Parse_MultipleExpectedIds_AllLoaded()
    {
        var lines = new[]
        {
            """{"id":"find-003","query":"echo foxtrot","expected_opdb_ids":["FAKE-DDDD4","FAKE-EEEE5"]}""",
        };

        var result = FindabilityProbeParser.Parse(lines, "test");

        var p = result[0];
        Assert.Equal(2, p.ExpectedOpdbIds.Count);
        Assert.Contains("FAKE-DDDD4", p.ExpectedOpdbIds);
        Assert.Contains("FAKE-EEEE5", p.ExpectedOpdbIds);
    }

    // ── Skip behavior ────────────────────────────────────────────────────

    [Fact]
    public void Parse_BlankLines_AreSkipped()
    {
        var lines = new[]
        {
            string.Empty,
            "   ",
            """{"id":"find-001","query":"alpha","expected_opdb_ids":["FAKE-AAAA1"]}""",
            string.Empty,
        };

        var result = FindabilityProbeParser.Parse(lines, "test");

        Assert.Single(result);
    }

    [Fact]
    public void Parse_CommentLines_AreSkipped()
    {
        var lines = new[]
        {
            "# section: machine lookup probes",
            """{"id":"find-001","query":"alpha","expected_opdb_ids":["FAKE-AAAA1"]}""",
            "  # indented comment also skipped",
        };

        var result = FindabilityProbeParser.Parse(lines, "test");

        Assert.Single(result);
        Assert.Equal("find-001", result[0].Id);
    }

    // ── Validation errors ────────────────────────────────────────────────

    [Fact]
    public void Parse_DuplicateId_Throws()
    {
        var lines = new[]
        {
            """{"id":"find-001","query":"alpha","expected_opdb_ids":["FAKE-AAAA1"]}""",
            """{"id":"find-001","query":"beta","expected_opdb_ids":["FAKE-BBBB2"]}""",
        };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    [Fact]
    public void Parse_MissingId_Throws()
    {
        var lines = new[]
        {
            """{"query":"alpha","expected_opdb_ids":["FAKE-AAAA1"]}""",
        };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    [Fact]
    public void Parse_MissingQuery_Throws()
    {
        var lines = new[]
        {
            """{"id":"find-001","expected_opdb_ids":["FAKE-AAAA1"]}""",
        };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    [Fact]
    public void Parse_EmptyExpectedOpdbIds_Throws()
    {
        var lines = new[]
        {
            """{"id":"find-001","query":"alpha","expected_opdb_ids":[]}""",
        };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    [Fact]
    public void Parse_GradeAboveMax_Throws()
    {
        var lines = new[]
        {
            """{"id":"find-001","query":"alpha","expected_opdb_ids":["FAKE-AAAA1"],"graded":{"FAKE-AAAA1":4}}""",
        };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    [Fact]
    public void Parse_GradeNegative_Throws()
    {
        var lines = new[]
        {
            """{"id":"find-001","query":"alpha","expected_opdb_ids":["FAKE-AAAA1"],"graded":{"FAKE-AAAA1":-1}}""",
        };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        var lines = new[] { "not-json" };

        Assert.Throws<InvalidDataException>(() =>
            FindabilityProbeParser.Parse(lines, "test"));
    }

    // ── Fixture file ─────────────────────────────────────────────────────

    [Fact]
    public void ParseFile_FixtureFile_LoadsAllRows()
    {
        var path = Path.Join(AppContext.BaseDirectory, "findability.fixture.jsonl");
        Assert.True(File.Exists(path), $"Fixture not copied to output directory: {path}");

        var probes = FindabilityProbeParser.ParseFile(path);

        Assert.True(probes.Count >= 5, $"Expected at least 5 fixture rows; got {probes.Count}");
        // All IDs unique (parser enforces; this double-checks no silent dedup)
        Assert.Equal(
            probes.Count,
            probes.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
