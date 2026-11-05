using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Audit 2026-11-02: engines that catch their own exception never reach
    /// SafeDispatchAsync's conflict handling, so a Serializable transaction that
    /// lost a race (40001) was swallowed. For the mail claim that stranded the
    /// mail as pending - hidden and unclaimable until the seven-day cleanup
    /// deleted it - and for market matching it left crossed orders unmatched.
    ///
    /// Each race here is real, not mocked: a second transaction updates the row
    /// the engine is about to lock, the engine's SELECT ... FOR UPDATE waits on
    /// it, and when the blocker commits Postgres fails the engine's attempt with
    /// "could not serialize access due to concurrent update".
    /// </summary>
    [Collection("Postgres collection")]
    public class LostRaceRecoveryTests
    {
        private readonly PostgresTestFixture _fixture;

        public LostRaceRecoveryTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task<long> CreatePlayerAsync(string prefix)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"{prefix}_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private async Task<long> CreateMailAsync(long playerId, long gold, bool isPending, bool isClaimed = false)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var mail = new MailboxInstance
            {
                PlayerId = playerId,
                GoldAttachment = gold,
                IsPending = isPending,
                IsClaimed = isClaimed,
                SenderName = "Test",
                MessageText = "race",
                ReceivedTimestamp = GameClock.UtcNowOffset.ToUnixTimeSeconds(),
            };
            db.MailboxInstances.Add(mail);
            await db.SaveChangesAsync();
            return mail.Id;
        }

        private async Task<MailboxInstance> ReadMailAsync(long mailId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.MailboxInstances.AsNoTracking().SingleAsync(m => m.Id == mailId);
        }

        private async Task<long> GoldOfAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity)
                .SingleOrDefaultAsync();
        }

        /// <summary>
        /// Runs <paramref name="engineCall"/> while another transaction holds an
        /// update on a row of <paramref name="table"/> with id <paramref name="rowId"/>,
        /// and commits that update only once the engine is waiting for the lock.
        /// </summary>
        private Task RaceAgainstAnUpdateAsync(string table, long rowId, string touchColumn, Func<Task> engineCall)
            => RaceAgainstSqlAsync(
                $"UPDATE \"{table}\" SET \"{touchColumn}\" = \"{touchColumn}\" WHERE \"Id\" = {{0}}",
                new object[] { rowId },
                $"%{table}%FOR UPDATE%",
                engineCall);

        /// <summary>
        /// Runs <paramref name="blockingSql"/> in an open transaction, starts
        /// <paramref name="engineCall"/>, waits until a statement matching
        /// <paramref name="waiterQueryLike"/> is queued behind the row lock, then
        /// commits the blocker.
        /// </summary>
        private async Task RaceAgainstSqlAsync(string blockingSql, object[] parameters, string waiterQueryLike, Func<Task> engineCall)
        {
            await using var blocker = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var tx = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlRawAsync(blockingSql, parameters);
            int blockerPid = await blocker.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

            Task call = engineCall();

            // Wait until the engine's statement is actually queued behind OUR
            // connection - committing earlier would be no race at all. Matched
            // by blocking pid rather than by query text: pg_stat_activity cuts
            // the text at 1 KB, and EF's column list for PlayerRecords alone is
            // longer than that. waiterQueryLike narrows it when the text fits.
            bool waiting = false;
            for (int i = 0; i < 200 && !waiting; i++)
            {
                await using var probe = await _fixture.DbContextFactory.CreateDbContextAsync();
                int waiters = await probe.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {0} = ANY(pg_blocking_pids(pid)) AND (query LIKE {1} OR length(query) >= 1000)",
                    blockerPid, waiterQueryLike).SingleAsync();
                waiting = waiters > 0;
                if (!waiting)
                {
                    await Task.Delay(25);
                }
            }
            if (!waiting)
            {
                await using var dump = await _fixture.DbContextFactory.CreateDbContextAsync();
                var activity = await dump.Database.SqlQueryRaw<string>(
                    "SELECT coalesce(wait_event_type,'-') || ' | ' || coalesce(state,'-') || ' | ' || left(query, 160) AS \"Value\" FROM pg_stat_activity WHERE datname = current_database()").ToListAsync();
                Assert.Fail($"the engine never queued behind the blocker ({waiterQueryLike}) - the race was not set up. Activity:\n{string.Join("\n", activity)}");
            }

            await tx.CommitAsync();
            await call;
        }

        [Fact]
        public async Task AMailClaimThatLosesARace_IsRetried_AndTheGoldArrives()
        {
            long playerId = await CreatePlayerAsync("mailrace");
            long mailId = await CreateMailAsync(playerId, 777, isPending: true);
            var registry = new PlayerSessionRegistry();
            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, registry);

            await RaceAgainstAnUpdateAsync("MailboxInstances", mailId, "QualityTier",
                () => engine.CommitMailClaimAsync(playerId, mailId, true));

            var mail = await ReadMailAsync(mailId);
            Assert.True(mail.IsClaimed, "the claim was lost to the race instead of retried");
            Assert.False(mail.IsPending);
            Assert.Equal(777, await GoldOfAsync(playerId));
            Assert.True(registry.CommandResultQueue.TryDequeue(out var result));
            Assert.Equal((byte)CommandResultCode.Success, result.ResultCode);
        }

        [Fact]
        public async Task ASecondCommitForTheSameMail_CreditsNothing()
        {
            long playerId = await CreatePlayerAsync("mailtwice");
            long mailId = await CreateMailAsync(playerId, 500, isPending: true);
            var registry = new PlayerSessionRegistry();
            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, registry);

            await engine.CommitMailClaimAsync(playerId, mailId, true);
            await engine.CommitMailClaimAsync(playerId, mailId, true);

            Assert.Equal(500, await GoldOfAsync(playerId));
        }

        [Fact]
        public async Task AClaimForAPlayerWhoWentOffline_IsStillCommitted()
        {
            // The drain used to skip a player missing from activePlayers, so a
            // disconnect between the claim and the next tick stranded the mail.
            long playerId = await CreatePlayerAsync("mailoff");
            long mailId = await CreateMailAsync(playerId, 250, isPending: true);
            var registry = new PlayerSessionRegistry();
            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, registry);
            registry.MailClaimRequestQueue.Enqueue(new MailClaimRequest { PlayerId = playerId, MailId = mailId, GoldAttachment = 250 });

            Task? dispatched = null;
            MailTickCoordinator.DrainClaimRequests(
                registry,
                new System.Collections.Generic.Dictionary<long, TickStatePayload>(),
                (_, _, action) => dispatched = action(),
                engine);

            Assert.NotNull(dispatched);
            await dispatched!;
            Assert.True((await ReadMailAsync(mailId)).IsClaimed);
            Assert.Equal(250, await GoldOfAsync(playerId));
        }

        [Fact]
        public async Task AtStartUp_PendingUnclaimedMailIsReleased_AndClaimedMailIsLeftAlone()
        {
            long playerId = await CreatePlayerAsync("mailboot");
            long stranded = await CreateMailAsync(playerId, 100, isPending: true);
            long claimed = await CreateMailAsync(playerId, 100, isPending: true, isClaimed: true);
            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            int released = await engine.ReleaseOrphanedClaimsAsync();

            Assert.True(released >= 1);
            Assert.False((await ReadMailAsync(stranded)).IsPending);
            var claimedMail = await ReadMailAsync(claimed);
            Assert.True(claimedMail.IsPending, "a claimed row is history and must not be rewritten");
            Assert.True(claimedMail.IsClaimed);
        }

        [Fact]
        public async Task CrossedMarketOrdersThatLoseARace_StillMatch()
        {
            long sellerId = await CreatePlayerAsync("mkseller");
            long buyerId = await CreatePlayerAsync("mkbuyer");
            string baseItemId = $"race_item_{Guid.NewGuid():N}";
            long buyOrderId;
            long equipmentId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var equipment = new MarketEquipmentInstance { PlayerId = sellerId, BaseItemId = baseItemId, QualityTier = 1, IsLockedInEscrow = true };
                db.MarketEquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;

                db.MarketOrderRecords.Add(new MarketOrderRecord
                {
                    SellerId = sellerId, OrderType = "SELL", BaseItemId = baseItemId, QualityTier = 1,
                    Price = 1_000, Status = 0, EquipmentInstanceId = equipmentId,
                });
                var buy = new MarketOrderRecord
                {
                    SellerId = buyerId, OrderType = "BUY", BaseItemId = baseItemId, QualityTier = 1,
                    Price = 1_200, Status = 0,
                };
                db.MarketOrderRecords.Add(buy);
                await db.SaveChangesAsync();
                buyOrderId = buy.Id;
            }

            var engine = new MarketOrderBookEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            await RaceAgainstAnUpdateAsync("MarketOrderRecords", buyOrderId, "Price",
                () => engine.MatchOrdersAsync(baseItemId, 1));

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var archive = await verify.HistoricalMarketArchives.AsNoTracking()
                .SingleOrDefaultAsync(a => a.EquipmentInstanceId == equipmentId);
            Assert.NotNull(archive);
            Assert.False(await verify.MarketOrderRecords.AsNoTracking().AnyAsync(o => o.BaseItemId == baseItemId));
            Assert.Equal(buyerId, (await verify.MarketEquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == equipmentId)).PlayerId);
            // The unused 200 of the buyer's escrow comes back.
            Assert.Equal(200, await GoldOfAsync(buyerId));
        }

        [Fact]
        public async Task ACodexKillBatchThatLosesARace_IsRetried_AndNoKillIsLost()
        {
            // The batch carries every player's kills for five seconds, and what
            // LoadPlayerState derives from it - the defeated-boss mask, the
            // unlocked regions - is durable. It used to be dropped whole.
            long playerId = await CreatePlayerAsync("codexrace");
            const int monsterId = 91;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = playerId, MonsterId = monsterId, KillCount = 5, FirstDrawnRarity = 1 });
                await db.SaveChangesAsync();
            }

            var engine = new CodexEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            CodexEngine.KillEventQueue.Enqueue(new KillEvent { PlayerId = playerId, MonsterId = monsterId, Kills = 3 });

            await RaceAgainstSqlAsync(
                "UPDATE monster_codex_entries SET \"KillCount\" = \"KillCount\" WHERE \"PlayerId\" = {0} AND \"MonsterId\" = {1}",
                new object[] { playerId, monsterId },
                "%monster_codex_entries%",
                () => engine.ProcessKillBatchAsync(System.Threading.CancellationToken.None));

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            int kills = await verify.MonsterCodexEntries.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.MonsterId == monsterId)
                .Select(c => c.KillCount).SingleAsync();
            Assert.Equal(8, kills);
        }

        [Fact]
        public async Task AnErasureThatLosesARace_IsRetried_AndTheAccountIsGone()
        {
            // The request comes from a live session whose last checkpoint may
            // still be writing the same PlayerRecords row the purge locks.
            long playerId = await CreatePlayerAsync("gdprrace");
            var engine = new CompliancePurgeEngine(_fixture.ServiceProvider, redis: null);

            bool purged = false;
            await RaceAgainstUpdateOnPlayerAsync(playerId, async () => purged = await engine.PurgeWithRetryAsync(playerId));

            Assert.True(purged);
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await verify.PlayerRecords.AsNoTracking().AnyAsync(p => p.Id == playerId), "the erasure was lost to the race");
        }

        private Task RaceAgainstUpdateOnPlayerAsync(long playerId, Func<Task> engineCall)
            => RaceAgainstSqlAsync(
                "UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = \"CurrentLevel\" WHERE \"Id\" = {0}",
                new object[] { playerId },
                "%FROM \"PlayerRecords\"%",
                engineCall);

        [Fact]
        public async Task AChestSaleThatLosesARace_SaysBusy_AndTheItemIsStillThere()
        {
            // It used to answer NotFound - "the item is gone" - for an item
            // that was still in the chest; the REST handler now retries Busy.
            long playerId = await CreatePlayerAsync("chestrace");
            long equipmentId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var piece = new EquipmentInstance { PlayerId = playerId, BaseItemId = "eq_linen_hood_helmet_armor_slot_base", QualityTier = 1, AffixPayload = "{}" };
                db.EquipmentInstances.Add(piece);
                await db.SaveChangesAsync();
                equipmentId = piece.Id;
            }

            (VillageChestEngine.ChestActionResult Result, long Gold) outcome = default;
            await RaceAgainstAnUpdateAsync("EquipmentInstances", equipmentId, "QualityTier", async () =>
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                outcome = await VillageChestEngine.RemoveEquipmentAsync(db, playerId, equipmentId, sell: true);
            });

            Assert.Equal(VillageChestEngine.ChestActionResult.Busy, outcome.Result);
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.True(await verify.EquipmentInstances.AsNoTracking().AnyAsync(e => e.Id == equipmentId));
        }

        // Lockstep validation 2026-11-05: 214-244 village upgrades and 8
        // recruits per 72-game-hour run of 20 bots lost a race like these and
        // answered "try again". They are retried now.

        private static long HumanGenome()
        {
            var genome = new GeneticVector(0);
            genome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            return genome.RawValue;
        }

        private async Task SeedBreederAsync(FolkIdleDbContext db, long playerId)
        {
            db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 1 });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 10_000L });
            await Task.CompletedTask;
        }

        private Task RaceAgainstAGoldUpdateAsync(long playerId, Func<Task> engineCall)
            => RaceAgainstSqlAsync(
                "UPDATE \"CommodityRecords\" SET \"Quantity\" = \"Quantity\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold'",
                new object[] { playerId },
                "%CommodityRecords%FOR UPDATE%",
                engineCall);

        // The tick's checkpoint flush writes the gold row; lockstep scale 1 lost
        // 18 hero-villager breedings to 40001/40P01 on 2026-11-05.
        [Fact]
        public async Task AHeroVillagerBreedingThatLosesARace_IsRetried_AndBearsOneChild()
        {
            long playerId = await CreatePlayerAsync("hvrace");
            Guid hero = Guid.NewGuid();
            long villagerId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await SeedBreederAsync(db, playerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = hero, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, IsFemale = false });
                var lineage = new CharacterLineageRegistry { CharacterId = hero, GenerationIndex = 2, GeneticVector = HumanGenome() };
                lineage.SetAptitudeVector(new[] { 4, 4, 4, 4 });
                db.CharacterLineages.Add(lineage);
                var newcomer = new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, IsFemale = true };
                newcomer.SetAptitudeVector(new[] { 6, 6, 6, 6 });
                db.VillageNewcomers.Add(newcomer);
                await db.SaveChangesAsync();
                villagerId = newcomer.Id;
            }
            var registry = new PlayerSessionRegistry();
            var engine = new BreedingEngine(_fixture.ServiceProvider, registry);

            await RaceAgainstAGoldUpdateAsync(playerId, () => engine.ExecuteHeroVillagerBreedingAsync(playerId, hero, villagerId));

            Assert.True(registry.BirthNotificationQueue.TryDequeue(out var birth), "the breeding was lost to the race instead of retried");
            Assert.False(registry.BirthNotificationQueue.TryDequeue(out _));
            Assert.Equal(new[] { (byte)CommandResultCode.BreedingSucceeded }, registry.CommandResultQueue.Select(r => r.ResultCode).ToArray());
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(1, await verify.CharacterLineages.CountAsync(l => l.ParentPaternalId == hero));
            Assert.Equal(10_000L - birth.GoldSpent, await GoldOfAsync(playerId));
        }

        [Fact]
        public async Task ABreedingThatLosesARace_IsRetried_AndBearsOneChild()
        {
            long playerId = await CreatePlayerAsync("brrace");
            Guid father = Guid.NewGuid();
            Guid mother = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await SeedBreederAsync(db, playerId);
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = father, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, IsFemale = false },
                    new CharacterRecord { Id = mother, PlayerId = playerId, AgePhase = 1, SlotIndex = 1, IsFemale = true });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = father, GenerationIndex = 0, GeneticVector = HumanGenome() },
                    new CharacterLineageRegistry { CharacterId = mother, GenerationIndex = 0, GeneticVector = HumanGenome() });
                await db.SaveChangesAsync();
            }
            var registry = new PlayerSessionRegistry();
            var engine = new BreedingEngine(_fixture.ServiceProvider, registry);

            await RaceAgainstAGoldUpdateAsync(playerId, () => engine.ExecuteBreedingAsync(playerId, father, mother));

            Assert.True(registry.BirthNotificationQueue.TryDequeue(out var birth), "the breeding was lost to the race instead of retried");
            Assert.False(registry.BirthNotificationQueue.TryDequeue(out _));
            Assert.Equal(new[] { (byte)CommandResultCode.BreedingSucceeded }, registry.CommandResultQueue.Select(r => r.ResultCode).ToArray());
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(1, await verify.CharacterLineages.CountAsync(l => l.ParentPaternalId == father && l.ParentMaternalId == mother));
            Assert.Equal(10_000L - birth.GoldSpent, await GoldOfAsync(playerId));
        }

        [Fact]
        public async Task AVillageUpgradeThatLosesARace_IsRetried_AndStarts()
        {
            long playerId = await CreatePlayerAsync("upgrace");
            var tier = VillageManagementEngine.GetTierMaterials(0);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = playerId,
                    BuildingId = (int)VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 0
                });
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = playerId, ItemId = tier.Log, Quantity = 100_000L },
                    new CommodityRecord { PlayerId = playerId, ItemId = tier.Ore, Quantity = 100_000L },
                    new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000_000L });
                await db.SaveChangesAsync();
            }
            var registry = new PlayerSessionRegistry();
            var engine = new VillageManagementEngine(_fixture.ServiceProvider, registry);

            await RaceAgainstSqlAsync(
                "UPDATE \"VillageInfrastructures\" SET \"CurrentLevel\" = \"CurrentLevel\" WHERE \"PlayerId\" = {0} AND \"BuildingId\" = {1}",
                new object[] { playerId, (int)VillageManagementEngine.LumberjackBuildingId },
                "%VillageInfrastructures%",
                () => engine.ExecuteUpgradeBuildingAsync(playerId, VillageManagementEngine.LumberjackBuildingId));

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await verify.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == playerId && v.BuildingId == (int)VillageManagementEngine.LumberjackBuildingId);
            Assert.Equal(1, row.UpgradeTargetLevel);
            Assert.True(registry.InfrastructureUpdateQueue.TryDequeue(out _), "the upgrade was lost to the race instead of retried");
            Assert.False(registry.InfrastructureUpdateQueue.TryDequeue(out _));

            // Paid exactly once: the lost attempt was rolled back whole.
            var quote = VillageManagementEngine.QuoteUpgrade((int)VillageManagementEngine.LumberjackBuildingId, 0);
            long Owed(string item) => quote.Where(l => l.ItemId == item).Sum(l => (long)l.Quantity);
            async Task<long> Held(string item) => await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == item).Select(c => c.Quantity).SingleAsync();
            Assert.Equal(100_000L - Owed(tier.Log), await Held(tier.Log));
            Assert.Equal(100_000L - Owed(tier.Ore), await Held(tier.Ore));
            Assert.Equal(1_000_000_000L - Owed("gold"), await Held("gold"));
            Assert.False(registry.CommandResultQueue.TryDequeue(out var result), $"a retried upgrade still answered {result.ResultCode}");
        }

        [Fact]
        public async Task ARecruitThatLosesARace_IsRetried_AndChargesOnce()
        {
            long playerId = await CreatePlayerAsync("recrace");
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = playerId,
                    BuildingId = (int)VillageManagementEngine.InnBuildingId,
                    CurrentLevel = 5
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000L });
                await db.SaveChangesAsync();
            }
            var registry = new PlayerSessionRegistry();
            var engine = new VillageManagementEngine(_fixture.ServiceProvider, registry);

            await RaceAgainstAnUpdateAsync("PlayerRecords", playerId, "CurrentLevel",
                () => engine.ExecuteRecruitVillagerAsync(playerId));

            long cost = VillagerArrivalRules.RecruitCostGold(0);
            Assert.True(registry.VillagerRecruitmentUpdateQueue.TryDequeue(out var notification), "the recruit was lost to the race instead of retried");
            Assert.Equal(cost, notification.GoldSpent);
            Assert.False(registry.VillagerRecruitmentUpdateQueue.TryDequeue(out _));
            Assert.Equal(1_000_000L - cost, await GoldOfAsync(playerId));
        }

        [Fact]
        public void ARefusalSaysTryAgainOnlyForALostRace()
        {
            var conflict = new InvalidOperationException("wrapped",
                new Npgsql.PostgresException("could not serialize", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.SerializationFailure));
            Assert.Equal(CommandResultCode.TransactionPending, TransientDbConflict.RefusalFor(conflict));
            Assert.Equal(CommandResultCode.GenericValidationFailure, TransientDbConflict.RefusalFor(new InvalidOperationException("bug")));
            Assert.Equal(CommandResultCode.BreedingFailed, TransientDbConflict.RefusalFor(new Exception("x"), CommandResultCode.BreedingFailed));
        }
    }
}
