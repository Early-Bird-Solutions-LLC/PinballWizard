using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;

namespace PinballWizard.Api.Auth;

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/auth/google", HandleGoogleLogin);
        group.MapGet("/auth/callback", HandleCallback);
        group.MapGet("/auth/me", HandleMe).RequireAuthorization();
    }

    private static IResult HandleGoogleLogin(HttpContext context)
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = "/api/auth/callback"
        };
        return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult> HandleCallback(
        HttpContext context,
        IJwtTokenService jwtService)
    {
        var result = await context.AuthenticateAsync(GoogleDefaults.AuthenticationScheme);
        if (!result.Succeeded)
            return Results.Unauthorized();

        var claims = result.Principal!.Claims.ToList();
        var userId = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "";
        var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value ?? "";
        var name = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? "";

        var token = jwtService.GenerateToken(userId, email, name);

        // Redirect to the frontend with the token
        return Results.Redirect($"/?token={token}");
    }

    private static IResult HandleMe(ClaimsPrincipal user)
    {
        return Results.Ok(new
        {
            userId = user.FindFirstValue(ClaimTypes.NameIdentifier),
            email = user.FindFirstValue(ClaimTypes.Email),
            name = user.FindFirstValue(ClaimTypes.Name)
        });
    }
}
