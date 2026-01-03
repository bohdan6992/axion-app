using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace TradingBridgeApi.Auth;

public sealed class JwtTokenService
{
    private readonly IConfiguration _cfg;

    public JwtTokenService(IConfiguration cfg) => _cfg = cfg;

    public string CreateToken(IdentityUser user, string role)
    {
        var issuer = _cfg["Auth:Jwt:Issuer"] ?? "AxionLocal";
        var audience = _cfg["Auth:Jwt:Audience"] ?? "AxionLocal";
        var key = _cfg["Auth:Jwt:Key"] ?? throw new InvalidOperationException("Auth:Jwt:Key missing");

        var lifetimeMinutes = int.TryParse(_cfg["Auth:Jwt:LifetimeMinutes"], out var m) ? m : 720;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Email ?? ""),
            new Claim(ClaimTypes.Role, role),
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256
        );

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
