using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TradingBridgeApi.Auth;

public sealed class AxionIdentityDbContext : IdentityDbContext<IdentityUser, IdentityRole, string>
{
    public AxionIdentityDbContext(DbContextOptions<AxionIdentityDbContext> options) : base(options) { }
}
