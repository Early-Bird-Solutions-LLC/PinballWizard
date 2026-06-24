using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Web.Security;
using Xunit;

namespace PinballWizard.Web.Tests.Security;

// Unit tests for AdminActionGuard — the server-side authorization boundary for
// admin mutations. Exercises the real AuthorizationService with the production
// AdminOnly policy (RequireRole("GlobalAdmin")) so allow/deny is proven against
// the actual policy, not a mock.
public sealed class AdminActionGuardTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    private AdminActionGuard BuildGuard()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(o =>
            o.AddPolicy(AuthorizationPolicies.AdminOnly, p => p.RequireRole("GlobalAdmin")));
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return new AdminActionGuard(authz);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task IsAdminAsync_GlobalAdminPrincipal_ReturnsTrue()
    {
        var guard = BuildGuard();
        var admin = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "GlobalAdmin")], "test"));

        Assert.True(await guard.IsAdminAsync(admin));
    }

    [Fact]
    public async Task IsAdminAsync_AnonymousPrincipal_ReturnsFalse()
    {
        var guard = BuildGuard();
        var anon = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(await guard.IsAdminAsync(anon));
    }

    [Fact]
    public async Task IsAdminAsync_NonAdminAuthenticatedPrincipal_ReturnsFalse()
    {
        var guard = BuildGuard();
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "joe")], "test"));

        Assert.False(await guard.IsAdminAsync(user));
    }

    [Fact]
    public async Task IsAdminAsync_NullAuthState_ReturnsFalse()
    {
        var guard = BuildGuard();

        Assert.False(await guard.IsAdminAsync((Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>?)null));
    }
}
