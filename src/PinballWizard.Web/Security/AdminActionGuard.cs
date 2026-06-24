using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace PinballWizard.Web.Security;

// Server-side authorization boundary for admin mutations. Admin pages are
// publicly viewable ([AllowAnonymous]) with read-only content; every mutation
// handler MUST call this guard before acting, because AuthorizeView / _isAdmin
// only govern rendering — they are not a security boundary. Resolves the
// AdminOnly policy (prod: RequireRole("GlobalAdmin")) against the current user.
public sealed class AdminActionGuard(IAuthorizationService authorizationService)
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public async Task<bool> IsAdminAsync(ClaimsPrincipal user) =>
        (await authorizationService.AuthorizeAsync(user, AuthorizationPolicies.AdminOnly)).Succeeded;

    public async Task<bool> IsAdminAsync(Task<AuthenticationState>? authState)
    {
        var user = authState is null ? Anonymous : (await authState).User;
        return await IsAdminAsync(user);
    }
}
