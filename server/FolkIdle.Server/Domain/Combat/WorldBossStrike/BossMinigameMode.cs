using System;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// FOLKIDLE_BOSS_MINIGAME: off (default) | practice | wheel (spec 5.1) | duel (2026-11-14).
    /// Read once at start-up. Anything unrecognised is Off.
    /// </summary>
    public enum BossMinigameMode
    {
        Off = 0,
        Practice = 1,
        Wheel = 2,
        /// <summary>The duel that replaced the wheel (2026-11-14): strikes over REST like Wheel, played as BossDuelService.</summary>
        Duel = 3,
    }

    public sealed class BossMinigameSettings
    {
        public BossMinigameMode Mode { get; init; }

        public static BossMinigameSettings FromEnvironment(string? flag) => new()
        {
            Mode = (flag ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "practice" => BossMinigameMode.Practice,
                "wheel" => BossMinigameMode.Wheel,
                "duel" => BossMinigameMode.Duel,
                _ => BossMinigameMode.Off,
            },
        };

        public string ModeName => Mode.ToString().ToLowerInvariant();
    }
}
