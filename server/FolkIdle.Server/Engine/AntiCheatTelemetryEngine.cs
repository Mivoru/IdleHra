using System;
using System.Collections.Concurrent;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public sealed class AntiCheatTelemetryEngine
    {
        private const int RingSize = 100;

        // Modul: anti-cheat false positive. This detector permanently
        // quarantined legitimate players, and a quarantine is unappealable:
        // ProcessSingleTick returns early on Quarantine_Active so the account
        // stops progressing entirely, the socket is force-closed on every
        // login, and nothing anywhere in the codebase ever sets the flag back
        // to false. Three separate defects produced that:
        //
        // 1. It compared ABSOLUTE variance of inter-command intervals, measured
        //    in seconds, against a fixed 0.002 s^2 bound. A player clicking
        //    quickly through the UI - opening four screens, equipping three
        //    items - sends commands milliseconds apart, so their intervals are
        //    all near zero and their absolute variance is near zero too. Twenty
        //    fast clicks were indistinguishable from a macro and earned an
        //    instant permanent ban. Absolute dispersion cannot separate "very
        //    regular" from "very fast"; relative dispersion can, so the test is
        //    now the coefficient of variation (standard deviation over mean).
        //
        // 2. PingNetworkDiagnostics was not excluded. The client sends it on a
        //    timer, so it is machine-generated and perfectly regular BY DESIGN
        //    - the exact signature this detector looks for. Any player idle
        //    long enough for pings to dominate the ring was flagged for running
        //    the game as shipped.
        //
        // 3. Timing profiles were never discarded, so the ring spanned logins.
        //    The gap across a night offline sat in the sample set as one
        //    enormous interval, and the twenty commands after a relogin mixed
        //    with the twenty before it.
        // Modul: challenge response policy. The integrity challenge asks the
        // client to prove it can compute ComputeChallengeHash. That is a test of
        // KNOWLEDGE, not of speed - a cheat client either has the algorithm or
        // it does not, and it answers just as fast either way.
        //
        // The window was 500ms of wall clock, and a single miss quarantined the
        // account outright. That is not a cheat detector, it is a latency
        // detector: a mobile client on a 300ms round trip that hits one GC pause
        // or a backgrounded frame misses it through no fault of its own, and a
        // quarantine is irreversible without an operator running
        // --lift-quarantine. Automated Play Mode harnesses, whose frames only
        // advance when the driver pumps them, missed it every single time.
        //
        // Two changes, the same pair the macro detector needed: give the answer
        // a window that reflects real-world latency rather than LAN latency, and
        // require a RUN of misses before escalating. A client that genuinely
        // cannot answer misses every challenge and still trips the limit within
        // a minute; a client that is merely slow now survives.
        public const long ChallengeResponseWindowMs = 15000L;
        public const int ConsecutiveChallengeMissLimit = 4;

        private const double MacroCoefficientOfVariationThreshold = 0.05;
        private const int MinimumSampleCount = 20;

        // A macro is defined by SUSTAINED regularity. Twenty commands inside a
        // few seconds is a human in a hurry; twenty commands evenly spaced over
        // a minute is a script. Without a minimum span, the burst case is
        // indistinguishable from the script case no matter which dispersion
        // statistic is used.
        private const double MinimumObservationWindowSeconds = 60.0;

        // Below this mean interval the sample is a burst, not a cadence.
        private const double MinimumMeanIntervalSeconds = 0.35;

        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer _redis;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly FolkIdle.Server.Network.NetworkBroadcastSystem? _networkSystem;
        private readonly ConcurrentDictionary<long, CommandTimingProfile> _profiles = new();
        private readonly ConcurrentDictionary<long, byte> _shadowBanRequests = new();

        public AntiCheatTelemetryEngine(IServiceProvider serviceProvider, IConnectionMultiplexer redis, PlayerSessionRegistry playerRegistry, FolkIdle.Server.Network.NetworkBroadcastSystem? networkSystem = null)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
            _playerRegistry = playerRegistry;
            _networkSystem = networkSystem;
        }

        public void RecordCommand(long playerId, byte commandType)
        {
            // Modul: anti-cheat false positive. Both excluded commands are
            // generated by the client itself rather than by a player action:
            // 31 (AntiCheatChallengeResponse) answers this engine's own
            // challenge, and 52 (PingNetworkDiagnostics) is sent on a timer.
            // Feeding timer-driven traffic into a regularity detector means
            // detecting the client's own heartbeat as automation.
            if (playerId <= 0
                || commandType == (byte)Network.CommandType.AntiCheatChallengeResponse
                || commandType == (byte)Network.CommandType.PingNetworkDiagnostics)
            {
                return;
            }

            long now = Environment.TickCount64;
            var profile = _profiles.GetOrAdd(playerId, _ => new CommandTimingProfile());
            if (profile.RecordAndCheck(now))
            {
                RequestShadowBan(playerId, 54, 1);
            }
        }

        public void RequestShadowBan(long playerId, int reasonCode, int detailCode)
        {
            if (playerId <= 0 || !_shadowBanRequests.TryAdd(playerId, 1))
            {
                return;
            }

            TelemetryStreamer.TryWrite(new TelemetryEvent
            {
                PlayerId = playerId,
                EventType = 3,
                Value1 = reasonCode,
                Value2 = detailCode,
                Timestamp = Environment.TickCount64
            });

            // Modul: ON THE SERVER'S CONSOLE, not only in a telemetry stream
            // nobody persists.
            //
            // A live quarantine had to be diagnosed by reading two boolean
            // columns and guessing which of four detectors set them, because
            // the reason code went to TelemetryStreamer and TelemetryStreamer
            // does not write to the database. An irreversible penalty whose
            // cause cannot be reconstructed is one nobody can defend or appeal.
            Console.WriteLine($"QUARANTINE applied to player {playerId} (reason {reasonCode}, detail {detailCode}).");

            _playerRegistry.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = playerId });

            _ = Task.Run(async () =>
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (player != null)
                    {
                        player.IsQuarantined = true;
                        player.Quarantine_Active = true;

                        // Modul: WRITE DOWN WHY, 2026-09-01. The reason used to
                        // exist only in a TelemetryStreamer event that is never
                        // persisted and a console line that dies with the
                        // container - so a live quarantine could be seen but
                        // never explained, and the account that reported this
                        // had been restricted since before even the console
                        // line existed. An automatic, effectively total
                        // restriction that cannot be accounted for is one
                        // nobody can defend or appeal.
                        db.AccountPenalties.Add(new AccountPenalty
                        {
                            PlayerId = playerId,
                            Source = PenaltySource.AntiCheat,
                            ReasonCode = reasonCode,
                            DetailCode = detailCode,
                            AppliedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            AppliedBy = null,
                            Note = $"detector reason {reasonCode}, detail {detailCode}"
                        });

                        await db.SaveChangesAsync();
                    }

                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"MarketOrderRecords\" SET \"IsQuarantined\" = TRUE WHERE \"SellerId\" = {0} AND \"Status\" = 0",
                        playerId);

                    await transaction.CommitAsync();

                    if (_redis.IsConnected)
                    {
                        var redisDb = _redis.GetDatabase();
                        await redisDb.HashSetAsync(RedisSessionCache.SessionStateKey(playerId), new HashEntry[]
                        {
                            new("is_quarantined", 1),
                            new("shadow_reason", reasonCode),
                            new("shadow_detail", detailCode)
                        });
                        await redisDb.SetAddAsync(RedisSessionCache.DirtyPlayersSetKey, playerId);
                    }

                    // Modul 25: sever the bot's active socket immediately on
                    // confirmed automation, rather than leaving it connected
                    // until the next gated command freezes it.
                    _networkSystem?.ForceDisconnect(playerId);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"Shadow quarantine failed for player {playerId}: {ex.Message}");
                    _shadowBanRequests.TryRemove(playerId, out _);
                }
            });
        }

        // Modul: anti-cheat false positive. Timing profiles used to live for the
        // process lifetime, so a player's ring buffer spanned every session they
        // had ever played on this server instance: the multi-hour gap across an
        // offline night sat in the sample set as a single enormous interval, and
        // commands from before a relogin were averaged with commands after it.
        // Neither describes a cadence. Called when a session ends.
        public void ForgetPlayer(long playerId)
        {
            _profiles.TryRemove(playerId, out _);
        }

        public static uint GenerateChallengeSeed(long playerId, long logicEpochCounter, long tickCounter)
        {
            uint seed = unchecked((uint)playerId);
            seed ^= unchecked((uint)(playerId >> 32));
            seed ^= unchecked((uint)logicEpochCounter * 0x9E3779B9u);
            seed ^= unchecked((uint)tickCounter * 0x85EBCA6Bu);
            return XorShift32(seed == 0u ? 0xA341316Cu : seed);
        }

        public static uint ComputeChallengeHash(uint challengeSeed, long playerId, long logicEpochCounter)
        {
            uint value = challengeSeed;
            value ^= unchecked((uint)playerId);
            value = XorShift32(value);
            value ^= unchecked((uint)(playerId >> 32));
            value = XorShift32(value + unchecked((uint)logicEpochCounter));
            value ^= 0xC2B2AE35u;
            return XorShift32(value);
        }

        private static uint XorShift32(uint value)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value == 0u ? 0x6D2B79F5u : value;
        }

        private sealed class CommandTimingProfile
        {
            private readonly long[] _timestamps = new long[RingSize];
            private int _cursor;
            private int _count;

            public bool RecordAndCheck(long timestampMs)
            {
                lock (_timestamps)
                {
                    _timestamps[_cursor] = timestampMs;
                    _cursor = (_cursor + 1) % RingSize;
                    if (_count < RingSize) _count++;
                    if (_count < MinimumSampleCount) return false;

                    double sum = 0.0;
                    double sumSquares = 0.0;
                    int intervalCount = _count - 1;
                    int start = (_cursor - _count + RingSize) % RingSize;
                    long previous = _timestamps[start];

                    for (int i = 1; i < _count; i++)
                    {
                        int index = (start + i) % RingSize;
                        long current = _timestamps[index];
                        double intervalSeconds = (current - previous) / 1000.0;
                        previous = current;
                        sum += intervalSeconds;
                        sumSquares += intervalSeconds * intervalSeconds;
                    }

                    double mean = sum / intervalCount;

                    // sum is the total elapsed span across the whole sample, so
                    // these two guards reject the burst case that absolute
                    // variance could never distinguish from a macro.
                    if (sum < MinimumObservationWindowSeconds) return false;
                    if (mean < MinimumMeanIntervalSeconds) return false;

                    double variance = (sumSquares / intervalCount) - (mean * mean);
                    if (variance < 0.0) variance = 0.0;

                    // Coefficient of variation: dispersion RELATIVE to the
                    // cadence. A script firing every 2s has a CV near zero
                    // whether or not its absolute variance is small, and a human
                    // clicking every 2s on average does not - which is exactly
                    // the distinction the old absolute-variance test could not
                    // make.
                    double coefficientOfVariation = Math.Sqrt(variance) / mean;
                    return coefficientOfVariation < MacroCoefficientOfVariationThreshold;
                }
            }
        }
    }
}
