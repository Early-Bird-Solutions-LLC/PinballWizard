using Bunit;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Application.Landing;
using PinballWizard.Web.Components.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Landing;

// bUnit behavioral tests for LiveStatusBadge.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. Tests assert behavior (color semantics, tooltip text) —
// the green/amber/red distinction is a correctness requirement per ADR-0026
// § Landing: null ≠ false in the SystemStatus contract.
//
// CSS class assertions are intentional here — the dot color IS the user-visible
// behavior (the sole visual signal). Asserting the CSS class name is the
// correct behavioral assertion, not a structural smell.
public sealed class LiveStatusBadgeTests
{
    // MudBlazor 9 requires <MudPopoverProvider /> in the same render tree as
    // any popover-capable component. LiveStatusBadge contains a MudTooltip.
    private static IRenderedComponent<LiveStatusBadge> Render(
        BunitContext ctx, SystemStatus? status)
    {
        var fragment = ctx.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<LiveStatusBadge>(1);
            builder.AddAttribute(2, nameof(LiveStatusBadge.Status), status);
            builder.CloseComponent();
        });
        return fragment.FindComponent<LiveStatusBadge>();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Green when all SystemStatus fields are true
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_AllTrue_RendersGreenDot()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var status = new SystemStatus(CosmosHealthy: true, FoundryHealthy: true, AiSearchHealthy: true);
        var cut = Render(ctx, status);

        var dot = cut.Find("[data-testid='live-status-dot']");
        Assert.Contains("status-dot--green", dot.GetAttribute("class") ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Amber when any field is null (unknown — endpoint not wired)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_AnyNull_RendersAmberDot()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // CosmosHealthy is null — "unknown / dependency not wired" per ADR-0026.
        var status = new SystemStatus(CosmosHealthy: null, FoundryHealthy: true, AiSearchHealthy: true);
        var cut = Render(ctx, status);

        var dot = cut.Find("[data-testid='live-status-dot']");
        Assert.Contains("status-dot--amber", dot.GetAttribute("class") ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Amber when Status is null (endpoint call pending / fallback)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_NullStatus_RendersAmberDot()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render(ctx, status: null);

        var dot = cut.Find("[data-testid='live-status-dot']");
        Assert.Contains("status-dot--amber", dot.GetAttribute("class") ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Red when any field is false (known-degraded — distinct from null)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_AnyFalse_RendersRedDot()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // FoundryHealthy is false — "known-unhealthy". This is distinct from
        // null ("unknown") per the endpoint contract. The badge must be red,
        // not amber, so operations/prospects see the real degradation signal.
        var status = new SystemStatus(CosmosHealthy: true, FoundryHealthy: false, AiSearchHealthy: true);
        var cut = Render(ctx, status);

        var dot = cut.Find("[data-testid='live-status-dot']");
        Assert.Contains("status-dot--red", dot.GetAttribute("class") ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Red when AiSearch is false (third field is also checked)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_AiSearchFalse_RendersRedDot()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var status = new SystemStatus(CosmosHealthy: true, FoundryHealthy: true, AiSearchHealthy: false);
        var cut = Render(ctx, status);

        var dot = cut.Find("[data-testid='live-status-dot']");
        Assert.Contains("status-dot--red", dot.GetAttribute("class") ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. Tooltip enumerates per-system status (behavior: aria-label)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_Tooltip_EnumeratesPerSystemStatus()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var status = new SystemStatus(CosmosHealthy: true, FoundryHealthy: null, AiSearchHealthy: false);
        var cut = Render(ctx, status);

        // The aria-label on the dot contains the per-system tooltip text.
        var dot = cut.Find("[data-testid='live-status-dot']");
        var label = dot.GetAttribute("aria-label") ?? string.Empty;

        Assert.Contains("Cosmos", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Foundry", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI Search", label, StringComparison.OrdinalIgnoreCase);
        // Healthy / Unknown / Degraded per status values
        Assert.Contains("Healthy", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unknown", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Degraded", label, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. Persistent neutral label — context without hover (WCAG 1.4.1/1.4.13),
    //    and state-independent so a degraded state never broadcasts a scary
    //    word to prospects (colour + tooltip carry the actual state).
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LiveStatusBadge_VisibleLabel_IsNeutral_EvenWhenDegraded()
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Known-degraded (red) state.
        var status = new SystemStatus(CosmosHealthy: true, FoundryHealthy: false, AiSearchHealthy: true);
        var cut = Render(ctx, status);

        // The at-rest visible label is the neutral "System status" — NOT the
        // state word. State is conveyed by the dot colour, detail by the tooltip.
        var visibleLabel = cut.Find("[data-testid='live-status-label']");
        Assert.Equal("System status", visibleLabel.TextContent.Trim());

        // The dot still signals the real (degraded) state via colour.
        var dot = cut.Find("[data-testid='live-status-dot']");
        Assert.Contains("status-dot--red", dot.GetAttribute("class") ?? string.Empty,
            StringComparison.Ordinal);
    }
}
