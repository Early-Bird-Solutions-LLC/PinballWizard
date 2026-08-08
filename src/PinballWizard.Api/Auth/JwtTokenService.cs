using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PinballWizard.Api.Auth;

public interface IJwtTokenService
{
    string GenerateToken(string userId, string email, string displayName);
}

public sealed class JwtTokenService(IOptions<ApiSettings> settings) : IJwtTokenService
{
    public string GenerateToken(string userId, string email, string displayName)
    {
        var apiSettings = settings.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apiSettings.JwtSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(JwtRegisteredClaimNames.Name, displayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "PinballWizard",
            audience: "PinballWizard",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(apiSettings.JwtExpiryHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
