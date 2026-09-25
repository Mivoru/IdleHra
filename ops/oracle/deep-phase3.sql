-- Task 37 Phase 3: what has the Deep done to the economy? Read-only.
--
--     ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose exec -T postgres psql -U folkidle -d folkidle" < ops/oracle/deep-phase3.sql
--
-- Run it a week after the flag went on (FOLKIDLE_DELVE_DEEP=on since
-- 2026-09-25, so on or after 2026-10-02) and record the answers in
-- docs/TASK_BOARD.md section 37. The day-0 baseline is recorded there too.
--
-- Modul: the Deep's gold is DelveDeepGoldSpent (lifetime, every toll and
-- lantern), added 2026-09-25 because nothing recorded it before. Tolls taken
-- before that column existed, on the flag's first day, are not in it.

\echo '== 1. Gold the Deep has taken, and from how many players'
select count(*) filter (where "DelveDeepGoldSpent" > 0)   as players_who_paid,
       sum("DelveDeepGoldSpent")                          as deep_gold_total,
       max("DelveDeepGoldSpent")                          as biggest_single_spender
from "PlayerRecords";

\echo '== 2. How deep: the record holders'
select "Id", "Username", "CurrentLevel", "DelveDeepestFloor", "DelveDeepestThisWeek", "DelveDeepGoldSpent"
from "PlayerRecords"
where "DelveDeepestFloor" > 8
order by "DelveDeepestFloor" desc, "DelveDeepGoldSpent" desc
limit 10;

\echo '== 3. Titles earned'
select "TitleSlug", count(*) as holders, min("EarnedAtUtc") as first_earned
from player_titles group by 1 order by 1;

\echo '== 4. Share of income: Deep spend against what the economy minted over the same window'
-- EcoTelemetryLedgers is hourly. Minted = balances + mailbox + consumed, so its
-- growth over the window is what players earned (net of the sinks it counts),
-- and TotalGoldConsumed now includes DelveDeepGoldSpent.
with win as (
  select min("Timestamp") as t0, max("Timestamp") as t1
  from "EcoTelemetryLedgers"
  where "Timestamp" >= extract(epoch from timestamp '2026-09-25 17:00:00')::bigint
),
ends as (
  select (select "TotalGoldMinted"   from "EcoTelemetryLedgers", win where "Timestamp" = win.t0 limit 1) as minted0,
         (select "TotalGoldMinted"   from "EcoTelemetryLedgers", win where "Timestamp" = win.t1 limit 1) as minted1,
         (select "TotalGoldConsumed" from "EcoTelemetryLedgers", win where "Timestamp" = win.t0 limit 1) as consumed0,
         (select "TotalGoldConsumed" from "EcoTelemetryLedgers", win where "Timestamp" = win.t1 limit 1) as consumed1,
         (select round((t1 - t0) / 3600.0, 1) from win)                                                  as hours
)
select hours,
       minted1 - minted0                                  as gold_minted_in_window,
       consumed1 - consumed0                              as gold_consumed_in_window,
       round(100.0 * (consumed1 - consumed0) / nullif(minted1 - minted0, 0), 1) as consumed_pct_of_minted
from ends;

\echo '== 5. Deep runs open right now'
select count(*) as open_deep_runs, max("CurrentFloor") as deepest_open_floor, max("LanternsBought") as most_lanterns
from "DelveRunRecords" where "IsDeep";
