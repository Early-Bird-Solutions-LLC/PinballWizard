using PinballWizard.Application.Resolution;
using PinballWizard.Cli.Commands;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Cli.Tests.Commands;

public sealed class CapturePageTextCommandTests
{
    private static MachineResolver BuildResolver(params Machine[] machines)
    {
        var byId = machines.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var index = InMemoryMachineIndex.Build(machines, aliases: []);
        return new MachineResolver(index, byId);
    }

    private static Machine Godzilla => new()
    {
        Id = "GZ-TEST-01",
        PartitionKey = "stern",
        ManufacturerDisplayName = "stern",
        Title = "Godzilla",
        ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["stern"] = "godzilla" },
    };

    [Fact]
    public void Truncate_TitleInsideBudget_TruncatesAndPreservesResolution()
    {
        var resolver = BuildResolver(Godzilla);
        var fullText = "Godzilla Service Manual. " + new string('x', 5000);

        var excerpt = CaptureGoldenSetCommand.TruncateWithResolutionParity(
            fullText, manufacturerKey: "stern", resolver, budget: 1000);

        Assert.Equal(1000, excerpt.Length);
        Assert.Contains("Godzilla", excerpt);
    }

    [Fact]
    public void Truncate_TitleOnlyBeyondBudget_KeepsFullText()
    {
        // The title appears only after the budget boundary: truncating would
        // silently encode a WEAKER resolver input than production sees, so the
        // parity check must keep the full page text for this entry.
        var resolver = BuildResolver(Godzilla);
        var fullText = new string('x', 2000) + " Godzilla Service Manual.";

        var excerpt = CaptureGoldenSetCommand.TruncateWithResolutionParity(
            fullText, manufacturerKey: "stern", resolver, budget: 1000);

        Assert.Equal(fullText, excerpt);
    }

    [Fact]
    public void Truncate_TextShorterThanBudget_ReturnsUnchanged()
    {
        var resolver = BuildResolver(Godzilla);
        var fullText = "Godzilla Service Manual.";

        var excerpt = CaptureGoldenSetCommand.TruncateWithResolutionParity(
            fullText, manufacturerKey: "stern", resolver, budget: 1000);

        Assert.Same(fullText, excerpt);
    }
}
