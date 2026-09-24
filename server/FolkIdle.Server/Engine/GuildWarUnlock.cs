using System;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>What the gate saw the last time it counted.</summary>
    public sealed record GuildWarUnlockStatus(bool Unlocked, int QualifyingPlayers, int QualifyingGuilds)
    {
        public static readonly GuildWarUnlockStatus Locked = new(false, 0, 0);
    }

    /// <summary>
    /// THE gate in front of every Guild War path: pairing, settlement, payouts
    /// and opcodes 23/27/49/50.
    ///
    /// Modul: WHY A POPULATION FLOOR, 2026-09-23/24 (owner decisions, final).
    ///
    /// The live war code paid 100 diamonds to every member of a winning guild -
    /// more than the Delve's whole weekly ceiling - and the pairing pass
    /// resolved every running match whenever two guilds were unmatched, so two
    /// level-10 alts creating and disbanding guilds could force a payout every
    /// few minutes. Nobody had done it only because production had one guild.
    /// A war also means nothing with one guild. So wars are LOCKED until the
    /// game can hold them:
    ///
    /// - <see cref="RequiredQualifyingPlayers"/> players at or above the
    ///   leaderboard's level bar (QualifyingPopulation - level is the whole
    ///   definition, no activity window, no quarantine filter), AND
    /// - <see cref="RequiredGuilds"/> guilds with at least
    ///   <see cref="RequiredMembersPerGuild"/> qualifying members each, because
    ///   a hundred players in one guild still cannot hold a war.
    ///
    /// ONE-WAY. Once both are met the crossing is written to
    /// <c>feature_unlocks</c> and the gate stays open for ever - a population
    /// that dips under the floor for a holiday week must not switch off a
    /// running season.
    ///
    /// Locked means the commands ANSWER (CommandResultCode.GuildWarsLocked,
    /// which the client renders with the live M/50 from
    /// /api/v1/guild/war-unlock) rather than acting, and never disconnect - a
    /// stale screen is not an attack. The crons keep running and ask this gate
    /// every pass, so the unlock happens on its own when the floor is crossed,
    /// with no deploy.
    ///
    /// The tick thread may not touch the database, so the opcode handlers read
    /// <see cref="Current"/>, which GuildWarEngine's matchmaking loop refreshes
    /// every minute. The worst a stale cache can do is refuse a command for up
    /// to a minute after the unlock - never the reverse, because nothing
    /// re-locks.
    /// </summary>
    public sealed class GuildWarUnlock
    {
        public const string FeatureKey = "guild_wars";

        /// <summary>The owner's floor. One constant, deliberately.</summary>
        public const int RequiredQualifyingPlayers = 50;

        public const int RequiredGuilds = 4;

        public const int RequiredMembersPerGuild = 3;

        private volatile GuildWarUnlockStatus _current = GuildWarUnlockStatus.Locked;

        /// <summary>The last evaluation. Locked until the first one completes.</summary>
        public GuildWarUnlockStatus Current => _current;

        public bool IsUnlocked => _current.Unlocked;

        public static bool MeetsFloor(int qualifyingPlayers, int qualifyingGuilds)
            => qualifyingPlayers >= RequiredQualifyingPlayers && qualifyingGuilds >= RequiredGuilds;

        /// <summary>
        /// The whole rule. A recorded unlock wins over any count - that is what
        /// one-way means.
        /// </summary>
        public static bool IsUnlockedBy(bool alreadyRecorded, int qualifyingPlayers, int qualifyingGuilds)
            => alreadyRecorded || MeetsFloor(qualifyingPlayers, qualifyingGuilds);

        /// <summary>
        /// Counts, decides, and records the crossing the first time it happens.
        /// Safe to call from several places at once: the insert is
        /// ON CONFLICT DO NOTHING, and a lost race still answers "unlocked".
        /// </summary>
        public static async Task<GuildWarUnlockStatus> EvaluateAsync(FolkIdleDbContext db, CancellationToken ct = default)
        {
            int players = await QualifyingPopulation.CountPlayersAsync(db, ct);
            int guilds = await QualifyingPopulation.CountGuildsAsync(db, RequiredMembersPerGuild, ct);
            bool recorded = await db.FeatureUnlocks.AsNoTracking().AnyAsync(f => f.FeatureKey == FeatureKey, ct);

            bool unlocked = IsUnlockedBy(recorded, players, guilds);
            if (unlocked && !recorded)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO feature_unlocks (""FeatureKey"", ""UnlockedAtEpochMs"", ""QualifyingPlayersAtUnlock"", ""QualifyingGuildsAtUnlock"")
                       VALUES ({FeatureKey}, {nowMs}, {players}, {guilds})
                       ON CONFLICT (""FeatureKey"") DO NOTHING", ct);
                Console.WriteLine($"GuildWarUnlock: Guild Wars UNLOCKED at {players} qualifying players and {guilds} qualifying guilds.");
            }

            return new GuildWarUnlockStatus(unlocked, players, guilds);
        }

        /// <summary>Evaluates against a fresh scope and publishes the result to <see cref="Current"/>.</summary>
        public async Task<GuildWarUnlockStatus> RefreshAsync(IServiceProvider serviceProvider, CancellationToken ct = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var status = await EvaluateAsync(db, ct);
            Publish(status);
            return status;
        }

        /// <summary>
        /// Stores an evaluation for the tick thread. Never moves an unlocked
        /// cache back to locked - the database row is one-way, and so is this.
        /// </summary>
        public void Publish(GuildWarUnlockStatus status)
        {
            if (_current.Unlocked && !status.Unlocked)
            {
                status = status with { Unlocked = true };
            }
            _current = status;
        }
    }
}
