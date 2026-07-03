using System;

namespace PinballWizard.Web.Security;

// Microsoft.Identity.Web account endpoints + sign-in URL builder. The
// AccountController (Microsoft.Identity.Web.UI 4.10.0) is registered via
// AddMicrosoftIdentityUI() and mapped by app.MapControllers(); AdminLayout's
// working "Sign out" link already proves these paths resolve.
//
// Return-URL param 'redirectUri' VERIFIED against Microsoft.Identity.Web.UI
// 4.10.0 AccountController.SignIn in plan Task 1. Verification method:
// Inspected the XML documentation shipped with the NuGet package at
// ~/.nuget/packages/microsoft.identity.web.ui/4.10.0/lib/net9.0/Microsoft.Identity.Web.UI.xml —
// the SignIn action is declared as:
//   AccountController.SignIn(string scheme, string redirectUri, string loginHint, string domainHint)
// ASP.NET Core model binding maps the HTTP query parameter named 'redirectUri'
// to that action parameter, so '?redirectUri=<encoded-path>' is the correct form.
public static class AdminSignIn
{
    public const string SignInPath = "/MicrosoftIdentity/Account/SignIn";
    public const string SignOutPath = "/MicrosoftIdentity/Account/SignOut";

    private const string ReturnUrlParam = "redirectUri";

    // Sign-in URL that returns to `returnUrl` (a LOCAL relative path such as
    // "/admin/jobs/..."). Bare path when returnUrl is null/whitespace.
    public static string Href(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl)
            ? SignInPath
            : $"{SignInPath}?{ReturnUrlParam}={Uri.EscapeDataString(returnUrl)}";
}
