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
    // InsufficientGroundingView
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsufficientGroundingView_Render_ContainsNotEnoughHeadline()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<InsufficientGroundingView>();

        // Headline text per InsufficientGroundingView.razor.
        Assert.Contains("Not Enough to Go On", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
    // LowConfidenceView
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LowConfidenceView_Render_ContainsNotConfidentHeadline()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<LowConfidenceView>();

        // Headline per LowConfidenceView.razor.
        Assert.Contains("Not Confident Enough to Answer", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
    // NoCitationView
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoCitationView_Render_ContainsNoDocumentedEvidenceHeadline()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<NoCitationView>();

        // Headline per NoCitationView.razor.
        Assert.Contains("No Documented Evidence", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
    // OutOfScopeView
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OutOfScopeView_Render_ContainsOutsideMyCoverageHeadline()
    {
        await using var ctx = BuildCtx();
        var cut = ctx.Render<OutOfScopeView>();

        // Headline per OutOfScopeView.razor.
        Assert.Contains("Outside My Coverage", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
