using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class AdminIdentityControlTests : AsyncBunitContext
{
    public AdminIdentityControlTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudBlazor 9 wants a MudPopoverProvider present when MudBlazor components render
    // (reference_mudblazor9_bunit_popover_provider) — render it as a sibling.
    private IRenderedComponent<AdminIdentityControl> RenderControl()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminIdentityControl>(1);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminIdentityControl>();
    }

    [Fact]
    public void Anonymous_RendersSignIn_ToSignInEndpoint()
    {
        this.AddAuthorization().SetNotAuthorized();
        var cut = RenderControl();
        var link = cut.Find("[data-testid='admin-signin']");
        var href = link.GetAttribute("href")!;
        Assert.StartsWith(AdminSignIn.SignInPath, href);
        Assert.Contains("redirectUri=", href, System.StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='admin-identity']"));
        Assert.Empty(cut.FindAll("[data-testid='admin-signout']"));
    }

    [Fact]
    public void Authenticated_RendersIdentityAndSignOut()
    {
        this.AddAuthorization().SetAuthorized("jim@example.com");
        var cut = RenderControl();
        var identity = cut.Find("[data-testid='admin-identity']");
        Assert.Contains("jim@example.com", identity.TextContent, System.StringComparison.Ordinal);
        var signOut = cut.Find("[data-testid='admin-signout']");
        Assert.Equal(AdminSignIn.SignOutPath, signOut.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='admin-signin']"));
    }

    // Reproduces the blank "Signed in as" bar: Microsoft.Identity.Web maps
    // Identity.Name to the preferred_username claim, which Entra External ID
    // (CIAM) doesn't always populate — authenticated but nameless. bUnit's
    // SetAuthorized always attaches a Name claim, so an empty value is the
    // closest fixture to a fully-absent preferred_username claim; both
    // resolve identically through DisplayName's IsNullOrWhiteSpace check.
    [Fact]
    public void Authenticated_WithEmptyNameClaim_ShowsFallbackText()
    {
        this.AddAuthorization().SetAuthorized(string.Empty);
        var cut = RenderControl();
        var identity = cut.Find("[data-testid='admin-identity']");
        Assert.Contains("admin (authenticated, no name claim)", identity.TextContent, System.StringComparison.Ordinal);
    }
}
