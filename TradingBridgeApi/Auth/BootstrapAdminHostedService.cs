using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingBridgeApi.Auth;

public sealed class BootstrapAdminHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BootstrapAdminHostedService> _log;
    private readonly IConfiguration _cfg;

    public BootstrapAdminHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<BootstrapAdminHostedService> log,
        IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _cfg = cfg;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = (_cfg["Auth:BootstrapAdmin:Email"] ?? "").Trim();
        var password = _cfg["Auth:BootstrapAdmin:Password"] ?? "";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _log.LogWarning("Bootstrap admin is not configured (Auth:BootstrapAdmin:Email/Password). Skipping.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        // Ensure roles exist
        await EnsureRoleAsync(roles, "Admin");
        await EnsureRoleAsync(roles, "User");

        // Ensure admin user exists
        var user = await users.FindByEmailAsync(email);
        if (user == null)
        {
            user = new IdentityUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true
            };

            var create = await users.CreateAsync(user, password);
            if (!create.Succeeded)
            {
                _log.LogError("Failed to create bootstrap admin user {Email}: {Errors}",
                    email, string.Join("; ", create.Errors.Select(e => $"{e.Code}:{e.Description}")));
                return;
            }

            _log.LogInformation("Bootstrap admin user created: {Email}", email);
        }
        else
        {
            _log.LogInformation("Bootstrap admin user exists: {Email}", email);
        }

        // Ensure Admin role assigned
        if (!await users.IsInRoleAsync(user, "Admin"))
        {
            var addRole = await users.AddToRoleAsync(user, "Admin");
            if (!addRole.Succeeded)
            {
                _log.LogError("Failed to add Admin role to {Email}: {Errors}",
                    email, string.Join("; ", addRole.Errors.Select(e => $"{e.Code}:{e.Description}")));
                return;
            }

            _log.LogInformation("Bootstrap admin role assigned: {Email}", email);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureRoleAsync(RoleManager<IdentityRole> roles, string roleName)
    {
        if (!await roles.RoleExistsAsync(roleName))
        {
            await roles.CreateAsync(new IdentityRole(roleName));
        }
    }
}
