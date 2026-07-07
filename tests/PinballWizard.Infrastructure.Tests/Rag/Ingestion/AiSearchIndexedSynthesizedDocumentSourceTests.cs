using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

// Unit tests for the title-recovery parse. Every synthesized synthesizer writes the
// human title as the chunk's leading "# {title}" header, so the backfill can recover
// it from the index content field. The parse must return null (not a garbage title)
// for a mid-article chunk whose header was sliced off, so the service falls back to
// the machine title rather than surfacing body text as a heading.
public sealed class AiSearchIndexedSynthesizedDocumentSourceTests
{
    [Fact]
    public void TryParseTitle_HeaderThenBody_ReturnsTitle()
    {
        Assert.Equal(
            "How to play Godzilla",
            AiSearchIndexedSynthesizedDocumentSource.TryParseTitle("# How to play Godzilla\nTutorial by James. Source: ..."));
    }

    [Fact]
    public void TryParseTitle_HeaderOnlyNoNewline_ReturnsTitle()
    {
        Assert.Equal(
            "Attack from Mars",
            AiSearchIndexedSynthesizedDocumentSource.TryParseTitle("# Attack from Mars"));
    }

    [Fact]
    public void TryParseTitle_TiltForumsHeaderWithSuffix_ReturnsRawHeader()
    {
        // The source returns the raw header; the ' — Rulesheet' suffix is stripped
        // downstream by the descriptor-driven service, not here.
        Assert.Equal(
            "Godzilla — Rulesheet",
            AiSearchIndexedSynthesizedDocumentSource.TryParseTitle("# Godzilla — Rulesheet\nCommunity wiki rulesheet..."));
    }

    [Fact]
    public void TryParseTitle_NoMarkdownHeader_ReturnsNull()
    {
        Assert.Null(AiSearchIndexedSynthesizedDocumentSource.TryParseTitle("mid-article chunk body with no header"));
    }

    [Fact]
    public void TryParseTitle_EmptyHeader_ReturnsNull()
    {
        Assert.Null(AiSearchIndexedSynthesizedDocumentSource.TryParseTitle("# \nbody"));
    }

    [Fact]
    public void TryParseTitle_EmptyContent_ReturnsNull()
    {
        Assert.Null(AiSearchIndexedSynthesizedDocumentSource.TryParseTitle(string.Empty));
    }
}
