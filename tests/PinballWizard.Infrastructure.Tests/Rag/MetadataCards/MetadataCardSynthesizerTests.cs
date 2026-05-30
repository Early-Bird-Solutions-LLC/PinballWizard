using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Rag.MetadataCards;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.MetadataCards;

// Behavior-asserting tests for MetadataCardSynthesizer (build-spec
// § Phase 4 item 17). Each test covers a behavior the build-spec or
// the customer-facing-showcase posture calls out — sparse machines
// must not render "Designers: (none)" boilerplate; rich machines
// must surface every retrievable vocabulary signal (themes,
// designers, edition names + features); citation-friendly source
// attribution must appear when OPDB URL is present. Token count
// must be non-zero (proves the tokenizer ran, not just that text
// was emitted).
public sealed class MetadataCardSynthesizerTests
{
    private static MetadataCardSynthesizer NewSynthesizer()
        => new(NullLogger<MetadataCardSynthesizer>.Instance);

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MetadataCardSynthesizer(null!));
    }

    [Fact]
    public void Synthesize_NullMachine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewSynthesizer().Synthesize(null!));
    }

    [Fact]
    public void Synthesize_RichMachine_IncludesAllRetrievalSignals()
    {
        var machine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Stranger Things",
            Year = 2019,
            Designers = ["Brian Eddy", "Keith Elwin"],
            Themes = ["Television", "Sci-Fi"],
            OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P",
            Editions =
            [
                new MachineEdition
                {
                    Name = "Pro",
                    Msrp = "$5,999",
                    Description = "Standard playfield with one LCD.",
                    UniqueFeatures = ["Single LCD", "Standard ramps"],
                },
                new MachineEdition
                {
                    Name = "Premium",
                    Msrp = "$7,599",
                    Description = "Upper playfield with character animations.",
                },
            ],
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        // Every retrieval signal must surface in the prose so vector
        // and keyword search both have something to anchor on.
        Assert.Contains("Stranger Things", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Stern Pinball", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("2019", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Television", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Sci-Fi", chunk.Text, StringComparison.Ordinal);
        // Pin the themes joiner — a refactor that switches to ", " or
        // "; " would silently change citation-snippet readability.
        Assert.Contains("Television · Sci-Fi", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Brian Eddy", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Keith Elwin", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Pro", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Premium", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("$5,999", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Single LCD", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("https://opdb.org/machines/GRBN-MQR4P", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_RichMachine_ChunkShapeMatchesIndexSchema()
    {
        var machine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Stranger Things",
            Year = 2019,
            OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P",
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        // Per ADR-0021, metadata cards live in the same index as PDF
        // chunks but with page_start = page_end = 0 (no page anchor).
        // Section heading is "Metadata" so the citation surface can
        // render "Stranger Things — Metadata" rather than an empty
        // section field.
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(0, chunk.PageStart);
        Assert.Equal(0, chunk.PageEnd);
        Assert.Equal("Metadata", chunk.SectionHeading);
        Assert.True(chunk.TokenCount > 0, "TokenCount must be populated by the tokenizer.");
    }

    [Fact]
    public void Synthesize_NoYear_OmitsYearFromHeader()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Untitled Prototype",
            Year = null,
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.Contains("Untitled Prototype", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("()", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_NoThemes_OmitsThemesSection()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Some Machine",
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.DoesNotContain("Themes:", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_NoDesigners_OmitsDesignersSection()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Some Machine",
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.DoesNotContain("Designers:", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_NoEditions_OmitsEditionsSection()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Some Machine",
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.DoesNotContain("Editions:", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_NoOpdbSourceUrl_OmitsSourceLine()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Some Machine",
            OpdbSourceUrl = null,
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.DoesNotContain("Source:", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_AvailabilityPresent_RendersBracketed()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla",
            Editions =
            [
                new MachineEdition
                {
                    Name = "Pro",
                    Availability = "In Production",
                },
            ],
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.Contains("Pro [In Production]", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_PerEditionOpdbUrl_RendersAliasUrl()
    {
        var machine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Stranger Things",
            Editions =
            [
                new MachineEdition
                {
                    Name = "Premium",
                    OpdbAliasId = "GRBN-MQR4P-A97X1",
                    OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P-A97X1",
                },
            ],
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        // The per-edition alias URL must surface so edition-specific
        // citations can deep-link to the alias record.
        Assert.Contains("https://opdb.org/machines/GRBN-MQR4P-A97X1", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_LimitedEdition_RendersLimitedQuantity()
    {
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Some LE",
            Editions =
            [
                new MachineEdition
                {
                    Name = "Limited Edition",
                    LimitedQuantity = 500,
                },
            ],
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.Contains("Limited Edition", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("limited to 500", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthesize_SparseMachine_StillProducesNonEmptyChunk()
    {
        // The most minimal Machine valid under the model. Synthesizer
        // must not throw and must still emit text the index can
        // ingest — even if the content is just title + manufacturer.
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Skeleton",
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.False(string.IsNullOrWhiteSpace(chunk.Text));
        Assert.Contains("Skeleton", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Stern Pinball", chunk.Text, StringComparison.Ordinal);
        Assert.True(chunk.TokenCount > 0);
    }

    [Fact]
    public void Synthesize_TargetTokenCount_StaysWithinReasonableEnvelope()
    {
        // Build-spec § Phase 4 item 17 calls for ~150-token chunks.
        // For a typical machine with 2 editions and modest description
        // text, the synthesizer should land in the 50-300 token range.
        // A drastically larger output indicates a formatting
        // regression that would dilute retrieval relevance.
        var machine = new Machine
        {
            Id = "X-Y",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Godzilla",
            Year = 2021,
            Designers = ["Keith Elwin"],
            Themes = ["Movie"],
            OpdbSourceUrl = "https://opdb.org/machines/X-Y",
            Editions =
            [
                new MachineEdition
                {
                    Name = "Pro",
                    Msrp = "$6,999",
                    Description = "Standard playfield with magnetic ball lock.",
                },
                new MachineEdition
                {
                    Name = "Premium",
                    Msrp = "$8,999",
                    Description = "Upper playfield with action figures.",
                },
            ],
        };

        var chunk = NewSynthesizer().Synthesize(machine);

        Assert.InRange(chunk.TokenCount, 50, 300);
    }
}
