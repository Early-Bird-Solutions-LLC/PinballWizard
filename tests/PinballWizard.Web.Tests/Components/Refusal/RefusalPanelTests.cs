using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Refusal;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Refusal;

// bUnit smoke tests for RefusalPanel.
//
// Per ADR-0026 PR self-audit item 9(d): every new Razor component in the
// locked delight surface set must have a bUnit smoke test. RefusalPanel is
// explicitly in the locked delight surfaces per ADR-0026 § 6.
//
// Each test creates its own BunitContext and registers all services BEFORE
// rendering — bUnit locks the service provider on first GetService call.
//
// Tests assert behavior (the right per-category subview is visible, shared
// sections render when data is present, transient categories suppress community
// resources) — not structure (no assertion on MudBlazor internal class names).
//
// Spec conformance tests (modern-lcd.md §"Refusal that directs out"):
// - Panel carries accent-refusal CSS class contract (refusal-panel class)
// - Category labels carry data-testid="refusal-category-label" (ALL CAPS, display type)
// - Community routing CTAs render as refusal-route-cta (green puck buttons, peer parity)
public sealed class RefusalPanelTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Smoke: renders without errors for each RefusalCategory value
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RefusalCategory.OutOfScope)]
    [InlineData(RefusalCategory.InsufficientGrounding)]
    [InlineData(RefusalCategory.LowModelConfidence)]
    [InlineData(RefusalCategory.NoCitation)]
    [InlineData(RefusalCategory.UpstreamThrottled)]
    [InlineData(RefusalCategory.CostCeilingHit)]
    [InlineData(RefusalCategory.HarmfulContent)]
    public void RefusalPanel_Renders_WithoutException_ForEachCategory(RefusalCategory category)
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = BuildDetail();

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, category)
            .Add(x => x.Detail, detail));

        // The panel wrapping element must be present.
        cut.Find("[data-testid='refusal-panel']");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spec: panel carries accent-refusal treatment via "refusal-panel" CSS class.
    // The CSS class maps to border: 1px solid var(--pw-accent-refusal) in
    // RefusalPanel.razor.css — asserting the class name is the observable contract.
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RefusalCategory.OutOfScope)]
    [InlineData(RefusalCategory.InsufficientGrounding)]
    [InlineData(RefusalCategory.LowModelConfidence)]
    [InlineData(RefusalCategory.NoCitation)]
    public void RefusalPanel_RecoveryCategory_PanelHasRefusalPanelClass(RefusalCategory category)
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, category)
            .Add(x => x.Detail, BuildDetail()));

        // The MudPaper element carries Class="refusal-panel ..." which maps to
        // the accent-refusal RED border in RefusalPanel.razor.css.
        var panel = cut.Find("[data-testid='refusal-panel']");
        Assert.Contains("refusal-panel", panel.ClassName ?? string.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spec: OutOfScope reason text includes routing-promise ONLY when community
    // resources are present.
    //
    // modern-lcd.md §"Posture": name coverage gaps honestly; never imply a
    // community destination exists when the routing CTA list is empty.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_OutOfScope_WithCommunityResources_ReasonTextMentionsCommunity()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = BuildDetail(includeMarketplaceResources: true);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // When community resources ARE present the reason must reference "community".
        var reason = cut.Find("[data-testid='out-of-scope-reason']");
        Assert.Contains("community", reason.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusalPanel_OutOfScope_WithoutCommunityResources_ReasonTextDoesNotMentionCommunity()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Detail with NO community resources — the routing-promise must be absent.
        var detail = BuildDetail(includeMarketplaceResources: false);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // Reason text must NOT reference "community below" when no CTAs follow.
        var reason = cut.Find("[data-testid='out-of-scope-reason']");
        Assert.DoesNotContain("community below", reason.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spec: category label carries data-testid="refusal-category-label" (ALL CAPS)
    // modern-lcd.md §"Per-category framing" — labels in display type, ALL CAPS.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_OutOfScope_CategoryLabel_IsAllCapsOutOfScope()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, BuildDetail()));

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("OUT OF SCOPE", label.TextContent.ToUpperInvariant());
    }

    [Fact]
    public void RefusalPanel_InsufficientGrounding_CategoryLabel_IsAllCapsLowConfidence()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.InsufficientGrounding)
            .Add(x => x.Detail, BuildDetail()));

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    [Fact]
    public void RefusalPanel_LowModelConfidence_CategoryLabel_IsAllCapsLowConfidence()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.LowModelConfidence)
            .Add(x => x.Detail, BuildDetail()));

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spec: community routing CTAs render as refusal-route-cta (puck buttons)
    // modern-lcd.md §"Routing-recommendation CTA spec" — peer parity,
    // accent-grounded backlight, recessed puck. Observable: data-testid + CSS class.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_WithCommunityResources_RendersRefusalRouteCtas()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = BuildDetail(includeMarketplaceResources: true);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // Routing CTAs must render under data-testid="refusal-routes".
        cut.Find("[data-testid='refusal-routes']");

        // Each resource renders as a refusal-route-cta element.
        var ctas = cut.FindAll("[data-testid='refusal-route-cta']");
        Assert.Equal(3, ctas.Count);
    }

    [Fact]
    public void RefusalPanel_WithCommunityResources_AllCtasHaveRefusalRouteCtaClass()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = BuildDetail(includeMarketplaceResources: true);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // All CTAs must carry "refusal-route-cta" — the CSS class that maps
        // to the accent-grounded puck backlight (peer parity per Posture rule 4).
        var ctas = cut.FindAll("[data-testid='refusal-route-cta']");
        foreach (var cta in ctas)
        {
            Assert.Contains("refusal-route-cta", cta.ClassName ?? string.Empty);
        }
    }

    [Fact]
    public void RefusalPanel_WithCommunityResources_CtasAreOutboundLinks()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = BuildDetail(includeMarketplaceResources: true);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // Each CTA must be an anchor with target=_blank (community-resource posture:
        // outbound is a feature). rel=noopener noreferrer is a security requirement.
        var ctas = cut.FindAll("[data-testid='refusal-route-cta']");
        foreach (var cta in ctas)
        {
            Assert.Equal("_blank", cta.GetAttribute("target"));
            Assert.Contains("noopener", cta.GetAttribute("rel") ?? string.Empty);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routing: OutOfScope renders "OUT OF SCOPE" label
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_OutOfScope_RendersOutOfScopeLabel()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, BuildDetail()));

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("OUT OF SCOPE", label.TextContent.ToUpperInvariant());

        // Must NOT show "LOW CONFIDENCE" which InsufficientGrounding uses
        // (they share similar body text; the label distinguishes them).
        Assert.DoesNotContain("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routing: InsufficientGrounding renders "LOW CONFIDENCE" label
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_InsufficientGrounding_RendersLowConfidenceLabel()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.InsufficientGrounding)
            .Add(x => x.Detail, BuildDetail()));

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: UpstreamThrottled does NOT render routing CTAs
    // Rationale: transient rate-limit; routing users away misleads about recoverability
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_UpstreamThrottled_DoesNotRender_CommunityResourceCards()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Detail carries marketplace resources to verify the panel SUPPRESSES them.
        var detail = BuildDetail(includeMarketplaceResources: true);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.UpstreamThrottled)
            .Add(x => x.Detail, detail));

        // Routing CTAs must not be present for transient system-state categories.
        Assert.Empty(cut.FindAll("[data-testid='refusal-routes']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: CostCeilingHit does NOT render routing CTAs
    // Rationale: operational constraint; per IRefusalRecoveryService, no recovery
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_CostCeilingHit_DoesNotRender_CommunityResourceCards()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = BuildDetail(includeMarketplaceResources: true);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.CostCeilingHit)
            .Add(x => x.Detail, detail));

        Assert.Empty(cut.FindAll("[data-testid='refusal-routes']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: LowConfidence renders ConfidenceBreakdown when Confidence is non-null
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_LowConfidence_RendersConfidenceBreakdown_WhenConfidenceNonNull()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var confidence = new ConfidenceBreakdown(
            RetrievalSimilarity: 0.55,
            ModelSelfReported: 0.60,
            CitationCoverage: 0.45,
            Composite: 0.52,
            Threshold: 0.65);

        var detail = BuildDetail(confidence: confidence);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.LowModelConfidence)
            .Add(x => x.Detail, detail));

        // ConfidenceBreakdown renders [data-testid='confidence-breakdown'].
        cut.Find("[data-testid='confidence-breakdown']");

        // All three score bars must be present.
        cut.Find("[data-testid='score-retrieval']");
        cut.Find("[data-testid='score-model']");
        cut.Find("[data-testid='score-citation']");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: LowConfidence does NOT render ConfidenceBreakdown when Confidence is null
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_LowConfidence_DoesNotRenderConfidenceBreakdown_WhenConfidenceNull()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Null confidence — Wave 1 state before the R1 fill arrives.
        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: null);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.LowModelConfidence)
            .Add(x => x.Detail, detail));

        Assert.Empty(cut.FindAll("[data-testid='confidence-breakdown']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: MissingWhat renders when non-null on a recovery-eligible category
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_OutOfScope_RendersMissingWhat_WhenProvided()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        const string missingWhat = "No documentation found for 'custom artwork' questions.";

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: missingWhat,
            SuggestedRephrase: null);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        cut.Find("[data-testid='missing-what-section']");
        Assert.Contains(missingWhat, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: SuggestedRephrase hides when null (no empty box rendered)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_OutOfScope_HidesSuggestedRephrase_WhenNull()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: null); // explicitly null

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // SuggestedRephrase section must not render when null.
        Assert.Empty(cut.FindAll("[data-testid='suggested-rephrase-section']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: SuggestedRephrase renders when provided
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_OutOfScope_RendersSuggestedRephrase_WhenProvided()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        const string rephrase = "Try: 'What manuals are available for the Stern Avengers?'";

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: rephrase);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        cut.Find("[data-testid='suggested-rephrase-section']");
        Assert.Contains(rephrase, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: SuggestedRephrase renders as a clickable MudButton when
    //           QuestionSelected delegate is provided
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefusalPanel_SuggestedRephrase_RendersButton_WhenDelegateProvided()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        const string rephrase = "What service bulletins exist for Stern Godzilla?";

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: rephrase);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail)
            .Add(x => x.QuestionSelected, EventCallback.Factory.Create<string>(this, _ => { })));

        // SuggestedRephrase section must be present.
        cut.Find("[data-testid='suggested-rephrase-section']");

        // A clickable button must be rendered (not plain text).
        cut.Find("[data-testid='suggested-rephrase-button']");

        // The button must carry the correct aria-label for screen readers.
        var btn = cut.Find("[data-testid='suggested-rephrase-button']");
        Assert.Equal($"Ask: {rephrase}", btn.GetAttribute("aria-label"));

        await Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: Clicking SuggestedRephrase button raises QuestionSelected
    //           callback with the rephrase text
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefusalPanel_SuggestedRephrase_Click_RaisesCallback_WithRephraseText()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        const string rephrase = "What service bulletins exist for Stern Godzilla?";
        string? received = null;

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: rephrase);

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail)
            .Add(x => x.QuestionSelected, EventCallback.Factory.Create<string>(
                this, q => received = q)));

        var btn = cut.Find("[data-testid='suggested-rephrase-button']");
        await cut.InvokeAsync(() => btn.Click());

        Assert.Equal(rephrase, received);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: SuggestedRephrase renders as plain text when no delegate is set
    //           (backward-compat: standalone / static rendering contexts)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_SuggestedRephrase_RendersPlainText_WhenNoDelegateProvided()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        const string rephrase = "What service bulletins exist for Stern Godzilla?";

        var detail = new RefusalDetail(
            Confidence: null,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: rephrase);

        // No QuestionSelected delegate.
        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, detail));

        // Section must still be present.
        cut.Find("[data-testid='suggested-rephrase-section']");

        // No button — plain text only.
        Assert.Empty(cut.FindAll("[data-testid='suggested-rephrase-button']"));

        // Rephrase text must be visible.
        Assert.Contains(rephrase, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: UpstreamThrottled renders RetryHint with seconds when provided
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_UpstreamThrottled_RendersRetryHint_WithSeconds()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.UpstreamThrottled)
            .Add(x => x.Detail, null)
            .Add(x => x.RetryAfterSeconds, 30));

        // RetryHint renders [data-testid='retry-hint'] with the second count.
        cut.Find("[data-testid='retry-hint']");
        Assert.Contains("30 second", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Behavior: RefusalPanel with null Detail renders without exception
    // (forward-compat: Wave 1 state before R2/R3/R4 fill the sub-fields)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefusalPanel_NullDetail_RendersWithoutException()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<RefusalPanel>(p => p
            .Add(x => x.Category, RefusalCategory.OutOfScope)
            .Add(x => x.Detail, null));

        cut.Find("[data-testid='refusal-panel']");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static RefusalDetail BuildDetail(
        ConfidenceBreakdown? confidence = null,
        bool includeMarketplaceResources = false)
    {
        IReadOnlyList<CommunityResource>? resources = null;

        if (includeMarketplaceResources)
        {
            resources = new List<CommunityResource>
            {
                new("Facebook Marketplace", "https://www.facebook.com/marketplace/category/pinball-machines", "marketplace", "Local pinball listings."),
                new("Mr. Pinball", "https://mrpinball.com", "marketplace", "Long-running pinball classifieds."),
                new("Pinside Market", "https://pinside.com/pinball/market", "marketplace", "Community buy/sell section."),
            };
        }

        return new RefusalDetail(
            Confidence: confidence,
            RelatedMachines: null,
            CommunityResources: resources,
            MissingWhat: null,
            SuggestedRephrase: null);
    }
}
