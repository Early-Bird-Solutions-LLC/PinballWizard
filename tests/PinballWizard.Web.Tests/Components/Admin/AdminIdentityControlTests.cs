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
        Assert.StartsWith(AdminSignIn.SignInPath, link.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='admin-identity']"));
        Assert.Empty(cut.FindAll("[data-testid='admin-signout']"));
    }

    [Fact]
    public void Authenticated_RendersIdentityAndSignOut()
    {
        this.AddAuthorization().SetAuthorized("jim@example.com");
        var cut = RenderControl();
        cut.Find("[data-testid='admin-identity']");
        var signOut = cut.Find("[data-testid='admin-signout']");
        Assert.Equal(AdminSignIn.SignOutPath, signOut.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='admin-signin']"));
    }
}
