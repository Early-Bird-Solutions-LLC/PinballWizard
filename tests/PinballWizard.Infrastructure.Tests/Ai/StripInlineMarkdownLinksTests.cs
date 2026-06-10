using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

// Pins the inline-markdown-link strip applied to canonical answer text
// (decision: Jim, 2026-06-10, option a — strip inline, rely on Sources
// cards). The Web renderer is plain text by design, so "[label](url)"
// would display as raw syntax; the transform reduces links to their
// labels. Provenance is unaffected — Sources cards are built from
// tool-trace citations extracted off the AgentResponse object, not from
// answer prose.
public sealed class StripInlineMarkdownLinksTests
{
    [Fact]
    public void SourceLink_ReducesToLabel()
    {
        var input = "The Sega Godzilla machine was released in 1998. [Source: OPDB](https://opdb.org/search?q=G5po2-MeP6B).";
        Assert.Equal(
            "The Sega Godzilla machine was released in 1998. Source: OPDB.",
            AiRouter.StripInlineMarkdownLinks(input));
    }

    [Fact]
    public void MultipleLinks_AllReducedInPlace()
    {
        var input = "See [the manual](https://a.example/m.pdf) and [the OPDB page](https://opdb.org/search?q=X) for details.";
        Assert.Equal(
            "See the manual and the OPDB page for details.",
            AiRouter.StripInlineMarkdownLinks(input));
    }

    [Fact]
    public void TextWithoutLinks_Unchanged()
    {
        var input = "Plain prose with [brackets] and (parens) but no link syntax.";
        Assert.Same(input, AiRouter.StripInlineMarkdownLinks(input));
    }

    [Fact]
    public void EmptyText_PassesThrough()
    {
        Assert.Equal(string.Empty, AiRouter.StripInlineMarkdownLinks(string.Empty));
    }
}
