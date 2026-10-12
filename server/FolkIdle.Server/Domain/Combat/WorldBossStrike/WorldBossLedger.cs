using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>What one recorded strike changed beyond the player's own row.</summary>
    public sealed record StrikeBook(bool DefeatedNow, IReadOnlyList<WorldBossMilestone> MilestonesPaid);

    /// <summary>What the week's payout did, for the log and for the chat after the commit.</summary>
    public sealed record WeekPayout(int Mailed, bool Defeated, IReadOnlyList<string> Taunts);

    /// <summary>
    /// The bookkeeping of Chernobog's week: registration, the reveal, the kill,
    /// the milestones and the Sunday payout. Every method runs on the CALLER's
    /// context, inside the caller's transaction, with the WorldBossSnapshots row
    /// already locked - so a strike, its milestones and the kill are one commit.
    /// Spec: docs/superpowers/specs/2026-10-13-chernobog-world-boss-design.md.
    /// </summary>
    public static class WorldBossLedger
    {
        /// <summary>
        /// Books one strike worth <paramref name="credit"/> (already floored and
        /// clamped) for the player whose locked row is <paramref name="attempt"/>.
        /// <paramref name="skill"/> is the shield-wheel score of a played strike,
        /// null for an auto-strike or opcode 32.
        /// </summary>
        public static async Task<StrikeBook> RecordStrikeAsync(
            FolkIdleDbContext db, WorldBossSnapshot snapshot, PlayerWorldBossAttempt attempt,
            long credit, double? skill, int playerLevel, DateTime utcNow)
        {
            long now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
            bool defeatedNow = false;

            attempt.TotalInflictedDamage += credit;

            if (snapshot.HpRevealed == 0)
            {
                // Registration. Only an account at the ranked level registers,
                // so exercise.mjs throwaways and fresh alts cannot inflate the
                // health everyone else has to take down.
                if (playerLevel >= LeaderboardTierRegistry.MinimumRankedLevel)
                {
                    attempt.IsRegistered = true;
                    attempt.RegistrationDamage += credit;
                    attempt.RegistrationStrikes++;
                    snapshot.TotalDamageContributed += credit;
                }
                if (skill is double s)
                {
                    attempt.RegistrationSkillSum += s;
                    attempt.RegistrationPlayed++;
                }
            }
            else
            {
                if (skill is double s)
                {
                    attempt.LaterSkillSum += s;
                    attempt.LaterPlayed++;
                }

                // Modul: A LATECOMER'S DAMAGE IS THEIR OWN (owner, 2026-10-13).
                // Someone who first strikes on Wednesday did not raise the health,
                // so they do not lower it either: their damage counts for their
                // milestones and their rank, never for the kill.
                if (attempt.IsRegistered && snapshot.CurrentHp > 0)
                {
                    long dealt = Math.Min(credit, snapshot.CurrentHp);
                    snapshot.CurrentHp -= dealt;
                    snapshot.TotalDamageContributed += dealt;
                    if (snapshot.CurrentHp == 0 && snapshot.DefeatedAtEpoch == 0)
                    {
                        snapshot.DefeatedAtEpoch = now;
                        defeatedNow = true;
                    }
                }
            }

            var paid = await PayMilestonesAsync(db, attempt, utcNow);
            return new StrikeBook(defeatedNow, paid);
        }

        /// <summary>
        /// Pays every milestone the player's weekly total has reached and this
        /// week has not paid yet - including damage dealt before a deploy that
        /// introduced them. Mailed at once (owner: "a milestone pays when it is
        /// crossed"), cosmetics granted directly and only once per account.
        /// </summary>
        public static async Task<List<WorldBossMilestone>> PayMilestonesAsync(FolkIdleDbContext db, PlayerWorldBossAttempt attempt, DateTime utcNow)
        {
            var reached = WorldBossRewards.NewlyReached(attempt.TotalInflictedDamage, attempt.MilestonesPaid).ToList();
            if (reached.Count == 0) return reached;

            long hourly = await GoldRewardScale.HourlyIncomeAsync(db, attempt.PlayerId, utcNow);
            long nowEpoch = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();

            foreach (var milestone in reached)
            {
                attempt.MilestonesPaid |= 1 << milestone.Index;

                var extras = new List<string>();
                if (milestone.AvatarId != null && await GrantAvatarAsync(db, attempt.PlayerId, milestone.AvatarId, utcNow))
                {
                    extras.Add($"the {WorldBossRewards.FindAvatar(milestone.AvatarId)?.Name} avatar");
                }
                if (milestone.TitleSlug != null && await TitleEngine.GrantAsync(db, attempt.PlayerId, milestone.TitleSlug, utcNow))
                {
                    extras.Add($"the title \"{TitleRegistry.DisplayNameFor(milestone.TitleSlug)}\"");
                }
                if (milestone.PetId != null && await PetEngine.GrantAsync(db, attempt.PlayerId, milestone.PetId, utcNow))
                {
                    extras.Add($"the {PetRegistry.Find(milestone.PetId)?.Name}");
                }

                long gold = GoldRewardScale.ForMinutes(hourly, milestone.GoldMinutes);
                if (gold == 0 && milestone.Diamonds == 0 && extras.Count == 0) continue;

                string text = $"You have dealt {milestone.Damage:N0} damage to me this week.";
                if (extras.Count > 0) text += " Svarog's forge sends you " + string.Join(" and ", extras) + ".";
                db.MailboxInstances.Add(new MailboxInstance
                {
                    PlayerId = attempt.PlayerId,
                    BaseItemId = string.Empty,
                    Quantity = 0,
                    GoldAttachment = gold,
                    DiamondAttachment = milestone.Diamonds,
                    SenderName = WorldBossRewards.SenderName,
                    MessageText = text,
                    ReceivedTimestamp = nowEpoch,
                });
            }
            return reached;
        }

        /// <summary>Gives a bound world boss avatar unless the account has it. True if it was new.</summary>
        public static async Task<bool> GrantAvatarAsync(FolkIdleDbContext db, long playerId, string avatarId, DateTime utcNow)
        {
            int inserted = await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO cosmetic_items (\"PlayerId\", \"Kind\", \"DefinitionId\", \"Rarity\", \"Source\", \"AcquiredAtUtc\", \"IsListed\") " +
                "SELECT {0}, {1}, {2}, {3}, {4}, {5}, false " +
                "WHERE NOT EXISTS (SELECT 1 FROM cosmetic_items WHERE \"PlayerId\" = {0} AND \"DefinitionId\" = {2})",
                playerId, (byte)CosmeticKind.Avatar, avatarId, (byte)CosmeticRegistry.Legendary,
                (byte)CosmeticSource.WorldBoss, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            return inserted > 0;
        }

        /// <summary>
        /// Sets the hidden health once registration is over. False when it is
        /// not due or already done. See WorldBossHpRules for why it is shaped so.
        /// </summary>
        public static async Task<bool> RevealIfDueAsync(FolkIdleDbContext db, WorldBossSnapshot snapshot, long nowEpoch)
        {
            if (snapshot.EventState != 1 || snapshot.HpRevealed == 1) return false;
            if (snapshot.RevealEpoch <= 0 || nowEpoch < snapshot.RevealEpoch) return false;

            var registered = await db.PlayerWorldBossAttempts.AsNoTracking()
                .Where(a => a.BossInstanceId == snapshot.BossInstanceId && a.IsRegistered)
                .Select(a => new { a.RegistrationDamage, a.RegistrationStrikes })
                .ToListAsync();

            double factor = WorldBossHpRules.DrawFactor();
            long total = WorldBossHpRules.TotalHp(registered.Select(r => new Registration(r.RegistrationDamage, r.RegistrationStrikes)), factor);
            long alreadyDealt = registered.Sum(r => r.RegistrationDamage);

            snapshot.HpRevealed = 1;
            snapshot.HpFactor = factor;
            snapshot.MaxHp = total;
            snapshot.CurrentHp = Math.Max(0, total - alreadyDealt);
            if (total > 0 && snapshot.CurrentHp == 0) snapshot.DefeatedAtEpoch = nowEpoch;
            Console.WriteLine($"World boss revealed: {registered.Count} registered, N={factor:F3}, MaxHp={total}, CurrentHp={snapshot.CurrentHp}.");
            return true;
        }

        /// <summary>
        /// An encounter opened before Chernobog's week existed (RevealEpoch 0):
        /// gives it a reveal time and registers whoever had already struck, so
        /// the deploy week still has a boss that can fall.
        /// </summary>
        public static async Task<bool> UpgradeLegacyEncounterAsync(FolkIdleDbContext db, WorldBossSnapshot snapshot)
        {
            if (snapshot.EventState != 1 || snapshot.RevealEpoch > 0) return false;

            snapshot.RevealEpoch = snapshot.EventEndEpoch + 1 - 5 * 86_400L;
            long firstRegistrationDay = WorldBossCalendar.DayKey(snapshot.RevealEpoch) - 2;
            var attempts = await db.PlayerWorldBossAttempts
                .Where(a => a.BossInstanceId == snapshot.BossInstanceId && a.TotalInflictedDamage > 0)
                .ToListAsync();
            foreach (var a in attempts)
            {
                // The running total cannot say how many days it took; a row last
                // used after the first day struck on (at most) two.
                a.IsRegistered = true;
                a.RegistrationDamage = a.TotalInflictedDamage;
                a.RegistrationStrikes = a.AttemptDateKey > firstRegistrationDay ? 2 : 1;
            }
            snapshot.MaxHp = 0;
            snapshot.CurrentHp = 0;
            snapshot.HpRevealed = 0;
            snapshot.TotalDamageContributed = attempts.Sum(a => a.TotalInflictedDamage);
            Console.WriteLine($"World boss: legacy encounter upgraded, {attempts.Count} registered, reveal at {snapshot.RevealEpoch}.");
            return true;
        }

        /// <summary>
        /// Sunday's payout, inside the transaction that concludes the encounter:
        /// last week's top three titles move, the top three are paid by rank, and
        /// if the boss fell every registered player gets the kill reward - a
        /// sandbagger a quarter of the gold and no chest.
        /// </summary>
        public static async Task<WeekPayout> PayWeekAsync(FolkIdleDbContext db, WorldBossSnapshot snapshot, DateTime utcNow, Random random)
        {
            long nowEpoch = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
            int mailed = 0;
            var taunts = new List<string>();

            var attempts = await db.PlayerWorldBossAttempts.AsNoTracking()
                .Where(a => a.BossInstanceId == snapshot.BossInstanceId)
                .ToListAsync();
            var ranked = await WorldBossBoard.RankedAsync(db);
            var ids = attempts.Select(a => a.PlayerId).ToList();
            var income = await GoldRewardScale.HourlyIncomeAsync(db, ids, utcNow);
            var names = await db.PlayerRecords.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Username })
                .ToDictionaryAsync(p => p.Id, p => p.Username);

            void Mail(long playerId, long gold, string text)
            {
                db.MailboxInstances.Add(new MailboxInstance
                {
                    PlayerId = playerId,
                    BaseItemId = string.Empty,
                    Quantity = 0,
                    GoldAttachment = gold,
                    SenderName = WorldBossRewards.SenderName,
                    MessageText = text,
                    ReceivedTimestamp = nowEpoch,
                });
                mailed++;
            }

            // 1. The top three. Last week's holders lose their titles first, so
            // each title is worn by exactly one player at a time.
            var slugs = WorldBossRewards.PlaceTitleSlugs.ToArray();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM player_titles WHERE \"TitleSlug\" IN ({0}, {1}, {2})", slugs[0], slugs[1], slugs[2]);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PlayerRecords\" SET \"ActiveTitleSlug\" = NULL WHERE \"ActiveTitleSlug\" IN ({0}, {1}, {2})", slugs[0], slugs[1], slugs[2]);
            for (int place = 0; place < Math.Min(slugs.Length, ranked.Count); place++)
            {
                long playerId = ranked[place].PlayerId;
                await TitleEngine.GrantAsync(db, playerId, slugs[place], utcNow);
                // Worn at once unless the player already chose a title.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"PlayerRecords\" SET \"ActiveTitleSlug\" = {0} WHERE \"Id\" = {1} AND \"ActiveTitleSlug\" IS NULL", slugs[place], playerId);
                long gold = GoldRewardScale.ForMinutes(income.GetValueOrDefault(playerId), WorldBossRewards.PlaceGoldHours[place] * 60);
                Mail(playerId, gold,
                    $"Place {place + 1} this week, with {ranked[place].Damage:N0} damage. Wear \"{TitleRegistry.DisplayNameFor(slugs[place])}\" until next Sunday - if you can keep it.");
            }

            // 2. The kill reward, to the registered.
            bool defeated = snapshot.DefeatedAtEpoch > 0;
            if (defeated)
            {
                var registeredIds = attempts.Where(a => a.IsRegistered).Select(a => a.PlayerId).ToList();
                var proven = (await db.WorldBossProfiles.AsNoTracking()
                        .Where(p => registeredIds.Contains(p.PlayerId) && p.ProvenSkillAtUtc != null)
                        .Select(p => p.PlayerId)
                        .ToListAsync())
                    .ToHashSet();

                foreach (var a in attempts.Where(a => a.IsRegistered))
                {
                    bool flagged = WorldBossHpRules.IsSandbagging(
                        a.RegistrationSkillSum, a.RegistrationPlayed, a.LaterSkillSum, a.LaterPlayed, proven.Contains(a.PlayerId));
                    long gold = GoldRewardScale.ForMinutes(income.GetValueOrDefault(a.PlayerId), WorldBossRewards.KillGoldHours * 60);
                    if (flagged)
                    {
                        gold = gold * WorldBossRewards.SandbaggerKillSharePct / 100;
                        Mail(a.PlayerId, gold,
                            "You held back while the others registered, and the forge noticed. A quarter of the reward, and no chest.");
                        db.AccountPenalties.Add(new AccountPenalty
                        {
                            PlayerId = a.PlayerId,
                            Source = PenaltySource.WorldBossSandbag,
                            ReasonCode = 1,
                            DetailCode = 0,
                            AppliedAtEpochMs = nowEpoch * 1000L,
                            Note = $"world boss sandbagging: registration s={a.RegistrationSkillSum / Math.Max(1, a.RegistrationPlayed):F2} over {a.RegistrationPlayed}, later s={a.LaterSkillSum / Math.Max(1, a.LaterPlayed):F2} over {a.LaterPlayed}; kill reward cut to {WorldBossRewards.SandbaggerKillSharePct}%",
                        });
                        taunts.Add(WorldBossRewards.Taunt(names.GetValueOrDefault(a.PlayerId) ?? $"Player {a.PlayerId}", random.Next()));
                    }
                    else
                    {
                        Mail(a.PlayerId, gold, "You drove me back into Svarog's chest. Take the gold, and the chest the forge left for you. I will be back on Monday.");
                        await CosmeticEngine.InsertChestsAsync(db, a.PlayerId, new[] { CosmeticRegistry.Legendary }, CosmeticSource.WorldBoss, utcNow);
                    }
                }
            }

            // 3. Remember who showed the skill, for the weeks after this one.
            var known = (await db.WorldBossProfiles.AsNoTracking().Where(p => ids.Contains(p.PlayerId)).Select(p => p.PlayerId).ToListAsync()).ToHashSet();
            foreach (var a in attempts)
            {
                if (known.Contains(a.PlayerId)) continue;
                if (!WorldBossHpRules.ShowsSkill(a.RegistrationSkillSum + a.LaterSkillSum, a.RegistrationPlayed + a.LaterPlayed)) continue;
                db.WorldBossProfiles.Add(new WorldBossProfile { PlayerId = a.PlayerId, ProvenSkillAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc) });
            }

            await db.SaveChangesAsync();
            return new WeekPayout(mailed, defeated, taunts);
        }
    }
}
