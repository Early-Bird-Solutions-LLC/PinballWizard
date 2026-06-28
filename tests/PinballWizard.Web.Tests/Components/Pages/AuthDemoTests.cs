using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Pages;

// bUnit smoke tests for AuthDemo.razor.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component must have a
// bUnit smoke test. AuthDemo is a static SSR public page — no services,
// no async lifecycle, no interaction.
//
// Extends AsyncBunitContext (not bare BunitContext) because AppBulletList
// renders MudList which registers MudBlazor.KeyInterceptorService. That
// service implements only IAsyncDisposable, so xUnit's synchronous Dispose()
// throws. AsyncBunitContext implements IAsyncLifetime so xUnit uses
// DisposeAsync() for teardown.
public sealed class AuthDemoTests : AsyncBunitContext
{
    public AuthDemoTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Page renders without exception
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthDemo_Renders_WithoutException()
    {
        var cut = Render<AuthDemo>();

        cut.Find("[data-testid='auth-demo-page']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Page heading contains "Authentication"
    //    AppPageHeader renders the title inline — assert on page markup.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthDemo_Heading_ContainsAuthentication()
    {
        var cut = Render<AuthDemo>();

        Assert.Contains(
            "Authentication",
            cut.Find("[data-testid='auth-demo-page']").TextContent,
            StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. All four named sections render
    //    Behavioral: content structure matches the showcase contract.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthDemo_AllSections_ArePresent()
    {
        var cut = Render<AuthDemo>();

        cut.Find("[data-testid='auth-demo-problem-heading']");
        cut.Find("[data-testid='auth-demo-solution-heading']");
        cut.Find("[data-testid='auth-demo-flow-heading']");
        cut.Find("[data-testid='auth-demo-security-heading']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Code block is present
    //    Behavioral: the CI workflow snippet is a key showcase element —
    //    if it disappears, the page loses its primary educational value.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthDemo_CodeBlock_IsPresent()
    {
        var cut = Render<AuthDemo>();

        cut.Find("[data-testid='auth-demo-code-block']");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Further reading links are present
    //    Behavioral: outbound links are part of the community-resource posture.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthDemo_LinksSection_IsPresent()
    {
        var cut = Render<AuthDemo>();

        cut.Find("[data-testid='auth-demo-links-list']");
    }
}
