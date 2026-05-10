using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Degraded;
using PinballWizard.Web.Components.Layout;
using PinballWizard.Web.Components.Theming;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// Per ADR-0026 PR self-audit item 9(d): every Razor component must have
// a bUnit smoke test. MainLayout is the public chrome wrapper that ALL
// public /wizard pages render inside. This test mounts MainLayout with
// synthetic @Body content and asserts:
//   1. MudAppBar is present (chrome rendered).
//   2. BrandHeader brand text is present.
//   3. TiltErrorBoundary is wired — verified structurally by the error-boundary
//      render wrapper being present in the component tree.
//
// The TiltErrorBoundary throw-and-recover behavior is asserted separately
// in TiltErrorBoundaryTests.cs.
//
// Category: chrome wrapper — MainLayout wraps MudBlazor layout primitives.
// ADR-0008, ADR-0026 § 6.
public sealed class MainLayoutTests : TestContext
{
    public MainLayoutTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        // OutageBanner (added to MainLayout in PR-D-degraded) injects
        // IClientDegradationStore — register the real implementation.
        Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();
        // Fake NavigationManager so BrandHeader nav links resolve.
        var navManager = Services.GetRequiredService<FakeNavigationManager>();
        _ = navManager; // registered automatically by bUnit TestContext.
    }

    [Fact]
    public void MainLayout_Renders_MudAppBar()
    {
        // Arrange — render MainLayout with a body stub.
        var cut = RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddContent(1, "Body content");
                builder.CloseElement();
            }));

        // Assert — MudAppBar is present in the chrome.
        cut.FindComponent<MudAppBar>();
    }

    [Fact]
    public void MainLayout_Renders_BrandHeader()
    {
        var cut = RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddContent(1, "Body content");
                builder.CloseElement();
            }));

        // Assert — BrandHeader (PinballWizard logo text) is rendered inside the AppBar.
        cut.FindComponent<BrandHeader>();
    }

    [Fact]
    public void MainLayout_WrapsBody_InTiltErrorBoundary()
    {
        var cut = RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "data-testid", "body-sentinel");
                builder.AddContent(2, "Body content");
                builder.CloseElement();
            }));

        // Assert — the body sentinel is rendered (TiltErrorBoundary passes through
        // child content when no exception is active).
        cut.Find("[data-testid='body-sentinel']");
        // Assert — TiltErrorBoundary component is in the tree.
        cut.FindComponent<TiltErrorBoundary>();
    }
}
