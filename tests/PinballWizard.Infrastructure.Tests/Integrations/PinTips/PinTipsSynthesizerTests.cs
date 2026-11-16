using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Integrations.PinTips;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.PinTips;

public sealed class PinTipsSynthesizerTests
{
    [Fact]
    public void Synthesize_PutsTheTipTextAndOpdbIdInTheChunk()
    {
        var synthesizer = new PinTipsSynthesizer(
            new HybridChunker(Options.Create(new ChunkerOptions()), NullLogger<HybridChunker>.Instance),
            NullLogger<PinTipsSynthesizer>.Instance);
        var tip = new PinTip(2, "G5pe4", "general", 15, "Shoot the castle repeatedly.");
        var request = new ChunkRequest(
            MachineId: tip.OpdbId,
            MachineTitle: tip.OpdbId,
            Manufacturer: "PinTips",
            DocumentId: "pintips_2",
            DocumentUrl: "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-pintips.json",
            DocumentType: DocumentType.PlayTip,
            LastScrapedUtc: DateTimeOffset.UnixEpoch);

        var chunks = synthesizer.Synthesize(tip, request.DocumentUrl, request);

        var chunk = Assert.Single(chunks);
        Assert.Contains("Shoot the castle repeatedly.", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("G5pe4", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("tip 2", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentHash_IsStableUntilTheIndexedTextChanges()
    {
        var url = "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-pintips.json";
        var tip = new PinTip(2, "G5pe4", "general", 15, "Shoot the castle repeatedly.");

        var first = PinTipsSynthesizer.ContentHash(tip, url);
        var again = PinTipsSynthesizer.ContentHash(tip, url);
        var edited = PinTipsSynthesizer.ContentHash(tip with { Text = "Shoot the orbits." }, url);

        Assert.Equal(first, again);
        Assert.NotEqual(first, edited);
        Assert.True(PinTipsSynthesizer.IsUnchanged(first, again));
        Assert.False(PinTipsSynthesizer.IsUnchanged(null, first));
        Assert.False(PinTipsSynthesizer.IsUnchanged(first, edited));
    }
}
