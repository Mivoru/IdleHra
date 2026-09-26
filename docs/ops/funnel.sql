-- The new-player funnel (task 39, plan item 4 in
-- docs/superpowers/plans/2026-09-26-audit-remediation.md).
--
-- How many players of the cohort reached each step. The cohort is every
-- account with a step-1 (registered) row: accounts from before the funnel was
-- deployed have none and are outside it by design - nothing can reconstruct
-- their registration time.
--
-- Run it on the box, on stdin (the backlog's note on piping SQL over SSH):
--   ssh <box> "cd ~/folkidle/ops/oracle && docker compose exec -T postgres psql -U folkidle -d folkidle" < docs/ops/funnel.sql
--
-- COALESCE on the username, deliberately: a guest (device) account has a NULL
-- Username, and `NULL NOT LIKE 'exercise%'` is NULL, not true - without it,
-- every guest registration would silently drop out of the cohort.
--
-- Steps (FunnelStep in server/FolkIdle.Server/Engine/FunnelRecorder.cs):
--   1 registered      5 onboarding_done   9 level_20
--   2 first_kill      6 region1_boss     10 joined_guild
--   3 first_equip     7 level_5          11 returned_d1
--   4 first_craft     8 level_10         12 returned_d7
--
-- D2 (the first-kill rule, fixed before the data): reopen the first-kill
-- decision only if the cohort holds >= 30 registrations AND step 1 -> 2 is the
-- LARGEST drop below AND that drop is >= 25%.

WITH cohort AS (
    SELECT f."PlayerId"
    FROM player_funnel_events f
    JOIN "PlayerRecords" p ON p."Id" = f."PlayerId"
    WHERE f."Step" = 1
      AND COALESCE(p."Username", '') NOT LIKE 'exercise%'
      AND COALESCE(p."Username", '') <> 'dev'
),
counts AS (
    SELECT f."Step", count(*) AS players
    FROM player_funnel_events f
    WHERE f."PlayerId" IN (SELECT "PlayerId" FROM cohort)
    GROUP BY f."Step"
)
SELECT c."Step",
       CASE c."Step"
           WHEN 1 THEN 'registered'     WHEN 2 THEN 'first_kill'
           WHEN 3 THEN 'first_equip'    WHEN 4 THEN 'first_craft'
           WHEN 5 THEN 'onboarding_done' WHEN 6 THEN 'region1_boss'
           WHEN 7 THEN 'level_5'        WHEN 8 THEN 'level_10'
           WHEN 9 THEN 'level_20'       WHEN 10 THEN 'joined_guild'
           WHEN 11 THEN 'returned_d1'   WHEN 12 THEN 'returned_d7'
       END AS step_name,
       c.players,
       round(100.0 * c.players / NULLIF((SELECT count(*) FROM cohort), 0), 1) AS pct_of_cohort
FROM counts c
ORDER BY c."Step";
