using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Refusal;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Refusal;

// bUnit smoke tests for the six per-category subview components.
//
// Per ADR-0026 PR self-audit item 9(d): every new Razor component in the
// locked delight surface set must have a bUnit smoke test.
//
// Each test:
//   - Creates its own BunitContext and registers MudBlazor services BEFORE rendering.
//   - Renders the component in isolation (not via RefusalPanel routing).
//   - Asserts no throw + at least one meaningful rendered output specific to that view.
//
// Tests follow Method_State_Expectation naming.
//
// Spec conformance: modern-lcd.md §"Refusal that directs out"
// Category labels are ALL CAPS display type (Barlow Condensed via h4 MudText).
// "refusal-category-label" class + data-testid="refusal-category-label" are
// the observable contracts pinned here.
public sealed class RefusalCategoryViewTests
{
    private static BunitContext BuildCtx()
    {
        var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }


    // ─────────────────────────────────────────────────────────────────────────
    // CostCeilingView
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CostCeilingView_Render_ContainsTooLargeHeadline()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<CostCeilingView>();

        // The fixed headline for cost ceiling is "Request Too Large to Process".
        Assert.Contains("Request Too Large to Process", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // InsufficientGroundingView — spec: category label is "LOW CONFIDENCE"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsufficientGroundingView_Render_ContainsCategoryLabel()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<InsufficientGroundingView>();

        // Per modern-lcd.md §"Per-category framing": InsufficientGrounding
        // maps to "LOW CONFIDENCE" label. data-testid is the observable contract.
        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    [Fact]
    public async Task InsufficientGroundingView_Render_HasRefusalCategoryLabelClass()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<InsufficientGroundingView>();

        // CSS class "refusal-category-label" is required by RefusalPanel.razor.css
        // for the accent-refusal color treatment.
        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("refusal-category-label", label.ClassName ?? string.Empty);
    }

