using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Rag.GameOverviews;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.GameOverviews;

public sealed class GameOverviewSynthesizerTests
{
    private static GameOverviewSynthesizer New() => new(NullLogger<GameOverviewSynthesizer>.Instance);

    private static Machine Godzilla() => new()
    {
        Id = "GweeP-MW95j", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball",
        Title = "Godzilla",
        OverviewProse = "Battle Godzilla and rival kaiju across the city in this SPIKE-2 machine.",
        Editions =
        [
            new MachineEdition { Name = "Pro", Description = "Core layout." },
            new MachineEdition { Name = "Limited Edition", Description = "Adds a magna-grab and mechanical building.", UniqueFeatures = ["magna-grab", "mechanical building"] },
        ],
    };

    [Fact]
    public void Synthesize_PreservesSharedProseAndEditionDeltas()
    {
        var chunks = New().Synthesize(Godzilla());
        Assert.Contains(chunks, c => c.SectionHeading == "Overview" && c.Text.Contains("Battle Godzilla", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.SectionHeading == "Edition: Limited Edition" && c.Text.Contains("magna-grab", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.SectionHeading == "Edition: Pro");
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
    }

    [Fact]
    public void Synthesize_NoContent_ReturnsEmpty_NoFabrication()
    {
        var bare = new Machine { Id = "X-1", PartitionKey = "stern", ManufacturerDisplayName = "Stern Pinball", Title = "Mystery" };
        Assert.Empty(New().Synthesize(bare));
    }
}
