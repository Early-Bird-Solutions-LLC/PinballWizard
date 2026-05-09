using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// Per ADR-0026 PR self-audit item 9(d): every Razor component must have
// a bUnit smoke test. Wizard.razor is the Wave 1 placeholder for the
// primary /wizard route (anonymous, per ADR-0026 § 1). This test mounts
// the component and asserts it renders without exception.
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
    }

    [Fact]
    public void Wizard_Renders_WithoutException()
    {
        // Act — mount the placeholder Wizard page.
        var cut = RenderComponent<Wizard>();

        // Assert — the Wave 1 placeholder text is visible in the markup.
        Assert.Contains("Wizard placeholder", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
