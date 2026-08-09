using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Awards the Seal for a completed chapter, and pays what a Seal is worth.
    ///
    /// **EACH SEAL GRANTS +2 PERMANENT SKILL POINTS, EVERY SEASON, FOREVER.**
    /// That is the load-bearing decision of the whole Book of Deeds, because it
    /// couples two systems: the skill tree gains a SECOND SOURCE of points,
    /// earned by exploring the game rather than by levelling it. Five Seals is
    /// +10 against a base of ~100 - felt, and nowhere near decisive.
    ///
    /// AWARDED SERVER-SIDE, on a read. There is no "claim" command and there
    /// deliberately is not one: a claim button is a thing to forget, and a
    /// client that decided when it had earned a Seal would be a client that
    /// could award itself the tree. The Progress screen asking "how am I doing"
    /// is the same question as "have I finished a chapter", so the answer is
    /// computed and banked in the same pass.
    /// </summary>
    public static class SealEngine
    {
        /// <summary>
        /// Grants any Seal whose chapter is complete and not yet held, and pays
        /// the skill points for it immediately.
        ///
        /// Returns the mask of newly awarded chapters, so a caller can tell the
        /// player - a Seal earned in silence is a reward that did not happen.
        ///
        /// PAID ON AWARD AS WELL AS AT THE ROLLOVER, because the alternative is
        /// telling a player who just finished a chapter that their reward
        /// starts in six weeks.
        /// </summary>
        public static async Task<int> AwardCompletedChaptersAsync(
            FolkIdleDbContext db, PlayerRecord player, DeedContext context)
        {
            int newlyAwarded = 0;

            var chapters = DeedRegistry.Chapters;
            for (int i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                if (DeedRegistry.HasSeal(player.SealsEarnedMask, chapter.Index)) continue;
                if (!DeedRegistry.IsComplete(chapter, context)) continue;

                player.SealsEarnedMask = DeedRegistry.WithSeal(player.SealsEarnedMask, chapter.Index);
                player.AvailableSkillPoints += DeedRegistry.SkillPointsPerSeal;

                // Modul: and four hours of banked time - one of the three
                // things that fill the chrono bank at all. See ChronoGrantRules
                // for why it had none before.
                player.BankedChronoSeconds = ChronoGrantRules.AddCapped(
                    player.BankedChronoSeconds, ChronoGrantRules.SealSeconds);

                newlyAwarded |= 1 << (chapter.Index - 1);
            }

            if (newlyAwarded != 0)
            {
                await db.SaveChangesAsync();
            }

            return newlyAwarded;
        }

        /// <summary>
        /// The skill points every account should START a season with, on top of
        /// the one-per-level the season itself pays.
        ///
        /// Called by SeasonalRotationEngine, which zeroes AvailableSkillPoints
        /// with the tree - Seals are the one thing that must survive that,
        /// because "every season, forever" is the whole promise.
        /// </summary>
        public static int SeasonStartingPointsFor(int sealsMask) => DeedRegistry.SkillPointsFrom(sealsMask);
    }
}
