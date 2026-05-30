using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// bUnit smoke tests for ToolCallBreadcrumb.
//
// ToolCallBreadcrumb is a delight surface sub-component of WizardAnswerStream
// (ADR-0026 § 3). It renders a MudChip pill when ToolName is non-empty, and
// renders nothing (no DOM node) when ToolName is null or empty.
//
// Inherits AsyncBunitContext so xUnit uses DisposeAsync() for teardown —
// prevents the MudBlazor.KeyInterceptorService IAsyncDisposable-only
// sync-dispose exception (same pattern as MainLayoutTests, WizardThinkingIndicatorTests).
//
// Tests follow Method_State_Expectation naming.
public sealed class ToolCallBreadcrumbTests : AsyncBunitContext
{
    public ToolCallBreadcrumbTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Null ToolName renders nothing
    //
    // When WizardAnswerStream has no active tool call, it sets ToolName to null.
    // The breadcrumb must produce no DOM — no empty chip placeholder visible.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallBreadcrumb_NullToolName_RendersNothing()
    {
        var cut = Render<ToolCallBreadcrumb>(p => p
            .Add(x => x.ToolName, (string?)null));

        Assert.Empty(cut.FindAll("[data-testid='tool-call-breadcrumb']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Empty ToolName renders nothing
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallBreadcrumb_EmptyToolName_RendersNothing()
    {
        var cut = Render<ToolCallBreadcrumb>(p => p
            .Add(x => x.ToolName, string.Empty));

        Assert.Empty(cut.FindAll("[data-testid='tool-call-breadcrumb']"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Known tool name renders breadcrumb with correct friendly label
    //
    // "searchCorpus" maps to "Searching corpus…" (per ToolCallBreadcrumb.razor
    // FriendlyLabel switch). This asserts both the container element and the
    // human-readable label that the user sees.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallBreadcrumb_SearchCorpusToolName_RendersBreadcrumbWithSearchingLabel()
    {
        var cut = Render<ToolCallBreadcrumb>(p => p
            .Add(x => x.ToolName, "searchCorpus"));

        cut.Find("[data-testid='tool-call-breadcrumb']");
        Assert.Contains("Searching corpus", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // getMachineByTitle renders breadcrumb with machine-lookup label
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallBreadcrumb_GetMachineByTitleToolName_RendersMachineLookupLabel()
    {
        var cut = Render<ToolCallBreadcrumb>(p => p
            .Add(x => x.ToolName, "getMachineByTitle"));

        cut.Find("[data-testid='tool-call-breadcrumb']");
        Assert.Contains("Looking up machine record", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unknown tool name renders breadcrumb with "Working…" fallback
    //
    // New tool functions added in future phases should show "Working…" without
    // requiring a code change to this component. The switch default branch covers
    // any unrecognized tool name — this test pins that forward-compatibility contract.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallBreadcrumb_UnknownToolName_RendersBreadcrumbWithWorkingFallback()
    {
        var cut = Render<ToolCallBreadcrumb>(p => p
            .Add(x => x.ToolName, "someFutureUnknownTool"));

        cut.Find("[data-testid='tool-call-breadcrumb']");
        Assert.Contains("Working", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Breadcrumb carries aria-label for screen readers
    //
    // The aria-label="Tool call: <FriendlyLabel>" attribute is the only
    // accessibility surface for the pill. Screen readers announce "Tool call:
    // Searching corpus…" — this test pins the a11y contract.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallBreadcrumb_SearchCorpusToolName_HasAriaLabelWithFriendlyLabel()
    {
        var cut = Render<ToolCallBreadcrumb>(p => p
            .Add(x => x.ToolName, "searchCorpus"));

        var element = cut.Find("[data-testid='tool-call-breadcrumb']");
        var ariaLabel = element.GetAttribute("aria-label");

        Assert.NotNull(ariaLabel);
        Assert.Contains("Searching corpus", ariaLabel, StringComparison.OrdinalIgnoreCase);
    }
}
