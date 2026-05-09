using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using PinballWizard.Web.Components.Pages;
using PinballWizard.Web.Components.Theming;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// Per ADR-0026 PR self-audit item 9(d): every Razor component must have
// a bUnit smoke test. Wizard.razor is the Wave 1 placeholder for the
// primary /wizard route (anonymous, per ADR-0026 § 1). This test mounts
// the component and asserts it renders without exception.
//
// PR-F1 extends these tests to assert layout-aware chrome rendering —
// that the Wizard page content mounts inside the MainLayout chrome
// (MudAppBar + BrandHeader + TiltErrorBoundary wrapper).
//
// Wave 2 tests will assert WizardAnswerStream, RefusalPanel, CitationStrip,
// and streaming behavior once those delight surfaces land.
public sealed class WizardTests : TestContext
{
    public WizardTests()
    {
        // MudBlazor components require MudServices in the DI container.
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        // bUnit registers FakeNavigationManager automatically; ensure it's
        // in place for BrandHeader nav links and NavigationManager injections.
        var _ = Services.GetRequiredService<FakeNavigationManager>();
    }

    [Fact]
    public void Wizard_Renders_WithoutException()
    {
        // Act — mount the placeholder Wizard page.
        var cut = RenderComponent<Wizard>();

        // Assert — the Wave 1 placeholder text is visible in the markup.
        Assert.Contains("Wizard placeholder", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wizard_MountsInsideMainLayout_WhenRenderedWithLayout()
    {
        // Arrange — render MainLayout with Wizard as the body content.
        // This is the layout-aware mount: MainLayout provides the chrome
        // (MudAppBar + BrandHeader + TiltErrorBoundary) that Wizard renders
        // inside when the router selects DefaultLayout = typeof(MainLayout).
        var cut = RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenComponent<Wizard>(0);
                builder.CloseComponent();
            }));

        // Assert — the chrome (AppBar, BrandHeader, error boundary) is present.
        cut.FindComponent<MudAppBar>();
        cut.FindComponent<BrandHeader>();
        cut.FindComponent<TiltErrorBoundary>();

        // Assert — the Wizard page content is also present within the chrome.
        Assert.Contains("Wizard placeholder", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