    [Fact]
    public async Task InsufficientGroundingView_WithRelatedMachines_RendersRelatedMachinesSection()
    {
        await using var ctx = BuildCtx();
        var machines = new List<RelatedMachine>
        {
            new("mch_avengers", "Stern Avengers", "https://opdb.org/machines/stern-avengers"),
        };

        // MudBlazor 9: MudList/MudListItem require MudPopoverProvider in the same
        // render tree. Render as sibling fragment (see AdminSettingsTests pattern).
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<InsufficientGroundingView>(1);
            builder.AddAttribute(2, nameof(InsufficientGroundingView.RelatedMachines), (IReadOnlyList<RelatedMachine>)machines);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<InsufficientGroundingView>();

        cut.Find("[data-testid='related-machines-section']");
        Assert.Contains("Stern Avengers", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LowConfidenceView — spec: category label is "LOW CONFIDENCE"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LowConfidenceView_Render_ContainsCategoryLabel()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<LowConfidenceView>();

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    [Fact]
    public async Task LowConfidenceView_WithConfidence_RendersBreakdownElement()
    {
        await using var ctx = BuildCtx();
        var confidence = new ConfidenceBreakdown(
            RetrievalSimilarity: 0.50,
            ModelSelfReported: 0.55,
            CitationCoverage: 0.40,
            Composite: 0.48,
            Threshold: 0.65);

        var cut = ctx.Render<LowConfidenceView>(p => p
            .Add(x => x.Confidence, confidence));

        cut.Find("[data-testid='confidence-breakdown']");
    }

    [Fact]
    public async Task LowConfidenceView_WithNullConfidence_DoesNotRenderBreakdown()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<LowConfidenceView>(p => p
            .Add(x => x.Confidence, null));

        Assert.Empty(cut.FindAll("[data-testid='confidence-breakdown']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NoCitationView — spec: category label is "LOW CONFIDENCE"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoCitationView_Render_ContainsCategoryLabel()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<NoCitationView>();

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("LOW CONFIDENCE", label.TextContent.ToUpperInvariant());
    }

    [Fact]
    public async Task NoCitationView_WithRelatedMachines_RendersRelatedMachinesSection()
    {
        await using var ctx = BuildCtx();
        var machines = new List<RelatedMachine>
        {
            new("mch_iron_maiden", "Stern Iron Maiden", null),
        };

        // MudBlazor 9: MudList/MudListItem require MudPopoverProvider in the same
        // render tree. Render as sibling fragment (see AdminSettingsTests pattern).
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<NoCitationView>(1);
            builder.AddAttribute(2, nameof(NoCitationView.RelatedMachines), (IReadOnlyList<RelatedMachine>)machines);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<NoCitationView>();

        cut.Find("[data-testid='related-machines-section']");
        Assert.Contains("Stern Iron Maiden", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OutOfScopeView — spec: category label is "OUT OF SCOPE"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OutOfScopeView_Render_ContainsCategoryLabel()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<OutOfScopeView>();

        // Per modern-lcd.md §"Per-category framing": OutOfScope → "OUT OF SCOPE".
        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("OUT OF SCOPE", label.TextContent.ToUpperInvariant());
    }

    [Fact]
    public async Task OutOfScopeView_Render_HasRefusalCategoryLabelClass()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<OutOfScopeView>();

        var label = cut.Find("[data-testid='refusal-category-label']");
        Assert.Contains("refusal-category-label", label.ClassName ?? string.Empty);
    }

    [Fact]
    public async Task OutOfScopeView_WithRelatedMachines_RendersRelatedMachinesSection()
    {
        await using var ctx = BuildCtx();
        var machines = new List<RelatedMachine>
        {
            new("mch_jjp_potc", "JJP Pirates of the Caribbean", "https://opdb.org/machines/jjp-potc"),
        };

        // MudBlazor 9: MudList/MudListItem require MudPopoverProvider in the same
        // render tree. Render as sibling fragment (see AdminSettingsTests pattern).
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OutOfScopeView>(1);
            builder.AddAttribute(2, nameof(OutOfScopeView.RelatedMachines), (IReadOnlyList<RelatedMachine>)machines);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<OutOfScopeView>();

        cut.Find("[data-testid='related-machines-section']");
        Assert.Contains("JJP Pirates of the Caribbean", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfScopeView_WithNullRelatedMachines_DoesNotRenderRelatedMachinesSection()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<OutOfScopeView>(p => p
            .Add(x => x.RelatedMachines, null));

        Assert.Empty(cut.FindAll("[data-testid='related-machines-section']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OutOfScopeView — conditional routing-promise text (Fix 2)
    //
    // "The community below can help" is only honest when routing CTAs follow.
    // HasCommunityResources gates the promise; without it the text should not
    // reference a destination that doesn't exist.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OutOfScopeView_HasCommunityResources_True_ReasonMentionsCommunity()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<OutOfScopeView>(p => p
            .Add(x => x.HasCommunityResources, true));

        var reason = cut.Find("[data-testid='out-of-scope-reason']");
        Assert.Contains("community", reason.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfScopeView_HasCommunityResources_False_ReasonOmitsCommunityBelow()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<OutOfScopeView>(p => p
            .Add(x => x.HasCommunityResources, false));

        // When no routing CTAs are present the reason must not promise a
        // nonexistent "community below" — per modern-lcd.md §Posture: honest framing.
        var reason = cut.Find("[data-testid='out-of-scope-reason']");
        Assert.DoesNotContain("community below", reason.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UpstreamThrottledView
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpstreamThrottledView_Render_ContainsHighDemandHeadline()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<UpstreamThrottledView>();

        // Headline per UpstreamThrottledView.razor.
        Assert.Contains("High Demand Right Now", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpstreamThrottledView_WithRetryAfterSeconds_RendersRetryHint()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<UpstreamThrottledView>(p => p
            .Add(x => x.RetryAfterSeconds, 30));

        // data-testid is on the MudAlert element in UpstreamThrottledView.razor.
        cut.Find("[data-testid='retry-hint']");
        Assert.Contains("30 second", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpstreamThrottledView_WithNullRetryAfterSeconds_RendersGenericRetryHint()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<UpstreamThrottledView>(p => p
            .Add(x => x.RetryAfterSeconds, null));

        // The else-branch renders the generic hint with data-testid='retry-hint'.
        cut.Find("[data-testid='retry-hint']");
        Assert.Contains("few seconds", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
