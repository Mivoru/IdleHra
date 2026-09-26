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
        /// This attempt's weak plate, drawn at issue and held here and nowhere
        /// else (spec 3.3.1). A real challenge draws it from the plates unbroken
        /// at issue; practice draws a decoy from all five, so practice can never
        /// answer from anything real and is no probe for the secret (spec 5.2).
        /// </summary>
        public int WeakPlate { get; init; }

        /// <summary>The encounter a real challenge belongs to; 0 in practice.</summary>
        public long EncounterEndEpoch { get; init; }

        internal readonly object Gate = new();
        internal readonly Dictionary<int, RecordedThrow> Throws = new();
        internal readonly Dictionary<int, ParryEntry> Parries = new();

        /// <summary>Every parry a throw has reported so far, by interrupt, the first answer kept.</summary>
        public IReadOnlyList<ParryEntry> ParriesSnapshot()
        {
            lock (Gate)
            {
                var list = new List<ParryEntry>(Parries.Values);
                list.Sort((a, b) => a.Interrupt.CompareTo(b.Interrupt));
                return list;
            }
        }

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

        /// <summary>
        /// Issues a challenge, or returns the one already outstanding (issued =
        /// false). <paramref name="brokenPlateMask"/> is the board at issue: a
        /// real challenge's weak plate is drawn only from the plates it leaves
        /// standing. Practice ignores it.
        /// </summary>
        public (WorldBossChallenge Challenge, bool Issued) IssueOrGet(long playerId, bool practice, bool enraged, long nowMs,
            int brokenPlateMask = 0, long encounterEndEpoch = 0)
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
                    WeakPlate = WeakPlateDraw.From(practice ? 0 : brokenPlateMask),
                    EncounterEndEpoch = practice ? 0 : encounterEndEpoch,
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

        /// <summary>
        /// Keeps the parries a throw reported, so a reopened screen can resume
        /// the same run - a parry, once made, is never replaced by a later one.
        /// </summary>
        public void RecordParries(WorldBossChallenge challenge, IEnumerable<ParryEntry> parries)
        {
            lock (challenge.Gate)
            {
                foreach (var parry in parries) challenge.Parries.TryAdd(parry.Interrupt, parry);
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
        /// Drops expired practice challenges silently. Expired real challenges
        /// are removed and returned, for the caller to resolve at the floor
        /// (spec 5.6) - whoever's request happened to sweep them.
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

        // Modul: THE ANSWER TO A STRIKE NOBODY FINISHED. An abandoned challenge
        // is resolved by whichever request sweeps it, which is usually not the
        // owner's, so the owner's next look at the screen collects the result
        // here ("your unfinished strike was resolved at the base multiplier").
        private readonly ConcurrentDictionary<long, WorldBossStrikeOutcome> _resolvedNotes = new();

        public void NoteResolved(long playerId, WorldBossStrikeOutcome outcome) => _resolvedNotes[playerId] = outcome;

        public WorldBossStrikeOutcome? TakeResolvedNote(long playerId) =>
            _resolvedNotes.TryRemove(playerId, out var outcome) ? outcome : null;
    }
}
