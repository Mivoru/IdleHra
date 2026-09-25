using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>What a Delve request did. Every one of these is shown to the player.</summary>
    public enum DelveResult
    {
        Ok = 0,
        /// <summary>A run is already in progress. Starting a second would strand a paid fee.</summary>
        RunAlreadyInProgress,
        NotEnoughGold,
        NoRunInProgress,
        /// <summary>A door index outside 0..DoorsPerFloor-1. A menu choice, so refused rather than treated as tampering.</summary>
        InvalidDoor,
        /// <summary>The last lantern charge went out. The run is over and nothing was banked.</summary>
        RunLost,
        /// <summary>The floor was cleared and the run continues.</summary>
        FloorCleared,
        /// <summary>The check failed but a charge remains - the same floor is re-offered.</summary>
        ChargeLost,
        /// <summary>Every floor is behind you. The only thing left to do is walk out (or, with the Deep open, descend).</summary>
        AtTheBottom,
        PlayerNotFound,

        // --- The Deep (task 37). Appended, so no existing value moves. ---

        /// <summary>
        /// The stake the server would freeze is higher than the one the player
        /// was shown - held gold rose between the view and the press. Nothing
        /// changed; the fresh view carries the new quote.
        /// </summary>
        PriceChanged,
        /// <summary>Every lantern a Deep run may buy has been bought.</summary>
        NoMoreLanterns,
        /// <summary>A descent was asked for with doors still in front of the run.</summary>
        NotAtTheBottom,
        /// <summary>FOLKIDLE_DELVE_DEEP is off. The Deep's routes answer this rather than a bare 404.</summary>
        DeepDisabled,
        /// <summary>A lantern was offered to a run that still has light. Buying is only for a run at 0 charges.</summary>
        ChargesRemain,
        /// <summary>A door was tried in the Deep with the lantern out. Light another, or walk out.</summary>
        LanternOut
    }

    /// <summary>
    /// The Deep's switch and clock. A settings object rather than a static so a
    /// test can run the engine with the Deep on and a fixed date beside tests
    /// that run it off, without the two racing over a global.
    /// </summary>
    public sealed class DelveDeepSettings
    {
        public bool Enabled { get; init; }

        public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

        /// <summary>FOLKIDLE_DELVE_DEEP: "on" opens the Deep; anything else, including unset, keeps it shut.</summary>
        public static DelveDeepSettings FromEnvironment(string? flag)
            => new DelveDeepSettings { Enabled = string.Equals(flag?.Trim(), "on", StringComparison.OrdinalIgnoreCase) };
    }

    /// <summary>What the screen needs to draw a run. Never accepted FROM the client.</summary>
    public sealed class DelveRunView
    {
        public bool Active { get; set; }
        public int CurrentFloor { get; set; }
        public int FloorsCleared { get; set; }
        public int ChargesRemaining { get; set; }
        public long EntryFeePaid { get; set; }

        /// <summary>Per door: the attribute it wants, or -1 where the door has not shown it.</summary>
        public int[] DoorDemands { get; set; } = Array.Empty<int>();

        /// <summary>Per door: the odds, 0-1, or -1 for a door whose demand is hidden.</summary>
        public double[] DoorOdds { get; set; } = Array.Empty<double>();

        /// <summary>Diamonds banking right now would pay, BEFORE the weekly ceiling.</summary>
        public int DiamondsIfBankedNow { get; set; }

        /// <summary>
        /// And what clearing one more floor would make it.
        ///
        /// Modul: SENT, not computed on the client. The payout curve is
        /// DelveRegistry's and a second copy of it in a Svelte file is exactly
        /// the drift this codebase's dominant bug class is made of. It is also
        /// half the game: pushing is only a decision if the player can see what
        /// pushing is worth.
        /// </summary>
        public int DiamondsIfNextFloorCleared { get; set; }

        /// <summary>And after it - what the player would actually receive.</summary>
        public int DiamondsAfterCeiling { get; set; }
        public long ConsolationGoldIfCapped { get; set; }

        public int DiamondsEarnedThisWeek { get; set; }
        public int WeeklyDiamondCeiling { get; set; }

        /// <summary>What a run would cost to start, for the screen that has none in progress.</summary>
        public long EntryFeeForNextRun { get; set; }
        public int HighestRegionReached { get; set; }
        public long CurrentGold { get; set; }

        public int[] Attributes { get; set; } = Array.Empty<int>();

        // --- The Deep (task 37). Every number is the server's; the client only
        // ever echoes StakeGold back as QuotedStake. ---

        /// <summary>FOLKIDLE_DELVE_DEEP is on. False = the screen never offers a descent.</summary>
        public bool DeepEnabled { get; set; }

        /// <summary>The run has descended past floor 8. Nothing it does from here pays diamonds.</summary>
        public bool IsDeep { get; set; }

        /// <summary>
        /// The run is at a landing: the bottom of floor 8, or a cleared Deep
        /// floor. The only acts left are walking out and (with the Deep open)
        /// descending.
        /// </summary>
        public bool AtLanding { get; set; }

        /// <summary>A descent is on offer right now - DeepEnabled and AtLanding.</summary>
        public bool CanDescend { get; set; }

        /// <summary>
        /// In the Deep, the stake frozen at the descent. At the bottom, the stake
        /// a descent WOULD freeze now - the number the client sends back as
        /// QuotedStake.
        /// </summary>
        public long StakeGold { get; set; }

        /// <summary>Gold the next descent tolls: toll(9) = the stake at the bottom, toll(d+1) on a Deep landing. 0 when none is on offer.</summary>
        public long DescendQuote { get; set; }

        /// <summary>The floor the next descent would enter.</summary>
        public int NextDeepFloor { get; set; }

        public int LanternsBought { get; set; }

        /// <summary>The deepest floor ever cleared, and this week's (0 when the stored week is stale).</summary>
        public int DeepestFloor { get; set; }
        public int DeepestThisWeek { get; set; }

        /// <summary>
        /// In the Deep with the lantern out and refills left: what the next
        /// lantern costs (stake x 2^bought). 0 otherwise. The client never sends
        /// a price - buying is an action, and the server charges this number.
        /// </summary>
        public long LanternPrice { get; set; }

        /// <summary>Lanterns this run may still buy (MaxLanternRefills - LanternsBought).</summary>
        public int LanternRefillsLeft { get; set; }

        /// <summary>The next Deep title past the player's record, or null when all are earned.</summary>
        public DelveNextTitle? NextTitle { get; set; }

        /// <summary>The display name of the title the player wears, or null. Rendered as sent; the client keeps no list.</summary>
        public string? ActiveTitle { get; set; }
    }

    public sealed class DelveNextTitle
    {
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Floor { get; set; }
    }

    public sealed class DelveActionOutcome
    {
        public DelveResult Result { get; set; }
        public DelveRunView View { get; set; } = new();

        /// <summary>Set only by a bank (walking out, or the floors-1-8 bank a descent makes). Both are what the player was actually paid.</summary>
        public int DiamondsGranted { get; set; }
        public long GoldReturned { get; set; }

        /// <summary>Set only by a Deep purchase (a toll or a lantern): exactly what was debited.</summary>
        public long GoldCharged { get; set; }
    }

    /// <summary>
    /// THE DELVE. A gold sink shaped like a game rather than a fee.
    ///
    /// Modul: EVERY OUTCOME IS ROLLED HERE. The client's whole vocabulary is
    /// "start", "I choose door N" and "I am walking out". It never sends a
    /// floor, a depth, an outcome or a reward, and there is no field on any of
    /// these requests that a tampered client could make profitable - which is
    /// the one design rule a minigame in this codebase cannot get wrong. Opcode
    /// 39 once granted diamonds from an unsigned client number; a game with a
    /// score is precisely the shape that invites that mistake again.
    ///
    /// Modul: OFF THE TICK, ON THE ESTABLISHED GOLD PATH. Gold is charged the
    /// way BreedingEngine charges it - a Serializable transaction with the
    /// CommodityRecords row locked FOR UPDATE - and the caller enqueues a
    /// ReloadState afterwards so the live payload picks the deduction up. That
    /// is deliberately NOT the tick-side CurrentGold/RedisPendingGoldDelta
    /// path: "two gold paths, and mixing them pays the player twice" is a rule
    /// this codebase learned the hard way, and a third path for a minigame
    /// would be how it learns it again.
    /// </summary>
    public sealed class DelveEngine
    {
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly DelveDeepSettings _deep;

        public DelveEngine(IDbContextFactory<FolkIdleDbContext> contextFactory, DelveDeepSettings? deep = null)
        {
            _contextFactory = contextFactory;
            _deep = deep ?? new DelveDeepSettings();
        }

        // Utc-kinded whatever the clock returned: Npgsql refuses a non-UTC DateTime for timestamptz.
        private DateTime Now() => DateTime.SpecifyKind(_deep.UtcNow(), DateTimeKind.Utc);

        /// <summary>
        /// ISO week and year, packed. Modul: the reset is a COMPARISON, not a
        /// cron job. A scheduled reset is one more background loop that can die
        /// silently and take a ceiling with it - and this codebase has lost a
        /// whole feature to exactly that. Deriving the week from the clock
        /// cannot fail to run.
        /// </summary>
        public static int CurrentWeekKey(DateTime utcNow)
        {
            return ISOWeek.GetYear(utcNow) * 100 + ISOWeek.GetWeekOfYear(utcNow);
        }

        /// <summary>
        /// Moves the player into this week if the stored key is stale.
        ///
        /// Modul: THE ONE PLACE THE WEEK TURNS OVER, for BOTH weekly columns.
        /// DelveDeepestThisWeek shares DelveWeekKey with DelveDiamondsThisWeek,
        /// and before the Deep only the bank path reset the week (and only the
        /// diamond counter). With two writers, whichever runs first in a new
        /// week must clear both - or a bank would zero a record set this week,
        /// or a record written first would carry last week's diamonds forward
        /// and shut the tap early. Both orders are tested.
        /// </summary>
        private static void RollWeek(PlayerRecord player, DateTime utcNow)
        {
            int weekKey = CurrentWeekKey(utcNow);
            if (player.DelveWeekKey == weekKey) return;

            player.DelveWeekKey = weekKey;
            player.DelveDiamondsThisWeek = 0;
            player.DelveDeepestThisWeek = 0;
            player.DelveDeepestThisWeekAtUtc = null;
        }

        /// <summary>Raises the all-time and weekly records to <paramref name="floorCleared"/> where it beats them.</summary>
        private static void RecordDepth(PlayerRecord player, int floorCleared, DateTime utcNow)
        {
            RollWeek(player, utcNow);
            if (floorCleared > player.DelveDeepestFloor) player.DelveDeepestFloor = floorCleared;
            if (floorCleared > player.DelveDeepestThisWeek)
            {
                player.DelveDeepestThisWeek = floorCleared;
                player.DelveDeepestThisWeekAtUtc = utcNow;
            }
        }

        private static int UnpackDoor(int packed, int index) => (packed >> (index * 8)) & 0xFF;

        private static int PackDoors(int[] demands)
        {
            int packed = 0;
            for (int i = 0; i < demands.Length && i < 4; i++) packed |= (demands[i] & 0xFF) << (i * 8);
            return packed;
        }

        private static int AttributeValue(PlayerRecord player, int attributeId) => attributeId switch
        {
            AttributeRegistry.Might => player.BaseStrength,
            AttributeRegistry.Finesse => player.BaseDexterity,
            AttributeRegistry.Vigour => player.BaseConstitution,
            _ => player.BaseLuck
        };

        /// <summary>The pass chance of a door on the run's current floor - the Deep's curve once the run is past floor 8.</summary>
        private static double DoorChance(DelveRunRecord run, int attributeValue)
            => run.IsDeep
                ? DelveRegistry.DeepSuccessChance(attributeValue, run.CurrentFloor)
                : DelveRegistry.SuccessChance(attributeValue, run.CurrentFloor);

        /// <summary>
        /// At a landing: the bottom of floor 8 before a descent, or a Deep floor
        /// just cleared. No doors are on offer at a landing.
        /// </summary>
        private static bool IsAtLanding(DelveRunRecord run)
            => run.IsDeep
                ? run.FloorsCleared >= run.CurrentFloor
                : run.FloorsCleared >= DelveRegistry.FloorCount;

        /// <summary>
        /// Rolls the three doors for a floor: what each wants, and which of them
        /// admits it. See DelveRegistry.DoorRevealChance for why Fortune is the
        /// stat that buys knowing.
        /// </summary>
        private static (int packed, int revealedMask) RollFloor(int fortune, Random rng)
        {
            var demands = new int[DelveRegistry.DoorsPerFloor];
            int mask = 0;
            double reveal = DelveRegistry.DoorRevealChance(fortune);

            for (int i = 0; i < demands.Length; i++)
            {
                demands[i] = rng.Next(AttributeRegistry.Count);
                if (rng.NextDouble() < reveal) mask |= 1 << i;
            }

            // Modul: at least one door always shows itself. A floor of three
            // blind doors is not a decision, it is a coin flip with extra
            // reading - and a player who met one would reasonably conclude the
            // screen was broken.
            if (mask == 0) mask = 1 << rng.Next(demands.Length);

            return (PackDoors(demands), mask);
        }

        /// <summary>
        /// How far the player has actually travelled, from their own codex.
        ///
        /// Modul: NOT PlayerRegionCompletions, which needs a THOUSAND kills of
        /// every monster in a region and so reads 0 for a player comfortably
        /// farming region 3. Pricing the entry fee off that would hand the
        /// richest players the cheapest runs, which is the exact inversion this
        /// feature exists to correct. A codex entry means "you have been there",
        /// which is the question being asked.
        /// </summary>
        private static async Task<int> HighestRegionReachedAsync(FolkIdleDbContext db, long playerId)
        {
            var monsterIds = await db.MonsterCodexEntries
                .Where(c => c.PlayerId == playerId && c.KillCount > 0)
                .Select(c => c.MonsterId)
                .ToListAsync();

            int highest = 1;
            foreach (int id in monsterIds)
            {
                int tier = ContentRegistry.GetMonsterRegionTier(id);
                if (tier > highest) highest = tier;
            }
            return Math.Clamp(highest, 1, 5);
        }

        private static async Task<long> ReadGoldAsync(FolkIdleDbContext db, long playerId)
        {
            var row = await db.CommodityRecords.AsNoTracking()
                .SingleOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            return row?.Quantity ?? 0L;
        }

        private static Task<CommodityRecord?> LockGoldRowAsync(FolkIdleDbContext db, long playerId)
            => db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                .FirstOrDefaultAsync();

        /// <summary>
        /// Fills the parts of the view that describe the PLAYER rather than the
        /// run - what a run would cost, what the ceiling has left, the sheet the
        /// doors will be resolved against, and the Deep's records.
        /// </summary>
        private void ApplyPlayerContext(DelveRunView view, PlayerRecord player, int highestRegion, long gold, DateTime utcNow)
        {
            bool thisWeek = player.DelveWeekKey == CurrentWeekKey(utcNow);
            view.HighestRegionReached = highestRegion;
            view.EntryFeeForNextRun = DelveRegistry.EntryFeeForRegion(highestRegion);
            view.CurrentGold = gold;
            view.WeeklyDiamondCeiling = DelveRegistry.MaxDiamondsPerWeek;
            view.DiamondsEarnedThisWeek = thisWeek ? player.DelveDiamondsThisWeek : 0;
            view.Attributes = new[] { player.BaseStrength, player.BaseDexterity, player.BaseConstitution, player.BaseLuck };
            view.DeepEnabled = _deep.Enabled;
            view.DeepestFloor = player.DelveDeepestFloor;
            view.DeepestThisWeek = thisWeek ? player.DelveDeepestThisWeek : 0;
            view.ActiveTitle = TitleRegistry.DisplayNameFor(player.ActiveTitleSlug);
            var next = TitleRegistry.NextDeepTitle(player.DelveDeepestFloor);
            view.NextTitle = next == null ? null : new DelveNextTitle { Slug = next.Slug, Name = next.DisplayName, Floor = next.DeepFloor };
        }

        private static void ApplyRunToView(DelveRunView view, DelveRunRecord run, PlayerRecord player)
        {
            view.Active = true;
            view.CurrentFloor = run.CurrentFloor;
            view.FloorsCleared = run.FloorsCleared;
            view.ChargesRemaining = run.ChargesRemaining;
            view.EntryFeePaid = run.EntryFeePaid;
            view.IsDeep = run.IsDeep;
            view.AtLanding = IsAtLanding(run);
            view.LanternsBought = run.LanternsBought;
            if (run.IsDeep)
            {
                view.LanternRefillsLeft = Math.Max(0, DelveRegistry.MaxLanternRefills - run.LanternsBought);
                if (run.ChargesRemaining <= 0 && view.LanternRefillsLeft > 0)
                {
                    view.LanternPrice = DelveRegistry.LanternRefillPrice(run.StakeGold, run.LanternsBought);
                }
            }
            if (run.IsDeep) view.StakeGold = run.StakeGold;

            var demands = new int[DelveRegistry.DoorsPerFloor];
            var odds = new double[DelveRegistry.DoorsPerFloor];
            for (int i = 0; i < DelveRegistry.DoorsPerFloor; i++)
            {
                bool revealed = (run.RevealedDoorMask & (1 << i)) != 0;
                int demand = UnpackDoor(run.PackedDoorDemands, i);
                demands[i] = revealed ? demand : -1;
                // Modul: the odds are published for the doors that show
                // themselves, because the bank-or-push decision is only a
                // decision if the arithmetic is visible. A hidden door reports
                // -1 rather than its real odds - showing them would reveal the
                // demand by inference and make Fortune worthless.
                odds[i] = revealed ? DoorChance(run, AttributeValue(player, demand)) : -1.0;
            }
            view.DoorDemands = demands;
            view.DoorOdds = odds;

            // Modul: THE DEEP PAYS NO DIAMONDS, and the view says so rather
            // than repeating floor 8's figure: floors 1-8 were banked at the
            // descent, and nothing past them is convertible.
            if (run.IsDeep) return;

            view.DiamondsIfBankedNow = DelveRegistry.DiamondsForBanking(run.FloorsCleared);
            view.DiamondsIfNextFloorCleared = DelveRegistry.DiamondsForBanking(
                Math.Min(run.FloorsCleared + 1, DelveRegistry.FloorCount));
        }

        private static void ApplyBankPreview(DelveRunView view, DelveRunRecord run)
        {
            if (run.IsDeep) return;
            int gross = DelveRegistry.DiamondsForBanking(run.FloorsCleared);
            int remaining = Math.Max(0, DelveRegistry.MaxDiamondsPerWeek - view.DiamondsEarnedThisWeek);
            int granted = Math.Min(gross, remaining);
            view.DiamondsAfterCeiling = granted;
            view.ConsolationGoldIfCapped = ConsolationFor(run.EntryFeePaid, run.FloorsCleared, gross, granted);
        }

        private static long ConsolationFor(long entryFee, int floorsCleared, int grossDiamonds, int grantedDiamonds)
        {
            if (grossDiamonds <= 0) return 0;
            double unpaid = (double)(grossDiamonds - grantedDiamonds) / grossDiamonds;
            if (unpaid <= 0) return 0;
            return (long)Math.Floor(DelveRegistry.ConsolationGold(entryFee, floorsCleared) * unpaid);
        }

        /// <summary>
        /// The stake a descent would freeze right now: the region fee, or
        /// StakeFraction of max(gold, the 7-day high-water mark).
        /// </summary>
        private async Task<long> CurrentStakeAsync(FolkIdleDbContext db, long playerId, int highestRegion, long gold)
        {
            long highWater = await GoldHighWater.SevenDayMaxAsync(db, playerId, GoldHighWater.Today(Now()));
            return DelveRegistry.Stake(DelveRegistry.EntryFeeForRegion(highestRegion), Math.Max(gold, highWater));
        }

        /// <summary>
        /// The descent quote on the view: at the bottom, the stake the server
        /// would freeze (priced on gold AFTER the floors-1-8 bank, as the
        /// descent itself is); on a Deep landing, the frozen stake's next toll.
        /// </summary>
        private async Task ApplyDescendQuoteAsync(FolkIdleDbContext db, DelveRunView view, DelveRunRecord run, long playerId, int highestRegion, long gold)
        {
            if (!IsAtLanding(run)) return;

            int nextFloor = run.IsDeep ? run.CurrentFloor + 1 : DelveRegistry.FirstDeepFloor;
            long stake = run.IsDeep
                ? run.StakeGold
                : await CurrentStakeAsync(db, playerId, highestRegion, gold + view.ConsolationGoldIfCapped);

            view.StakeGold = stake;
            view.NextDeepFloor = nextFloor;
            view.DescendQuote = DelveRegistry.TollForFloor(stake, nextFloor);
            view.CanDescend = _deep.Enabled;
        }

        /// <summary>Read-only. What the screen draws, whether or not a run is live.</summary>
        public async Task<DelveRunView> GetViewAsync(long playerId)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();

            var player = await db.PlayerRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId);
            var view = new DelveRunView();
            if (player == null) return view;

            int highestRegion = await HighestRegionReachedAsync(db, playerId);
            long gold = await ReadGoldAsync(db, playerId);
            ApplyPlayerContext(view, player, highestRegion, gold, Now());

            var run = await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId);
            if (run != null)
            {
                ApplyRunToView(view, run, player);
                ApplyBankPreview(view, run);
                await ApplyDescendQuoteAsync(db, view, run, playerId, highestRegion, gold);
            }

            return view;
        }

        public async Task<DelveActionOutcome> StartRunAsync(long playerId, Random? rng = null)
        {
            rng ??= Random.Shared;
            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var outcome = new DelveActionOutcome();
            try
            {
                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.PlayerNotFound;
                    return outcome;
                }

                int highestRegion = await HighestRegionReachedAsync(db, playerId);
                long fee = DelveRegistry.EntryFeeForRegion(highestRegion);

                var existing = await db.DelveRunRecords.SingleOrDefaultAsync(r => r.PlayerId == playerId);
                if (existing != null)
                {
                    // Modul: REFUSED, NOT REPLACED. The live run is holding a
                    // fee that has already been taken; overwriting it would
                    // destroy paid-for progress and look like the button simply
                    // resetting the screen.
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.RunAlreadyInProgress;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                var goldRow = await LockGoldRowAsync(db, playerId);

                if (goldRow == null || goldRow.Quantity < fee)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.NotEnoughGold;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                goldRow.Quantity -= fee;

                var (packed, mask) = RollFloor(player.BaseLuck, rng);
                var run = new DelveRunRecord
                {
                    PlayerId = playerId,
                    EntryFeePaid = fee,
                    CurrentFloor = 1,
                    FloorsCleared = 0,
                    ChargesRemaining = DelveRegistry.LanternCharges,
                    PackedDoorDemands = packed,
                    RevealedDoorMask = mask,
                    StartedAtEpoch = new DateTimeOffset(Now()).ToUnixTimeSeconds()
                };
                db.DelveRunRecords.Add(run);

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                outcome.Result = DelveResult.Ok;
                outcome.View = new DelveRunView();
                ApplyPlayerContext(outcome.View, player, highestRegion, goldRow.Quantity, Now());
                ApplyRunToView(outcome.View, run, player);
                ApplyBankPreview(outcome.View, run);
                return outcome;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task<DelveActionOutcome> ChooseDoorAsync(long playerId, int doorIndex, Random? rng = null)
        {
            rng ??= Random.Shared;
            var outcome = new DelveActionOutcome();

            if (doorIndex < 0 || doorIndex >= DelveRegistry.DoorsPerFloor)
            {
                // A door is a button on a screen. Refused and reported, never
                // treated as evidence of a tampered client.
                outcome.Result = DelveResult.InvalidDoor;
                outcome.View = await GetViewAsync(playerId);
                return outcome;
            }

            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var run = await db.DelveRunRecords
                    .FromSqlRaw("SELECT * FROM \"DelveRunRecords\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);

                if (run == null || player == null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = run == null ? DelveResult.NoRunInProgress : DelveResult.PlayerNotFound;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                if (run.IsDeep && run.ChargesRemaining <= 0)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.LanternOut;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                if (IsAtLanding(run))
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.AtTheBottom;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                int demand = UnpackDoor(run.PackedDoorDemands, doorIndex);
                double chance = DoorChance(run, AttributeValue(player, demand));
                bool passed = rng.NextDouble() < chance;

                if (passed && run.IsDeep)
                {
                    // Modul: A DEEP CLEAR STOPS AT A LANDING. Every floor past 8
                    // is entered by paying its toll, so a clear does not roll
                    // the next floor's doors - it records the depth, in this
                    // transaction, and offers "walk out" or "descend to d+1".
                    run.FloorsCleared = run.CurrentFloor;
                    run.RevealedDoorMask = 0;
                    RecordDepth(player, run.CurrentFloor, Now());

                    // Modul: THE TITLE IS GRANTED IN THE SAME TRANSACTION AS THE
                    // RECORD THAT EARNED IT. Every milestone at or above this
                    // floor is (re)granted - idempotent, so a replay or a
                    // title added to the registry later is caught up here.
                    foreach (var title in TitleRegistry.ForDeepFloor(player.DelveDeepestFloor))
                    {
                        await TitleEngine.GrantAsync(db, playerId, title.Slug, Now());
                    }
                    outcome.Result = DelveResult.FloorCleared;
                }
                else if (passed)
                {
                    run.FloorsCleared++;
                    if (run.FloorsCleared >= DelveRegistry.FloorCount)
                    {
                        // The bottom. The doors stop being offered and the only
                        // remaining acts are walking out with what was banked,
                        // or - with the Deep open - descending.
                        run.CurrentFloor = DelveRegistry.FloorCount;
                        run.RevealedDoorMask = 0;
                        outcome.Result = DelveResult.AtTheBottom;
                    }
                    else
                    {
                        run.CurrentFloor = run.FloorsCleared + 1;
                        var (packed, mask) = RollFloor(player.BaseLuck, rng);
                        run.PackedDoorDemands = packed;
                        run.RevealedDoorMask = mask;
                        outcome.Result = DelveResult.FloorCleared;
                    }
                }
                else
                {
                    run.ChargesRemaining--;

                    // Modul: IN THE DEEP THE LIGHT GOING OUT IS AN OFFER, not an
                    // end - while a refill is left, the run waits at 0 charges
                    // for the player to buy a lantern or walk out. Only a run
                    // that has bought every lantern it may is lost outright.
                    bool canRefill = run.IsDeep && run.LanternsBought < DelveRegistry.MaxLanternRefills;
                    if (run.ChargesRemaining <= 0 && canRefill)
                    {
                        run.ChargesRemaining = 0;
                        run.RevealedDoorMask = 0;
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();

                        outcome.Result = DelveResult.ChargeLost;
                        outcome.View = await GetViewAsync(playerId);
                        return outcome;
                    }

                    if (run.ChargesRemaining <= 0)
                    {
                        // In the Deep this loses only the gold already spent:
                        // floors 1-8 were banked at the descent, and the record
                        // stands.
                        db.DelveRunRecords.Remove(run);
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();

                        outcome.Result = DelveResult.RunLost;
                        outcome.View = await GetViewAsync(playerId);
                        return outcome;
                    }

                    // Modul: a survived failure RE-ROLLS the same floor rather
                    // than ending the attempt. The floor is still in front of
                    // you and the doors behind it are different ones - which
                    // keeps a bad draw recoverable and makes the charge a
                    // resource to spend rather than a strike against you.
                    var (packed, mask) = RollFloor(player.BaseLuck, rng);
                    run.PackedDoorDemands = packed;
                    run.RevealedDoorMask = mask;
                    outcome.Result = DelveResult.ChargeLost;
                }

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                outcome.View = await GetViewAsync(playerId);
                return outcome;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Pays out floors 1-8 of <paramref name="run"/> onto the locked player
        /// and gold rows: diamonds up to the weekly ceiling, consolation gold
        /// for whatever the ceiling refused. Changes tracked entities only; the
        /// caller saves and commits.
        ///
        /// Modul: THE ONE BANKING PATH. BankAsync (walking out) and DescendAsync
        /// (banking on the way down) both call this, so the Deep is
        /// diamond-neutral by construction: a descent pays exactly what walking
        /// out would have, and nothing below floor 8 is ever convertible. A
        /// copy of this in the descent would be one more place for the
        /// ceiling to drift.
        /// </summary>
        private async Task<(int Granted, long Consolation, CommodityRecord? GoldRow)> BankFloorsAsync(
            FolkIdleDbContext db, PlayerRecord player, DelveRunRecord run, long playerId)
        {
            RollWeek(player, Now());

            int gross = DelveRegistry.DiamondsForBanking(run.FloorsCleared);
            int remaining = Math.Max(0, DelveRegistry.MaxDiamondsPerWeek - player.DelveDiamondsThisWeek);
            int granted = Math.Min(gross, remaining);
            long consolation = ConsolationFor(run.EntryFeePaid, run.FloorsCleared, gross, granted);

            if (granted > 0)
            {
                player.PremiumDiamonds += granted;
                player.DelveDiamondsThisWeek += granted;
            }

            var goldRow = await LockGoldRowAsync(db, playerId);
            if (consolation > 0)
            {
                if (goldRow == null)
                {
                    goldRow = new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = consolation };
                    db.CommodityRecords.Add(goldRow);
                }
                else
                {
                    goldRow.Quantity += consolation;
                }
            }

            return (granted, consolation, goldRow);
        }

        private static Task<PlayerRecord?> LockPlayerAsync(FolkIdleDbContext db, long playerId)
            => db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                .FirstOrDefaultAsync();

        private static Task<DelveRunRecord?> LockRunAsync(FolkIdleDbContext db, long playerId)
            => db.DelveRunRecords
                .FromSqlRaw("SELECT * FROM \"DelveRunRecords\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                .FirstOrDefaultAsync();

        /// <summary>
        /// Walk out. Pays diamonds up to the weekly ceiling and gold for
        /// whatever the ceiling refused, then ends the run.
        ///
        /// Banking a run that has cleared nothing is how a player abandons one:
        /// it pays zero and takes the row away, which is honest and needs no
        /// second command.
        ///
        /// In the Deep, walking out ends the run and pays NOTHING: floors 1-8
        /// were banked at the descent, and the Deep's only rewards - the
        /// records - were written when each floor was cleared.
        /// </summary>
        public async Task<DelveActionOutcome> BankAsync(long playerId)
        {
            var outcome = new DelveActionOutcome();
            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var run = await LockRunAsync(db, playerId);

                if (run == null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.NoRunInProgress;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                var player = await LockPlayerAsync(db, playerId);

                if (player == null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.PlayerNotFound;
                    return outcome;
                }

                int granted = 0;
                long consolation = 0;
                if (!run.IsDeep)
                {
                    (granted, consolation, _) = await BankFloorsAsync(db, player, run, playerId);
                }

                int floorsCleared = run.FloorsCleared;
                db.DelveRunRecords.Remove(run);
                await db.SaveChangesAsync();
                await tx.CommitAsync();

                outcome.Result = DelveResult.Ok;
                outcome.DiamondsGranted = granted;
                outcome.GoldReturned = consolation;

                outcome.View = new DelveRunView();
                int highestRegion = await HighestRegionReachedAsync(db, playerId);
                ApplyPlayerContext(outcome.View, player, highestRegion, await ReadGoldAsync(db, playerId), Now());
                outcome.View.FloorsCleared = floorsCleared;
                return outcome;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Buys one lantern charge for a Deep run whose light is out, at
        /// stake x 2^bought, capped at MaxLanternRefills.
        ///
        /// Modul: THE CLIENT SENDS NO PRICE. The request is the action alone;
        /// the price is computed here from the FROZEN stake and the count on the
        /// run row, debited from the locked gold row in the same transaction as
        /// the charge it buys. Every refusal says why: no Deep run, light still
        /// burning, every lantern bought, not enough gold - and changes nothing.
        /// </summary>
        public async Task<DelveActionOutcome> BuyLanternAsync(long playerId)
        {
            var outcome = new DelveActionOutcome();
            if (!_deep.Enabled)
            {
                outcome.Result = DelveResult.DeepDisabled;
                outcome.View = await GetViewAsync(playerId);
                return outcome;
            }

            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var run = await LockRunAsync(db, playerId);

                DelveResult? refusal = run == null || !run.IsDeep ? DelveResult.NoRunInProgress
                    : run.ChargesRemaining > 0 ? DelveResult.ChargesRemain
                    : run.LanternsBought >= DelveRegistry.MaxLanternRefills ? DelveResult.NoMoreLanterns
                    : null;

                long price = run == null ? 0 : DelveRegistry.LanternRefillPrice(run.StakeGold, run.LanternsBought);
                CommodityRecord? goldRow = null;
                if (refusal == null)
                {
                    goldRow = await LockGoldRowAsync(db, playerId);
                    if (goldRow == null || goldRow.Quantity < price) refusal = DelveResult.NotEnoughGold;
                }

                if (refusal != null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = refusal.Value;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                goldRow!.Quantity -= price;
                run!.LanternsBought++;
                run.ChargesRemaining = 1;
                // A fresh light shows the floor afresh.
                var (packed, mask) = RollFloor(player.BaseLuck, Random.Shared);
                run.PackedDoorDemands = packed;
                run.RevealedDoorMask = mask;

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                outcome.Result = DelveResult.Ok;
                outcome.GoldCharged = price;
                outcome.View = await GetViewAsync(playerId);
                return outcome;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// DEV TOOLS ONLY (POST /api/v1/dev/delve/lantern-out): puts out the
        /// light of the caller's Deep run, so exercise.mjs can buy a lantern on
        /// every run instead of failing doors until chance obliges. Returns false
        /// when there is no Deep run to darken.
        /// </summary>
        public async Task<bool> DevPutOutTheLanternAsync(long playerId)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            var run = await db.DelveRunRecords.SingleOrDefaultAsync(r => r.PlayerId == playerId);
            if (run == null || !run.IsDeep || IsAtLanding(run)) return false;
            run.ChargesRemaining = 0;
            run.RevealedDoorMask = 0;
            await db.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// DEV TOOLS ONLY (POST /api/v1/dev/delve/at-bottom, behind
        /// NetworkBroadcastSystem.DevToolsEnabled): replaces the player's run
        /// with one standing at the bottom of floor 8, as if every floor had
        /// been cleared. Charges no fee - it is the state a paid run reaches,
        /// not a way to buy one - and the entry fee on the row is the region's,
        /// so the floors-1-8 bank a descent makes pays what a real clear would.
        /// </summary>
        public async Task<DelveRunView> DevPlaceRunAtBottomAsync(long playerId)
        {
            await using (var db = await _contextFactory.CreateDbContextAsync())
            {
                var existing = await db.DelveRunRecords.SingleOrDefaultAsync(r => r.PlayerId == playerId);
                if (existing != null) db.DelveRunRecords.Remove(existing);

                int highestRegion = await HighestRegionReachedAsync(db, playerId);
                db.DelveRunRecords.Add(new DelveRunRecord
                {
                    PlayerId = playerId,
                    EntryFeePaid = DelveRegistry.EntryFeeForRegion(highestRegion),
                    CurrentFloor = DelveRegistry.FloorCount,
                    FloorsCleared = DelveRegistry.FloorCount,
                    ChargesRemaining = DelveRegistry.LanternCharges,
                    StartedAtEpoch = new DateTimeOffset(Now()).ToUnixTimeSeconds()
                });
                await db.SaveChangesAsync();
            }

            return await GetViewAsync(playerId);
        }

        /// <summary>
        /// Descend into the Deep, or one floor deeper in it.
        ///
        /// From the bottom of floor 8: bank floors 1-8 (BankFloorsAsync, the
        /// walk-out path), sample the high-water mark from the locked gold row,
        /// freeze the stake, debit toll(9) and roll floor 9's doors - one
        /// Serializable transaction, so a refusal at any step changes nothing at
        /// all, the bank included. From a cleared Deep floor d: debit toll(d+1)
        /// on the frozen stake and roll its doors.
        ///
        /// Modul: THE SERVER NEVER CHARGES THE CLIENT'S NUMBER. QuotedStake is
        /// the stake the player was SHOWN; it is only a ceiling. Held gold, and
        /// so the stake, rises with income between the view and the press, and
        /// charging more than was shown would be a price the player never
        /// agreed to - so a higher server stake answers PriceChanged with a
        /// fresh quote instead. A quote above the server's number is fine: the
        /// server's own, lower stake is what is charged.
        ///
        /// Modul: A DB DEBIT, NEVER RedisPendingGoldDelta. The checkpoint applies
        /// the pending delta as an INCREMENT to CommodityRecords, so a debit
        /// written there would be un-debited one checkpoint later. The caller
        /// enqueues ReloadState so the live payload sees the new balance.
        /// </summary>
        public async Task<DelveActionOutcome> DescendAsync(long playerId, long quotedStake, Random? rng = null)
        {
            rng ??= Random.Shared;
            var outcome = new DelveActionOutcome();

            if (!_deep.Enabled)
            {
                outcome.Result = DelveResult.DeepDisabled;
                outcome.View = await GetViewAsync(playerId);
                return outcome;
            }

            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var run = await LockRunAsync(db, playerId);
                var player = run == null ? null : await LockPlayerAsync(db, playerId);

                DelveResult? refusal = run == null ? DelveResult.NoRunInProgress
                    : player == null ? DelveResult.PlayerNotFound
                    : !IsAtLanding(run) ? DelveResult.NotAtTheBottom
                    : null;
                if (refusal != null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = refusal.Value;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                DateTime now = Now();
                int nextFloor;
                long stake;
                CommodityRecord? goldRow;

                if (!run!.IsDeep)
                {
                    (outcome.DiamondsGranted, outcome.GoldReturned, goldRow) = await BankFloorsAsync(db, player!, run, playerId);

                    // Spec §3.4 (b): sampled from the locked row, so the stake is
                    // honest even on a day no checkpoint has run.
                    long heldAfterBank = goldRow?.Quantity ?? 0L;
                    await GoldHighWater.RecordAsync(db, playerId, heldAfterBank, GoldHighWater.Today(now));

                    int highestRegion = await HighestRegionReachedAsync(db, playerId);
                    stake = await CurrentStakeAsync(db, playerId, highestRegion, heldAfterBank);
                    nextFloor = DelveRegistry.FirstDeepFloor;
                }
                else
                {
                    goldRow = await LockGoldRowAsync(db, playerId);
                    stake = run.StakeGold;
                    nextFloor = run.CurrentFloor + 1;
                }

                if (stake > quotedStake)
                {
                    await tx.RollbackAsync();
                    outcome = new DelveActionOutcome { Result = DelveResult.PriceChanged, View = await GetViewAsync(playerId) };
                    return outcome;
                }

                long toll = DelveRegistry.TollForFloor(stake, nextFloor);
                if (goldRow == null || goldRow.Quantity < toll)
                {
                    // The whole transaction goes, the floors-1-8 bank with it:
                    // the run stays at the bottom and can still walk out and be
                    // paid exactly that.
                    await tx.RollbackAsync();
                    outcome = new DelveActionOutcome { Result = DelveResult.NotEnoughGold, View = await GetViewAsync(playerId) };
                    return outcome;
                }

                goldRow.Quantity -= toll;

                if (!run.IsDeep)
                {
                    run.IsDeep = true;
                    run.StakeGold = stake;
                    RecordDepth(player!, DelveRegistry.FloorCount, now);
                }

                run.CurrentFloor = nextFloor;
                run.FloorsCleared = nextFloor - 1;
                var (packed, mask) = RollFloor(player!.BaseLuck, rng);
                run.PackedDoorDemands = packed;
                run.RevealedDoorMask = mask;

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                outcome.Result = DelveResult.Ok;
                outcome.GoldCharged = toll;
                outcome.View = await GetViewAsync(playerId);
                return outcome;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
    }
}
