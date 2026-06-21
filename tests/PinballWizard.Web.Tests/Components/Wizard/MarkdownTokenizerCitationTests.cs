using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Behavioral tests for MarkdownTokenizer [[cite:N]] inline token recognition.
//
// MarkdownTokenizer.Render returns a RenderFragment; tests wrap it in a minimal
// builder call so bUnit can render and query the output DOM.
//
// Key invariants under test:
//   1. [[cite:N]] → CitationMarker with correct data-citation-number.
//   2. Malformed / unknown tokens ([[cite:]], [[unknown:3]]) → literal text, no marker.
//   3. Two [[cite:1]] in the same answer → distinct Occurrence (1 and 2), distinct DOM ids.
//   4. Surrounding prose is preserved; raw token text does not appear in the markup.
public sealed class MarkdownTokenizerCitationTests
{
    // ──────────────────────────────────────────────────────────────────────
    // [[cite:N]] renders a CitationMarker with the correct number
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CiteToken_RendersCitationMarker_WithNumber()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var fragment = MarkdownTokenizer.Render(
            "The flippers persist after the switch test passes [[cite:2]].");
        var cut = ctx.Render(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, fragment);
            builder.CloseElement();
        });

        var marker = cut.Find("[data-testid='citation-marker']");
        Assert.Equal("2", marker.GetAttribute("data-citation-number"));

        // The raw token must not appear verbatim in the markup.
        Assert.DoesNotContain("[[cite:2]]", cut.Markup);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Malformed and unknown tokens render as literal text (fail-safe)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void MalformedCiteToken_RendersAsLiteralText()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var fragment = MarkdownTokenizer.Render(
            "Edge case [[cite:]] and [[unknown:3]] stay literal.");
        var cut = ctx.Render(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, fragment);
            builder.CloseElement();
        });

        Assert.Contains("[[cite:]]", cut.Markup);
        Assert.Contains("[[unknown:3]]", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='citation-marker']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Two [[cite:1]] tokens in one answer → distinct Occurrence (1 then 2)
    // and distinct DOM ids (marker-1-1 / marker-1-2)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoCiteTokensSameNumber_GetDistinctOccurrences()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var fragment = MarkdownTokenizer.Render(
            "First mention [[cite:1]] and second mention [[cite:1]] in the same answer.");
        var cut = ctx.Render(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, fragment);
            builder.CloseElement();
        });

        var markers = cut.FindAll("[data-testid='citation-marker']");
        Assert.Equal(2, markers.Count);

        // Both carry number 1.
        Assert.All(markers, m => Assert.Equal("1", m.GetAttribute("data-citation-number")));

        // Occurrences must be 1 and 2 (distinct DOM ids).
        var ids = markers.Select(m => m.GetAttribute("id")).ToList();
        Assert.Contains("marker-1-1", ids);
        Assert.Contains("marker-1-2", ids);
    }
}
