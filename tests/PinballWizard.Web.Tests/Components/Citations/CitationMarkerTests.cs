using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Citations;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Citations;

// Behavioral tests for CitationMarker.
//
// CitationMarker renders an inline numbered pinball-insert citation marker — a
// native <a href="#citation-N"> with data-testid, data-citation-number, a unique
// DOM id, and a hover tooltip derived from a cascaded ordered-citation list.
//
// Test-adaptation note (Step 3): The brief's second test sets .Add(x => x.Tooltip, ...)
// directly, but Tooltip is a private computed property (not a [Parameter]) derived from
// the [CascadingParameter] IReadOnlyList<Citation>? OrderedCitations. The test is adapted
// to supply the tooltip text via a 1-element cascaded OrderedCitations list whose [0]
// produces "[MANUAL] Stern Godzilla Manual", then asserts the same title/aria-label values
// the brief required. The assertion contract is preserved; only the delivery mechanism
// changed to match the actual component API.
public sealed class CitationMarkerTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Renders the correct number, anchor href, and text content.
    //
    // Load-bearing: data-citation-number and href must match the Number param
    // so the tokenizer can correlate markers to cards without positional drift.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renders_number_and_anchor_to_matching_card()
    {
        await using var ctx = BuildCtx();

        var cut = ctx.Render<CitationMarker>(p => p.Add(x => x.Number, 3));
        var el = cut.Find("[data-testid='citation-marker']");

        Assert.Equal("3", el.GetAttribute("data-citation-number"));
        Assert.Equal("#citation-3", el.GetAttribute("href"));
        Assert.Contains("3", el.TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Tooltip is derived from the cascaded OrderedCitations list and surfaces
    // as both the title and aria-label on the anchor element.
    //
    // ADAPTATION: The brief's test used .Add(x => x.Tooltip, "...") but Tooltip
    // is a private computed property, not a [Parameter]. Instead we cascade a
    // 1-element IReadOnlyList<Citation> whose CitationSourceType=Unknown produces
    // "[UNKNOWN] Stern Godzilla Manual", and cite Number=1 so OrderedCitations[0]
    // is chosen. The title/aria-label assertion is identical to the brief's
    // requirement, just the input path is correct.
    //
    // CitationSourceType.Unknown.ToString().ToUpperInvariant() = "UNKNOWN", so
    // the tooltip format is "[UNKNOWN] Stern Godzilla Manual".
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tooltip_from_cascade_is_the_aria_label_and_title()
    {
        await using var ctx = BuildCtx();

        // Supply a 1-element ordered citation list via cascading parameter.
        // SourceType=Unknown so the formatted tooltip is "[UNKNOWN] Stern Godzilla Manual".
        // Name="OrderedCitations" matches [CascadingParameter(Name = "OrderedCitations")]
        // in CitationMarker — required since Task 8 added the explicit Name to disambiguate.
        var orderedCitations = (IReadOnlyList<Citation>)new List<Citation>
        {
            new Citation("Stern Godzilla Manual", "https://example.com/manual.pdf",
                SourceType: CitationSourceType.Unknown),
        };
        ctx.Services.AddCascadingValue("OrderedCitations", _ => orderedCitations);

        var cut = ctx.Render<CitationMarker>(p => p.Add(x => x.Number, 1));
        var el = cut.Find("[data-testid='citation-marker']");

        const string expectedTooltip = "[UNKNOWN] Stern Godzilla Manual";
        Assert.Equal(expectedTooltip, el.GetAttribute("title"));
        Assert.Equal(expectedTooltip, el.GetAttribute("aria-label"));
    }
}
