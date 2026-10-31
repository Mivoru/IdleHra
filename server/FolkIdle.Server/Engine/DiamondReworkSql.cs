namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The SQL of the DiamondRework migration (owner M1/I1/A2, 2026-10-31),
    /// kept here so DiamondReworkTests can run it against a real database
    /// and compare it with the C# rules it encodes.
    /// </summary>
    /// <remarks>
    /// Modul: NOT ADDITIVE - it rewrites live rows, so a database backup is
    /// taken before the deploy that carries it (ops/oracle/backup-db.sh).
    /// Nothing is taken from anybody: a stored lifetime tier moves to the
    /// index its threshold has on the longer ladder (AchievementMilestones
    /// .MigratedTier), so no tier is paid twice and none is lost; an
    /// inheritance stat becomes ceil(old / 2) of the new +4% levels, which is
    /// never less bonus than before (old 11 = 22% becomes 6 = 24%).
    /// </remarks>
    public static class DiamondReworkSql
    {
        public const string RemapTreasury = @"
UPDATE player_lifetime_achievements
SET ""CompletedTier"" = CASE ""CompletedTier"" WHEN 1 THEN 1 WHEN 2 THEN 3 WHEN 3 THEN 5 WHEN 4 THEN 7 ELSE ""CompletedTier"" END
WHERE ""AchievementId"" = 2 AND ""CompletedTier"" BETWEEN 1 AND 4;";

        public const string RemapForging = @"
UPDATE player_lifetime_achievements
SET ""CompletedTier"" = CASE ""CompletedTier"" WHEN 1 THEN 1 WHEN 2 THEN 3 WHEN 3 THEN 4 WHEN 4 THEN 6 ELSE ""CompletedTier"" END
WHERE ""AchievementId"" = 3 AND ""CompletedTier"" BETWEEN 1 AND 4;";

        public const string HalveInheritance = @"
UPDATE player_inheritance_stats
SET ""Level"" = LEAST(10, (""Level"" + 1) / 2)
WHERE ""Level"" > 0;";
    }
}
