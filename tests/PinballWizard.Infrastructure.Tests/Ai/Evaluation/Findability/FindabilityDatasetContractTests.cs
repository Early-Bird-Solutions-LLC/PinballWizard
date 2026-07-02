using PinballWizard.Application.Ai.Evaluation.Findability;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai.Evaluation.Findability;

// Pins the on-disk data/eval/findability.v1.jsonl judged dataset against the
// FindabilityProbe schema the parser enforces. This is the guard against the
// schema drift that would otherwise be invisible until an eval run: the dataset
// is authored by a curator (seeded from the live catalog) while the parser is
// code, and the two must agree on field names (`id`, `expected_opdb_ids`,
// optional `graded`). If a future edit renames a field or drops an `id`, this
// test fails at build time instead of silently producing a zero-recall run.
public sealed class FindabilityDatasetContractTests
{
    private static string DatasetPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate repo root (no PinballWizard.slnx found walking up from the test assembly).");
        }
        return Path.Combine(dir.FullName, "data", "eval", "findability.v1.jsonl");
    }

    [Fact]
    public void ProductionDataset_ParsesUnderTheProbeSchema()
    {
        var path = DatasetPath();
        Assert.True(File.Exists(path), $"Findability dataset missing at '{path}'.");

        // Parse throws InvalidDataException with a line number on any schema
        // violation (missing id/query, empty expected_opdb_ids, dup id, grade
        // out of 0-3). A clean parse IS the contract.
        var probes = FindabilityProbeParser.ParseFile(path);

        Assert.NotEmpty(probes);
    }

    [Fact]
    public void ProductionDataset_EveryProbe_HasAtLeastOneExpectedOpdbId()
    {
        // Redundant with the parser's own guard, but asserts the intent
        // explicitly: a findability probe with no correct answer is undefined.
        var probes = FindabilityProbeParser.ParseFile(DatasetPath());

        Assert.All(probes, p => Assert.NotEmpty(p.ExpectedOpdbIds));
    }

    [Fact]
    public void ProductionDataset_ExpectedOpdbIds_LookLikeOpdbIds()
    {
        // Live-catalog OPDB ids have the shape G<base>-M<variant> (e.g.
        // "GYWBZ-MkPrr"). This catches a curator pasting a slug or a title into
        // expected_opdb_ids instead of the canonical id.
        var probes = FindabilityProbeParser.ParseFile(DatasetPath());
        var opdbId = new System.Text.RegularExpressions.Regex(@"^G[0-9A-Za-z]+-M[0-9A-Za-z]+$");

        foreach (var probe in probes)
        {
            foreach (var id in probe.ExpectedOpdbIds)
            {
                Assert.True(
                    opdbId.IsMatch(id),
                    $"Probe '{probe.Id}' has expected id '{id}' that is not OPDB-id-shaped (G…-M…).");
            }
        }
    }
}
