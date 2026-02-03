using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace TradingBridgeApi.Auth
{
    public sealed class IdentityDbInitHostedService : IHostedService
    {
        private readonly IServiceProvider _sp;

        public IdentityDbInitHostedService(IServiceProvider sp) => _sp = sp;

        public Task StartAsync(CancellationToken ct)
        {
            // Run DB creation in background so Kestrel can start immediately.
            return Task.Run(() =>
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AxionIdentityDbContext>();
                db.Database.EnsureCreated();
            }, ct);
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
