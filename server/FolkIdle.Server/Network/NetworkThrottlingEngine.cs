using System;

namespace FolkIdle.Server.Network
{
    public struct TokenBucket
    {
        public double AvailableTokens;
        public long LastRefillTimestampEpoch;
    }

    public static class NetworkThrottlingEngine
    {
        public const double Capacity = 20.0;
        public const double RefillRatePerSecond = 5.0;

        public static TokenBucket CreateBucket()
        {
            return new TokenBucket
            {
                AvailableTokens = Capacity,
                LastRefillTimestampEpoch = System.Diagnostics.Stopwatch.GetTimestamp()
            };
        }

        public static bool TryConsume(ref TokenBucket bucket)
        {
            long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (bucket.LastRefillTimestampEpoch <= 0L)
            {
                bucket.AvailableTokens = Capacity;
                bucket.LastRefillTimestampEpoch = currentTicks;
            }

            long elapsedTicks = currentTicks - bucket.LastRefillTimestampEpoch;
            if (elapsedTicks > 0L)
            {
                bucket.AvailableTokens = Math.Min(Capacity, bucket.AvailableTokens + (double)elapsedTicks / System.Diagnostics.Stopwatch.Frequency * RefillRatePerSecond);
                bucket.LastRefillTimestampEpoch = currentTicks;
            }

            if (bucket.AvailableTokens < 1.0)
            {
                return false;
            }

            bucket.AvailableTokens -= 1.0;
            return true;
        }
    }
}
