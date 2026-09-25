using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>A spear the server has already answered, kept for idempotency and the finish's consistency rule.</summary>
    public sealed record RecordedThrow(int Seq, double TapMs, CounterEntry? Counter, SpearLanding Landing, bool WeakHit);

    /// <summary>One issued challenge: the schedule, when it was issued, and every throw answered so far.</summary>
    public sealed class WorldBossChallenge
    {
        public required string ChallengeId { get; init; }
        public required long PlayerId { get; init; }
        public required ShieldWheelSchedule Schedule { get; init; }
        public required long IssuedAtMs { get; init; }
        public bool Practice => Schedule.Practice;

        /// <summary>
        /// Practice only: a weak plate drawn for this challenge alone. Practice
        /// must never answer from the real one, or it is a free probe for the
        /// secret (spec 5.2).
        /// </summary>
        public int DecoyWeakPlate { get; init; }

        internal readonly object Gate = new();
        internal readonly Dictionary<int, RecordedThrow> Throws = new();

        public long ExpiresAtMs => IssuedAtMs + WorldBossStrikeRules.CountdownMs + WorldBossStrikeRules.MaxPlayMs + WorldBossChallengeRegistry.ExpiryGraceMs;

        public IReadOnlyList<RecordedThrow> ThrowsSnapshot()
        {
            lock (Gate)
            {
                var list = new List<RecordedThrow>(Throws.Values);
                list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                return list;
            }
        }
    }

    /// <summary>
    /// Outstanding challenges, in memory, one practice and one real per player.
    /// </summary>
    /// <remarks>
    /// Modul: IN MEMORY ON PURPOSE, AND A RESTART FORGETS THEM (spec 5.6). A
    /// real challenge spends its attempt only when it resolves, so a restart
    /// hands the player a fresh challenge - a re-roll the player cannot
    /// trigger. Expiry is lazy (every request sweeps what is due), so there is
    /// no StartCron and CronWorkerGuardTests is untouched.
    /// </remarks>
    public sealed class WorldBossChallengeRegistry
    {
        /// <summary>A challenge lives this long past the end of its play time.</summary>
        public const long ExpiryGraceMs = 60_000;

        private readonly ConcurrentDictionary<long, WorldBossChallenge> _practice = new();
        private readonly ConcurrentDictionary<long, WorldBossChallenge> _real = new();
        private readonly RandomNumberGenerator _rng;

        public WorldBossChallengeRegistry(RandomNumberGenerator? rng = null)
        {
            _rng = rng ?? RandomNumberGenerator.Create();
        }

        private ConcurrentDictionary<long, WorldBossChallenge> Map(bool practice) => practice ? _practice : _real;

        public WorldBossChallenge? Get(long playerId, bool practice) =>
            Map(practice).TryGetValue(playerId, out var challenge) ? challenge : null;

        /// <summary>Issues a challenge, or returns the one already outstanding (issued = false).</summary>
        public (WorldBossChallenge Challenge, bool Issued) IssueOrGet(long playerId, bool practice, bool enraged, long nowMs)
        {
            var map = Map(practice);
            if (map.TryGetValue(playerId, out var existing) && existing.ExpiresAtMs > nowMs) return (existing, false);

            WorldBossChallenge created;
            lock (_rng)
            {
                Span<byte> id = stackalloc byte[16];
                _rng.GetBytes(id);
                created = new WorldBossChallenge
                {
                    ChallengeId = Convert.ToHexString(id).ToLowerInvariant(),
                    PlayerId = playerId,
                    Schedule = ShieldWheelSchedule.Generate(_rng, practice, enraged),
                    IssuedAtMs = nowMs,
                    DecoyWeakPlate = practice ? RandomNumberGenerator.GetInt32(WorldBossStrikeRules.PlateCount) : -1,
                };
            }

            var stored = map.AddOrUpdate(playerId, created, (_, old) => old.ExpiresAtMs > nowMs ? old : created);
            return (stored, ReferenceEquals(stored, created));
        }

        /// <summary>Removes and returns the challenge if its id matches.</summary>
        public WorldBossChallenge? TryTake(long playerId, bool practice, string challengeId)
        {
            var map = Map(practice);
            if (!map.TryGetValue(playerId, out var challenge)) return null;
            if (!string.Equals(challenge.ChallengeId, challengeId, StringComparison.Ordinal)) return null;
            return map.TryRemove(new KeyValuePair<long, WorldBossChallenge>(playerId, challenge)) ? challenge : null;
        }

        /// <summary>
        /// Records one throw, idempotently by Seq: a repeated Seq returns the
        /// answer already given, whatever the repeat carried.
        /// </summary>
        public RecordedThrow RecordThrow(WorldBossChallenge challenge, RecordedThrow answer)
        {
            lock (challenge.Gate)
            {
                if (challenge.Throws.TryGetValue(answer.Seq, out var stored)) return stored;
                challenge.Throws[answer.Seq] = answer;
                return answer;
            }
        }

        public bool TryGetThrow(WorldBossChallenge challenge, int seq, out RecordedThrow? recorded)
        {
            lock (challenge.Gate)
            {
                bool found = challenge.Throws.TryGetValue(seq, out var value);
                recorded = value;
                return found;
            }
        }

        /// <summary>
        /// Drops expired practice challenges silently. Real challenges are
        /// returned for resolution at the floor (spec 5.6) - that lands with
        /// Phase 2; until then there are none.
        /// </summary>
        public IReadOnlyList<WorldBossChallenge> ExpireDue(long nowMs)
        {
            foreach (var pair in _practice)
            {
                if (pair.Value.ExpiresAtMs <= nowMs) _practice.TryRemove(pair);
            }

            var due = new List<WorldBossChallenge>();
            foreach (var pair in _real)
            {
                if (pair.Value.ExpiresAtMs <= nowMs && _real.TryRemove(pair)) due.Add(pair.Value);
            }
            return due;
        }

        public int PracticeCount => _practice.Count;
    }
}
