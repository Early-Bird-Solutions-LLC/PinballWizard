using PinballWizard.Application.Documents;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Documents;

// Guards the single source of truth for synthesized-source provenance constants.
// Both the live sync verbs (Program.cs) and the index-scan backfill read these; the
// value-pins below make any change deliberate (a drift would flip a test, matching
// what the sync verbs pass to SynthesizedDocumentRecordFactory.Create).
public sealed class SynthesizedSourceDescriptorsTests
{
    [Theory]
    [InlineData("kineticist_godzilla_M1", "kineticist_")]
    [InlineData("tiltforums_7210_M2", "tiltforums_")]
    [InlineData("twip_this-week", "twip_")]
    [InlineData("pb_freshdesk_12345", "pb_freshdesk_")]
    public void ForDocumentId_SynthesizedId_ResolvesByPrefix(string documentId, string expectedPrefix)
    {
        var descriptor = SynthesizedSourceDescriptors.ForDocumentId(documentId);
        Assert.NotNull(descriptor);
        Assert.Equal(expectedPrefix, descriptor!.DocumentIdPrefix);
    }

    [Theory]
    [InlineData("doc_58c56c2ec9dfb4df")] // scraped document — not synthesized
    [InlineData("meta_GweeP-Ml9pZ")]      // metadata card — out of scope
    [InlineData("overview_G43BW")]        // game overview — out of scope
    public void ForDocumentId_NonSynthesizedId_ReturnsNull(string documentId)
    {
        Assert.Null(SynthesizedSourceDescriptors.ForDocumentId(documentId));
    }

    [Fact]
    public void Kineticist_PinsProvenanceConstants()
    {
        var d = SynthesizedSourceDescriptors.Kineticist;
        Assert.Equal("Kineticist Tutorial", d.DiscoveryContext);
        Assert.Equal(DocumentType.Rulesheet, d.DocumentType);
        Assert.Equal("md", d.FileFormat);
        Assert.Null(d.ManufacturerOverride);
        Assert.Null(d.ContentTitleSuffixToStrip);
    }

    [Fact]
    public void TiltForums_PinsProvenanceConstants()
    {
        var d = SynthesizedSourceDescriptors.TiltForums;
        Assert.Equal("Tilt Forums Rulesheet", d.DiscoveryContext);
        Assert.Equal(DocumentType.Rulesheet, d.DocumentType);
        Assert.Equal("html", d.FileFormat);
        Assert.Equal(" — Rulesheet", d.ContentTitleSuffixToStrip);
    }

    [Fact]
    public void Twip_PinsProvenanceConstants()
    {
        var d = SynthesizedSourceDescriptors.Twip;
        Assert.Equal("TWIP Newsletter", d.DiscoveryContext);
        Assert.Equal(DocumentType.NewsDigest, d.DocumentType);
        Assert.Equal("html", d.FileFormat);
        Assert.Equal("Kineticist", d.ManufacturerOverride);
    }

    [Fact]
    public void PbFreshdesk_PinsProvenanceConstants()
    {
        var d = SynthesizedSourceDescriptors.PbFreshdesk;
        Assert.Equal("Pinball Brothers Freshdesk Article", d.DiscoveryContext);
        Assert.Equal(DocumentType.SupportArticle, d.DocumentType);
        Assert.Equal("html", d.FileFormat);
    }

    [Fact]
    public void NonMachineMachineIds_AreTheSyntheticSentinels()
    {
        Assert.Contains("pinball_news", SynthesizedSourceDescriptors.NonMachineMachineIds);
        Assert.Contains("pb_support", SynthesizedSourceDescriptors.NonMachineMachineIds);
    }
}
