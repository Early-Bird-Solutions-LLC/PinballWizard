using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Security;

public sealed class AdminSignInTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Href_NoReturnUrl_IsBareSignInPath(string? returnUrl) =>
        Assert.Equal(AdminSignIn.SignInPath, AdminSignIn.Href(returnUrl));

    [Fact]
    public void Href_WithReturnUrl_AppendsEncodedRedirect()
    {
        var href = AdminSignIn.Href("/admin/jobs/j/executions/e");
        Assert.Equal(
            "/MicrosoftIdentity/Account/SignIn?redirectUri=%2Fadmin%2Fjobs%2Fj%2Fexecutions%2Fe",
            href);
    }

    [Fact]
    public void Paths_AreTheMicrosoftIdentityAccountEndpoints()
    {
        Assert.Equal("/MicrosoftIdentity/Account/SignIn", AdminSignIn.SignInPath);
        Assert.Equal("/MicrosoftIdentity/Account/SignOut", AdminSignIn.SignOutPath);
    }
}
