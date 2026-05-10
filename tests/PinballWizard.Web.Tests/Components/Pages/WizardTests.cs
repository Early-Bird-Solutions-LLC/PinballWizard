using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Landing;
using PinballWizard.Web.Clients;
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
// Wave 2 PR-D-stream: WizardStreamingPlaceholder removed; WizardAnswerStream
// is now the primary surface. Tests updated:
//   - Register IWizardLandingClient (injected by Wizard.razor for slug resolution).
//   - Assert WizardAnswerStream renders (question input present in Idle state).
//   - MountsInsideMainLayout asserts the chrome is still in place.
public sealed class WizardTests : TestContext
{
    public WizardTests()
    {
        // MudBlazor components require MudServices in the DI container.
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        // WizardAnswerStream injects IWizardStreamingClient.
        // Wizard.razor injects IWizardLandingClient for slug resolution.
        // Register both BEFORE calling GetRequiredService — bUnit locks
        // the service provider on the first GetService call.
        Services.AddSingleton(Substitute.For<IWizardStreamingClient>());

        var landingClient = Substitute.For<IWizardLandingClient>();
        // Return null (endpoint down / no slug) — triggers slug-as-question fallback.
        landingClient
            .GetLandingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LandingResponse?>(null));
        Services.AddSingleton(landingClient);

        // Logging required by WizardAnswerStream.
        Services.AddLogging();

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
        // Act — mount the Wizard page (now contains WizardAnswerStream).
        var cut = RenderComponent<WizardPage>();

        // Assert — WizardAnswerStream renders in Idle state with the question input.
        cut.Find("[data-testid='wizard-answer-stream']");
        cut.Find("[data-testid='ask-button']");
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

        // Assert — the WizardAnswerStream delight surface is within the chrome.
        cut.Find("[data-testid='wizard-answer-stream']");
    }
}
