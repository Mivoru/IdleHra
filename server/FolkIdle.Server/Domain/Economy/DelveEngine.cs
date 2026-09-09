using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
        /// <summary>Every floor is behind you. The only thing left to do is walk out.</summary>
        AtTheBottom,
        PlayerNotFound
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
    }

    public sealed class DelveActionOutcome
    {
        public DelveResult Result { get; set; }
        public DelveRunView View { get; set; } = new();

        /// <summary>Set only by a bank. Both are what the player was actually paid.</summary>
        public int DiamondsGranted { get; set; }
        public long GoldReturned { get; set; }
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

        public DelveEngine(IDbContextFactory<FolkIdleDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

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

        /// <summary>
        /// Fills the parts of the view that describe the PLAYER rather than the
        /// run - what a run would cost, what the ceiling has left, the sheet the
        /// doors will be resolved against.
        /// </summary>
        private static void ApplyPlayerContext(DelveRunView view, PlayerRecord player, int highestRegion, long gold, DateTime utcNow)
        {
            view.HighestRegionReached = highestRegion;
            view.EntryFeeForNextRun = DelveRegistry.EntryFeeForRegion(highestRegion);
            view.CurrentGold = gold;
            view.WeeklyDiamondCeiling = DelveRegistry.MaxDiamondsPerWeek;
            view.DiamondsEarnedThisWeek = player.DelveWeekKey == CurrentWeekKey(utcNow) ? player.DelveDiamondsThisWeek : 0;
            view.Attributes = new[] { player.BaseStrength, player.BaseDexterity, player.BaseConstitution, player.BaseLuck };
        }

        private static void ApplyRunToView(DelveRunView view, DelveRunRecord run, PlayerRecord player)
        {
            view.Active = true;
            view.CurrentFloor = run.CurrentFloor;
            view.FloorsCleared = run.FloorsCleared;
            view.ChargesRemaining = run.ChargesRemaining;
            view.EntryFeePaid = run.EntryFeePaid;

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
                odds[i] = revealed ? DelveRegistry.SuccessChance(AttributeValue(player, demand), run.CurrentFloor) : -1.0;
            }
            view.DoorDemands = demands;
            view.DoorOdds = odds;

            view.DiamondsIfBankedNow = DelveRegistry.DiamondsForBanking(run.FloorsCleared);
            view.DiamondsIfNextFloorCleared = DelveRegistry.DiamondsForBanking(
                Math.Min(run.FloorsCleared + 1, DelveRegistry.FloorCount));
        }

        private static void ApplyBankPreview(DelveRunView view, DelveRunRecord run)
        {
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

        /// <summary>Read-only. What the screen draws, whether or not a run is live.</summary>
        public async Task<DelveRunView> GetViewAsync(long playerId)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();

            var player = await db.PlayerRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId);
            var view = new DelveRunView();
            if (player == null) return view;

            int highestRegion = await HighestRegionReachedAsync(db, playerId);
            ApplyPlayerContext(view, player, highestRegion, await ReadGoldAsync(db, playerId), DateTime.UtcNow);

            var run = await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId);
            if (run != null)
            {
                ApplyRunToView(view, run, player);
                ApplyBankPreview(view, run);
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

                var goldRow = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

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
                    StartedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                db.DelveRunRecords.Add(run);

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                outcome.Result = DelveResult.Ok;
                outcome.View = new DelveRunView();
                ApplyPlayerContext(outcome.View, player, highestRegion, goldRow.Quantity, DateTime.UtcNow);
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

                if (run.FloorsCleared >= DelveRegistry.FloorCount)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.AtTheBottom;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                int demand = UnpackDoor(run.PackedDoorDemands, doorIndex);
                double chance = DelveRegistry.SuccessChance(AttributeValue(player, demand), run.CurrentFloor);
                bool passed = rng.NextDouble() < chance;

                int highestRegion = await HighestRegionReachedAsync(db, playerId);
                long gold = await ReadGoldAsync(db, playerId);

                if (passed)
                {
                    run.FloorsCleared++;
                    if (run.FloorsCleared >= DelveRegistry.FloorCount)
                    {
                        // The bottom. The doors stop being offered and the only
                        // remaining act is walking out with what was banked.
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
                    if (run.ChargesRemaining <= 0)
                    {
                        db.DelveRunRecords.Remove(run);
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();

                        outcome.Result = DelveResult.RunLost;
                        outcome.View = new DelveRunView();
                        ApplyPlayerContext(outcome.View, player, highestRegion, gold, DateTime.UtcNow);
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

                outcome.View = new DelveRunView();
                ApplyPlayerContext(outcome.View, player, highestRegion, gold, DateTime.UtcNow);
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

        /// <summary>
        /// Walk out. Pays diamonds up to the weekly ceiling and gold for
        /// whatever the ceiling refused, then ends the run.
        ///
        /// Banking a run that has cleared nothing is how a player abandons one:
        /// it pays zero and takes the row away, which is honest and needs no
        /// second command.
        /// </summary>
        public async Task<DelveActionOutcome> BankAsync(long playerId)
        {
            var outcome = new DelveActionOutcome();
            await using var db = await _contextFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var run = await db.DelveRunRecords
                    .FromSqlRaw("SELECT * FROM \"DelveRunRecords\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (run == null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.NoRunInProgress;
                    outcome.View = await GetViewAsync(playerId);
                    return outcome;
                }

                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (player == null)
                {
                    await tx.RollbackAsync();
                    outcome.Result = DelveResult.PlayerNotFound;
                    return outcome;
                }

                int weekKey = CurrentWeekKey(DateTime.UtcNow);
                if (player.DelveWeekKey != weekKey)
                {
                    player.DelveWeekKey = weekKey;
                    player.DelveDiamondsThisWeek = 0;
                }

                int gross = DelveRegistry.DiamondsForBanking(run.FloorsCleared);
                int remaining = Math.Max(0, DelveRegistry.MaxDiamondsPerWeek - player.DelveDiamondsThisWeek);
                int granted = Math.Min(gross, remaining);
                long consolation = ConsolationFor(run.EntryFeePaid, run.FloorsCleared, gross, granted);

                if (granted > 0)
                {
                    player.PremiumDiamonds += granted;
                    player.DelveDiamondsThisWeek += granted;
                }

                if (consolation > 0)
                {
                    var goldRow = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .FirstOrDefaultAsync();

                    if (goldRow == null)
                    {
                        db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = consolation });
                    }
                    else
                    {
                        goldRow.Quantity += consolation;
                    }
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
                ApplyPlayerContext(outcome.View, player, highestRegion, await ReadGoldAsync(db, playerId), DateTime.UtcNow);
                outcome.View.FloorsCleared = floorsCleared;
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
