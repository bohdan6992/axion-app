using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Auth;

namespace TradingBridgeApi.Controllers.Auth;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly SignInManager<IdentityUser> _signIn;
    private readonly UserManager<IdentityUser> _users;
    private readonly AllowlistService _allow;
    private readonly JwtTokenService _jwt;

    public AuthController(
        SignInManager<IdentityUser> signIn,
        UserManager<IdentityUser> users,
        AllowlistService allow,
        JwtTokenService jwt)
    {
        _signIn = signIn;
        _users = users;
        _allow = allow;
        _jwt = jwt;
    }

    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string Token);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();


        // allowlist gate
        if (!_allow.IsAllowed(email))
            return Unauthorized("Not allowed");

        var user = await _users.FindByEmailAsync(email);
        if (user is null)
            return Unauthorized("Invalid credentials");

        var ok = await _signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: false);
        if (!ok.Succeeded)
            return Unauthorized("Invalid credentials");

        // role
        var roles = await _users.GetRolesAsync(user);
        var role = roles.Count > 0 ? roles[0] : "User";

        var token = _jwt.CreateToken(user, role);
        return Ok(new LoginResponse(token));
    }

    // MANUAL provisioning (Admin only): create user + set password + role
    public sealed record CreateUserRequest(string Email, string Password, string Role);

    [HttpPost("admin/create-user")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();


        if (!_allow.IsAllowed(email))
            return BadRequest("Email not in allowlist");

        var existing = await _users.FindByEmailAsync(email);
        if (existing is not null)
            return Conflict("User already exists");

        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var res = await _users.CreateAsync(user, req.Password);
        if (!res.Succeeded)
            return BadRequest(res.Errors);

        var role = string.IsNullOrWhiteSpace(req.Role) ? "User" : req.Role;
        await _users.AddToRoleAsync(user, role);

        return Ok();
    }
}
