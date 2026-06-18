using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Behavioral tests for TokenRenderer.
//
// Per ADR-0026 PR self-audit item 9(d): every new Razor component must have
// a bUnit smoke test. TokenRenderer is a sub-component of the WizardAnswerStream
// locked delight surface.
//
// Tests assert:
//   1.  Empty SpanTexts renders nothing (no token-renderer div).
//   2.  Non-empty SpanTexts with IsStreaming=true renders all tokens as
//       individual spans.
//   3.  The newest span gets the bumper-pulse CSS class when IsStreaming=true.
//   4.  When IsStreaming=false, no token-span elements are present (canonical
//       text is rendered through MarkdownContent, not raw spans).
//   5.  prefers-reduced-motion: the --new class is present in streaming mode
//       — CSS @media handles the motion suppression; we assert the class
//       structure so the media query has the correct selector target.
//   6.  aria-live="polite" is present for screen-reader announcements.
//   7.  **bold** in canonical text renders <strong> — no literal asterisks.
//   8.  Numbered-list inline format renders list structure (no "1." run-together).
//   9.  XSS-class input is HTML-encoded — no script element emitted.
//   10. Streaming with mid-marker split delta renders as plain text (safe, no throw).
public sealed class TokenRendererTests
{
    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Empty SpanTexts renders nothing
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_SpanTexts_renders_nothing()
    {
        using var ctx = BuildCtx();
        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, Array.Empty<string>()));

        Assert.Empty(cut.FindAll("[data-testid='token-renderer']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Streaming: non-empty SpanTexts renders all tokens as individual spans
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Non_empty_SpanTexts_streaming_renders_all_spans()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "Stern ", "Godzilla ", "Pro" };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, true));

        cut.Find("[data-testid='token-renderer']");
        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(3, spans.Count);

        Assert.Equal("Stern ", spans[0].TextContent);
        Assert.Equal("Godzilla ", spans[1].TextContent);
        Assert.Equal("Pro", spans[2].TextContent);
    }

    // ──────────────────────────────────────────────────────────────────────
    // IsStreaming=true: newest span carries bumper-pulse class
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void When_IsStreaming_newest_span_carries_bumper_pulse_class()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "Token A", "Token B", "Token C" };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, true));

        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(3, spans.Count);

        // Only the last span gets the --new modifier class.
        Assert.DoesNotContain("token-renderer__span--new", spans[0].ClassName ?? string.Empty);
        Assert.DoesNotContain("token-renderer__span--new", spans[1].ClassName ?? string.Empty);
        Assert.Contains("token-renderer__span--new", spans[2].ClassName ?? string.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────
    // IsStreaming=false: no token-span elements (canonical path uses MarkdownContent)
    //
    // Once Final lands, WizardAnswerStream sets IsStreaming=false. The component
    // switches to MarkdownContent which renders structured markup, not raw spans.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void When_not_IsStreaming_no_token_span_elements_present()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "Canonical answer text" };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, false));

        // The token-renderer container is present (non-empty SpanTexts).
        cut.Find("[data-testid='token-renderer']");

        // But the streaming token-span elements are NOT present — canonical
        // text goes through MarkdownContent instead.
        Assert.Empty(cut.FindAll("[data-testid='token-span']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // prefers-reduced-motion: the --new class is present in streaming mode
    //
    // The CSS @media (prefers-reduced-motion: reduce) rule targets
    // .token-renderer__span--new and overrides animation: none !important.
    // We assert the class is present so the media query has the correct
    // selector target. The actual suppression is a CSS responsibility.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Newest_span_class_structure_supports_prefers_reduced_motion_targeting()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "First token", "Second token" };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, true));

        // The newest span carries the --new modifier that the CSS media query targets.
        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(2, spans.Count);
        Assert.Contains("token-renderer__span--new", spans[1].ClassName ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // aria-live="polite" on the token-renderer container
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Token_renderer_has_aria_live_polite()
    {
        using var ctx = BuildCtx();
        string[] spans = ["Hello"];
        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, spans)
            .Add(c => c.IsStreaming, false));

        var container = cut.Find("[data-testid='token-renderer']");
        Assert.Equal("polite", container.GetAttribute("aria-live"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Markdown: **bold** in canonical text renders <strong> — no literal asterisks
    //
    // Issue #371: model output "**Medieval Madness**" renders literal asterisks.
    // In canonical (non-streaming) mode, MarkdownContent+MarkdownTokenizer
    // should render a <strong> element with encoded text content.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bold_markers_in_canonical_text_render_strong_element()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "The **Medieval Madness** remake." };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, false));

        // A <strong> element with the expected text content must be present.
        var strong = cut.Find("strong");
        Assert.Equal("Medieval Madness", strong.TextContent);

        // The rendered text must not contain literal asterisks.
        Assert.DoesNotContain("**", cut.Find("[data-testid='token-renderer']").InnerHtml,
            StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Markdown: numbered list renders list structure — no "1." run-together paragraph
    //
    // Issue #371 live capture: "1. **Medieval Madness Remake (2015)** — OPDB source
    // 2. **Medieval Madness Remake Limited Edition (2015)** — …" rendered inline.
    // After fix, each item must be in a <li> within an <ol>.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Numbered_list_renders_ol_and_li_elements()
    {
        using var ctx = BuildCtx();
        // Simulate the exact inline format from the live issue capture:
        // the model emits numbered items on separate lines.
        var texts = new[] { "1. Alpha edition\n2. Beta edition\n3. Gamma edition" };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, false));

        // An <ol> must be present.
        cut.Find("ol");

        // Three <li> elements.
        var items = cut.FindAll("li").ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("Alpha edition", items[0].TextContent.Trim());
        Assert.Equal("Beta edition", items[1].TextContent.Trim());
        Assert.Equal("Gamma edition", items[2].TextContent.Trim());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Safety: XSS-class input is HTML-encoded — no <script> element emitted
    //
    // Model text containing HTML-like content must be encoded, not passed
    // through as markup. MarkdownContent never uses MarkupString; all text
    // content goes through Blazor's AddContent (HTML-encoding pipeline).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Xss_payload_in_model_text_is_html_encoded_not_executed()
    {
        using var ctx = BuildCtx();
        var texts = new[] { "<script>alert(1)</script>**x**" };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, false));

        // No <script> element must exist in the rendered output.
        Assert.Empty(cut.FindAll("script"));

        // The literal < must appear encoded in the inner HTML (as &lt;).
        var rendererHtml = cut.Find("[data-testid='token-renderer']").InnerHtml;
        Assert.DoesNotContain("<script>", rendererHtml, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Streaming: mid-marker split deltas render as plain text without throwing
    //
    // When IsStreaming=true deltas like "**Godz" + "illa**" are rendered as
    // raw text spans. The component must not throw and must display the raw
    // delta text unmodified (markdown formatting is deferred to Final).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Streaming_mid_marker_split_renders_plain_text_without_throwing()
    {
        using var ctx = BuildCtx();
        // Simulate a delta split mid-bold-marker.
        var texts = new[] { "The **Godz", "illa** is great." };

        var cut = ctx.Render<TokenRenderer>(p => p
            .Add(c => c.SpanTexts, texts)
            .Add(c => c.IsStreaming, true));

        // Two token-span elements present (one per delta) — no exception.
        var spans = cut.FindAll("[data-testid='token-span']").ToList();
        Assert.Equal(2, spans.Count);

        // Text is rendered literally (unformatted) during streaming.
        Assert.Equal("The **Godz", spans[0].TextContent);
        Assert.Equal("illa** is great.", spans[1].TextContent);
    }
}
