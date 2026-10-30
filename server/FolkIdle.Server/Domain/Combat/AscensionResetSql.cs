namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The Ascension reset's SQL (A1 + A2, 2026-10-30), in one place so the
    /// migration that runs it and the test that proves it run the same text.
    /// See Migrations/20261030204105_AscensionV2Reset.cs for the why.
    /// </summary>
    public static class AscensionResetSql
    {
        /// <summary>One loot chest per cleared step, at the new ladder's band odds.</summary>
        public const string PayChests = @"
INSERT INTO ""LootChests"" (""PlayerId"", ""Tier"", ""Region"", ""Source"", ""GrantedAtUtc"")
SELECT p.""PlayerId"",
       CASE
         WHEN s.step = 10 THEN CASE WHEN r.roll < 75 THEN 3 ELSE 4 END
         WHEN s.step >= 7 THEN CASE WHEN r.roll < 40 THEN 1 WHEN r.roll < 70 THEN 2 WHEN r.roll < 95 THEN 3 ELSE 4 END
         WHEN s.step >= 4 THEN CASE WHEN r.roll < 45 THEN 1 WHEN r.roll < 80 THEN 2 WHEN r.roll < 95 THEN 3 ELSE 4 END
         ELSE CASE WHEN r.roll < 50 THEN 1 WHEN r.roll < 80 THEN 2 WHEN r.roll < 95 THEN 3 ELSE 4 END
       END,
       p.""Region"",
       3,
       now() AT TIME ZONE 'UTC'
FROM boss_ascension_progress p
CROSS JOIN LATERAL generate_series(1, p.""HighestStep"") AS s(step)
CROSS JOIN LATERAL (SELECT floor(random() * 100)::int AS roll, s.step AS _keep) AS r
WHERE p.""HighestStep"" > 0;
";

        /// <summary>One message per affected player.</summary>
        public const string Notify = @"
INSERT INTO ""MailboxInstances"" (""PlayerId"", ""BaseItemId"", ""QualityTier"", ""Quantity"", ""IsClaimed"", ""IsPending"", ""GoldAttachment"", ""DiamondAttachment"", ""SenderName"", ""MessageText"", ""ReceivedTimestamp"")
SELECT ""PlayerId"", '', 0, 0, false, false, 0, 0,
       'The Ascension',
       'Ascension has been rebuilt with new mechanics: bosses enrage from step 4 and ward themselves from step 7, and clearing step 10 now gives +2% damage against that region''s monsters for good. Your titles and frames stay. Every step you had cleared was paid out as a loot chest - ' || SUM(""HighestStep"") || ' in all, waiting on the Chest screen - and your ladders start again from step 1.',
       EXTRACT(EPOCH FROM now())::bigint
FROM boss_ascension_progress
WHERE ""HighestStep"" > 0
GROUP BY ""PlayerId"";
";

        /// <summary>The counters to zero; titles and frames are not touched.</summary>
        public const string ZeroSteps = @"UPDATE boss_ascension_progress SET ""HighestStep"" = 0 WHERE ""HighestStep"" > 0;";
    }
}
