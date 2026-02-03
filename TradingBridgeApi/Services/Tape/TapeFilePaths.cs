using System;
using System.IO;

namespace TradingBridgeApi.Services.Tape
{
    public sealed class TapeFilePaths
    {
        public string Root { get; }

        public TapeFilePaths()
        {
            try
            {
                Root = Path.Combine(AxionPaths.AppDataRoot, "tape", "intraday", "v1");
            }
            catch
            {
                Root = Path.Combine(AppContext.BaseDirectory, "tape", "intraday", "v1");
            }

            Directory.CreateDirectory(Root);
        }

        public string DateDir(string dateNy) => Path.Combine(Root, $"dateNy={dateNy}");
        public string MinuteDir(string dateNy, int minuteIdx) => Path.Combine(DateDir(dateNy), $"minuteIdx={minuteIdx:D4}");
        public string MinuteFile(string dateNy, int minuteIdx) => Path.Combine(MinuteDir(dateNy, minuteIdx), "part.parquet");
    }
}
