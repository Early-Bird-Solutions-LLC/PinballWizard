using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Degraded;
using PinballWizard.Web.Components.Layout;
using PinballWizard.Web.Components.Theming;
using PinballWizard.Web.Components.Wizard;
using Xunit;

// Alias PinballWizard.Web.Components.Pages.Wizard to avoid the name clash
// with the PinballWizard.Web.Components.Wizard namespace introduced in PR-F2.
using WizardPage = PinballWizard.Web.Components.Pages.Wizard;

namespace PinballWizard.Web.Tests.Components.Pages;

// Per ADR-0026 PR self-audit item 9(d): every Razor component must have
// a bUnit smoke test. Wizard.razor is the primary /wizard route (anonymous,
// per ADR-0026 § 1).
//
// PR-F2 amends: WizardStreamingPlaceholder replaces the plain text placeholder.
// Tests now assert the WizardStreamingPlaceholder is present (and IWizardStreamingClient
// is registered in DI) rather than checking for the F1 placeholder text.
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

        // PR-F2: WizardStreamingPlaceholder injects IWizardStreamingClient.
        // Register the mock BEFORE calling GetRequiredService — bUnit locks
        // the service provider on the first GetService call.
        Services.AddSingleton(Substitute.For<IWizardStreamingClient>());

        // PR-D-degraded: OutageBanner (in MainLayout) injects IClientDegradationStore.
        Services.AddScoped<IClientDegradationStore, ClientDegradationStore>();

        // bUnit registers FakeNavigationManager automatically. Resolving it
        // here confirms it is in place for BrandHeader nav links. Note: this
        // call locks the provider so it must come after all AddSingleton calls.
        var _ = Services.GetRequiredService<FakeNavigationManager>();
    }

    [Fact]
    public void Wizard_Renders_WithoutException()
    {
        // Act — mount the Wizard page (contains WizardStreamingPlaceholder).
        var cut = RenderComponent<WizardPage>();

        // Assert — the SSE streaming placeholder button renders.
        // The exact text "Stream hello-world" is in WizardStreamingPlaceholder.
        Assert.Contains("Stream hello-world", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
                builder.OpenComponent<WizardPage>(0);
                builder.CloseComponent();
            }));

        // Assert — the chrome (AppBar, BrandHeader, error boundary) is present.
        cut.FindComponent<MudAppBar>();
        cut.FindComponent<BrandHeader>();
        cut.FindComponent<TiltErrorBoundary>();

        // Assert — the streaming placeholder is within the chrome.
        Assert.Contains("Stream hello-world", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
