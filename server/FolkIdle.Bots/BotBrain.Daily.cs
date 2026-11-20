using System.Text.Json;

namespace FolkIdle.Bots
{
    /// <summary>
    /// The daily layer added after the bot was last taught (2026-11-15 to
    /// 11-19): the six daily missions, the Chronicle pass they feed, and the
    /// Descent into Nav.
    /// </summary>
    /// <remarks>
    /// Modul: WHY THE BOT HAS TO PLAY THESE (2026-11-20). The bots are the
    /// game's only progression data (three real players). Missions pay
    /// Chronicle XP and the pass pays chests, diamonds and tickets; Nav pays
    /// Souls, diamonds and dungeon tickets per depth. A bot that ignores all
    /// three under-reports every currency they pay, and the balance numbers
    /// drawn from it describe a game nobody plays any more.
    ///
    /// Modul: THE BOT DOES NOT CHASE MISSIONS. It claims whatever its normal
    /// play finished - which is what a player who opens the bubble once in a
    /// while gets. Swapping a mission it would never do (a feast, an
    /// expedition) is left out on purpose: it would make the bot look better
    /// at missions than the screens make a player.
    /// </remarks>
    public sealed partial class BotBrain
    {
        private async Task DailyChoresAsync(CancellationToken token)
        {
            if (Due("missions", 90)) await MissionsAsync(token);
            if (Due("nav", 20)) await NavAsync(token);
        }

        // ------------------------------------------------------------------
        // Daily missions and the Chronicle pass
        // ------------------------------------------------------------------

        private async Task MissionsAsync(CancellationToken token)
        {
            // One call claims every finished mission of the day and the all-done
            // bonus (MissionEngine.ClaimAsync).
            var (_, claimed) = await _client.PostAsync("/api/v1/missions/claim", new { }, token);
            if (claimed is { } c && c.Str("Outcome") == "Ok")
            {
                int xp = c.Int("XpGained");
                Event($"missions: claimed {xp} Chronicle XP");
                Count("mission_claims");
            }

            // No Level: every reached, unclaimed pass level at once.
            var (_, pass) = await _client.PostAsync("/api/v1/chronicle/claim", new { }, token);
            if (pass is { } p && p.Str("Outcome") == "Ok")
            {
                Event("chronicle: pass rewards claimed");
                Count("chronicle_claims");
                _inventoryDirty = true;
            }
        }

        // ------------------------------------------------------------------
        // The Descent into Nav
        // ------------------------------------------------------------------

        /// <summary>
        /// Every charge, from the character the server expects to go deepest in
        /// the highest Circle (its own NavSimulator estimate, shown on the
        /// screen as "expected depth"); then the Souls into the cheapest open
        /// Workshop upgrade, as many as they buy.
        /// </summary>
        /// <remarks>
        /// Modul: CHARGES REFILL EVERY 20 MINUTES and cap, so the timer is 20
        /// game minutes: a player who glances at Nav that often loses none.
        /// The gold research is left alone - gold is what the bot's gear and
        /// village are short of, and a research line pays only inside Nav.
        /// </remarks>
        private async Task NavAsync(CancellationToken token)
        {
            var view = await _client.GetAsync("/api/v1/nav", token);
            if (view is not { } v || !v.Bool("Open")) return;

            int circle = Math.Max(1, v.Int("Circle", 1));
            var best = v.Arr("Roster").OrderByDescending(r => r.Int("ExpectedDepth")).FirstOrDefault();
            int slot = best.ValueKind == JsonValueKind.Object ? best.Int("Slot") : 0;

            for (int charges = v.Int("Charges"), guard = 0; charges > 0 && guard < 12 && !token.IsCancellationRequested; charges--, guard++)
            {
                var (_, ran) = await _client.PostAsync("/api/v1/nav/descend", new { Slot = slot, Circle = circle }, token);
                if (ran is not { } r) break;
                string result = r.Str("Result");
                if (result != "Ran")
                {
                    if (result != "NoCharges") Event($"nav: descent refused ({result})");
                    break;
                }
                var run = r.Obj("Run");
                Event($"nav: circle {circle} slot {slot} cleared to depth {run.Int("DeepestCleared")} for {run.Long("Souls")} souls ({run.Str("EndedBy")})");
                Count("nav_descents");
                _inventoryDirty = true;
            }

            await SpendSoulsAsync(token);
        }

        private async Task SpendSoulsAsync(CancellationToken token)
        {
            for (int guard = 0; guard < 8 && !token.IsCancellationRequested; guard++)
            {
                var view = await _client.GetAsync("/api/v1/nav", token);
                if (view is not { } v) return;
                long souls = v.Long("Souls");
                var cheapest = v.Arr("Workshop")
                    .Where(u => !u.Bool("Maxed") && !u.Bool("Locked") && u.Long("Cost") > 0 && u.Long("Cost") <= souls)
                    .OrderBy(u => u.Long("Cost"))
                    .FirstOrDefault();
                if (cheapest.ValueKind != JsonValueKind.Object) return;
                string key = cheapest.Str("Key");
                var (_, bought) = await _client.PostAsync("/api/v1/nav/workshop", new { Key = key, Levels = 1 }, token);
                if (bought is not { } b || b.Str("Result") != "Bought") return;
                Event($"nav: workshop {key} to level {b.Int("Level")} ({cheapest.Long("Cost")} souls)");
                Count("nav_workshop_levels");
            }
        }
    }
}
