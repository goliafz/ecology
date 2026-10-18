using System.Diagnostics;

namespace MasterVks.Core
{
    /// <summary>
    /// Единые монотонные часы для видео и звука. Время Windows (DateTime) может
    /// прыгать при синхронизации, поэтому синхронизацию строим только по ним.
    /// </summary>
    internal static class Clock
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        public static readonly long Frequency = Stopwatch.Frequency;

        public static long Now
        {
            get { return Watch.ElapsedTicks; }
        }

        public static long FromSeconds(double seconds)
        {
            return (long)(seconds * Frequency);
        }

        public static double ToSeconds(long ticks)
        {
            return ticks / (double)Frequency;
        }
    }
}
