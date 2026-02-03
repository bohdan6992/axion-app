using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingBridgeApi.Services.Tape
{
    public sealed class TapeWriterHostedService : IHostedService, IDisposable
    {
        private readonly ITapeWriter _writer;
        private readonly ILogger<TapeWriterHostedService> _log;

        private Timer? _timer;
        private DateTime _lastWrittenUtcMinute = DateTime.MinValue; // ✅ idempotency at host level
        private readonly SemaphoreSlim _gate = new(1, 1);

        public TapeWriterHostedService(ITapeWriter writer, ILogger<TapeWriterHostedService> log)
        {
            _writer = writer;
            _log = log;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation("[TapeHost] starting");

            // tick every 5s; we will write only once per UTC minute
            _timer = new Timer(async _ => await TimerTickAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }

        private async Task TimerTickAsync()
        {
            // never overlap
            if (!await _gate.WaitAsync(0))
                return;

            try
            {
                var utcNow = DateTime.UtcNow;

                // only attempt near the top of the minute (0..7s)
                if (utcNow.Second > 7)
                    return;

                var utcMinute = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc);

                if (utcMinute <= _lastWrittenUtcMinute)
                    return;

                await _writer.WriteMinuteAsync(utcMinute);
                _lastWrittenUtcMinute = utcMinute;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[TapeHost] tick failed");
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation("[TapeHost] stopping");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _gate.Dispose();
        }
    }
}
