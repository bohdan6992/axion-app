using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace TradingBridgeApi.Controllers.Tape
{
    [ApiController]
    [Route("api/tape/stream")]
    public sealed class TapeSseController : ControllerBase
    {
        [HttpGet]
        public async Task Stream(CancellationToken cancellationToken)
        {
            var response = Response;

            response.Headers["Content-Type"] = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";

            await using var writer = new StreamWriter(response.Body);

            // Щоб не дублювати події в межах однієї хвилини
            var lastMinuteIdx = -1;

            while (!cancellationToken.IsCancellationRequested)
            {
                var nowNy = DateTime.UtcNow.AddHours(-5); // грубий NY-час, як у Tape
                var minuteIdx = nowNy.Hour * 60 + nowNy.Minute;

                if (minuteIdx != lastMinuteIdx)
                {
                    lastMinuteIdx = minuteIdx;

                    var payload = new
                    {
                        dateNy = nowNy.ToString("yyyy-MM-dd"),
                        minuteNy = nowNy.ToString("HH:mm"),
                        minuteIdx
                    };

                    var json = JsonSerializer.Serialize(payload);

                    await writer.WriteAsync("event: minute\n");
                    await writer.WriteAsync($"data: {json}\n\n");
                    await writer.FlushAsync();
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }
}
