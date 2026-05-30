using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// bUnit smoke tests for WizardShell.
//
// WizardShell is a LayoutComponentBase that wraps child content in a
// MudContainer with the wizard-shell CSS class. Per ADR-0026 PR self-audit
// item 9(d), every Razor component must have a bUnit smoke test.
//
// WizardShell is a chrome wrapper (not a locked delight surface per ADR-0026 § 6),
// so two structural pins suffice:
//   1. Renders without exception when given Body content.
//   2. The MudContainer with the wizard-shell class is present.
//   3. Body content is passed through (no swallowing).
//
// Tests follow Method_State_Expectation naming.
public sealed class WizardShellTests : AsyncBunitContext
{
    public WizardShellTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Renders without exception with a body stub
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WizardShell_Render_DoesNotThrow()
    {
        // WizardShell wraps @Body in a MudContainer. Providing a body stub
        // verifies the shell renders cleanly without cascading exceptions.
        var cut = Render<WizardShell>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddContent(1, "body content");
                builder.CloseElement();
            }));

        // The rendered output must be non-empty.
        Assert.False(string.IsNullOrWhiteSpace(cut.Markup));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MudContainer with wizard-shell class is present
    //
    // The CSS class "wizard-shell" is the styling hook used by the
    // /wizard/* page stylesheet. If WizardShell stops emitting this class,
    // all /wizard pages lose their layout contract silently — this test
    // catches that regression.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WizardShell_Render_ContainsMudContainerWithWizardShellClass()
    {
        var cut = Render<WizardShell>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddContent(1, "body content");
                builder.CloseElement();
            }));

        // MudContainer renders a <div> carrying the class names from Class="wizard-shell py-6".
        // bUnit exposes FindComponent to locate the MudContainer in the component tree.
        cut.FindComponent<MudContainer>();
        Assert.Contains("wizard-shell", cut.Markup, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body content passes through the container
    //
    // WizardShell must render @Body — if it accidentally swallows or replaces
    // child content, /wizard/* page content disappears silently.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WizardShell_Render_PassesThroughBodyContent()
    {
        var cut = Render<WizardShell>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "data-testid", "body-sentinel");
                builder.AddContent(2, "sentinel text");
                builder.CloseElement();
            }));

        cut.Find("[data-testid='body-sentinel']");
        Assert.Contains("sentinel text", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
