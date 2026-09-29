using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 79, phase 2: every CREDIT of a player's gold is counted as income
    /// exactly once, or says why it is not income.
    /// </summary>
    /// <remarks>
    /// Modul: THE MIRROR OF GoldLedgerTests, WITH ONE MORE WAY TO BE WRONG.
    /// A debit happens in one place. A credit can have three - the engine that
    /// earns it, the tick's display-only AddGold, and whatever banks a delta
    /// (the checkpoint, Redis write-behind, a rescue, the retry outbox). Count
    /// at two of them and the ledger doubles; count at none and it vanishes.
    /// So every credit-shaped line must name its decision within three lines:
    /// GoldLedger.RecordIncomeAsync (an engine credited the row),
    /// GoldLedger.TallyIncome (the gold rides RedisPendingGoldDelta and the
    /// checkpoint writes the tally), or a "// GoldLedger:" comment saying which
    /// of those two already counted it.
    /// </remarks>
    public class GoldIncomeLedgerTests
    {
        private static readonly Regex[] CreditShapes =
        {
            // An engine credits the gold row itself.
            new(@"CommodityLedger\.AddAsync\([^;]*""gold""", RegexOptions.Compiled),
            new(@"\bgold\w*!?\.Quantity\s*\+=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Gold that the checkpoint or Redis will bank later.
            new(@"RedisPendingGoldDelta\s*\+=", RegexOptions.Compiled),
            // A delayed grant carrying gold (the retry outbox).
            new(@"\[""gold""\]\s*=", RegexOptions.Compiled),
            // The live balance moving - new income, or a display catching up.
            new(@"\.AddGold\(", RegexOptions.Compiled),
        };

        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }

        private static IEnumerable<string> ServerSources()
        {
            char sep = Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{sep}Migrations{sep}") && !p.Contains($"{sep}bin{sep}") && !p.Contains($"{sep}obj{sep}"));
        }

        [Fact]
        public void EveryGoldCredit_IsCountedOnceOrSaysWhyNot()
        {
            var unrecorded = new List<string>();
            int credits = 0;

            foreach (string path in ServerSources())
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) continue;
                    if (!CreditShapes.Any(r => r.IsMatch(lines[i]))) continue;

                    credits++;
                    bool covered = false;
                    for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                    {
                        if (lines[j].Contains("GoldLedger.RecordIncomeAsync")
                            || lines[j].Contains("GoldLedger.TallyIncome")
                            || lines[j].Contains("// GoldLedger:"))
                        {
                            covered = true;
                            break;
                        }
                    }
                    if (!covered) unrecorded.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            // 30 when this was written. Fewer means the patterns went blind.
            Assert.True(credits >= 25, $"only {credits} gold credits found - have the patterns gone blind?");
            Assert.True(unrecorded.Count == 0, "gold credited with no income decision:\n" + string.Join("\n", unrecorded));
        }

        /// <summary>
        /// The tally is only half a writer: it counts nothing unless every hop
        /// of the checkpoint carries it. Each of these is one hop, and each
        /// was written in the same change - so each is pinned by name.
        /// </summary>
        [Fact]
        public void TheTallyRidesEveryHopOfTheCheckpoint()
        {
            string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { ServerRoot() }.Concat(parts).ToArray()));

            string checkpoint = Read("Domain", "Shared", "StateCheckpointManager.cs");
            Assert.True(Regex.Matches(checkpoint, @"GoldLedger\.RecordIncomeTallyAsync\(dbContext, state\.PlayerId, state\.PendingGoldIncome\)").Count >= 2,
                "FlushState and FlushBatch must both write the tally");
            Assert.Contains("state.PendingGoldIncome = default;", checkpoint); // RequestFlush moves it onto the job
            Assert.Contains("state.PendingGoldIncome.Subtract(snapshot.PendingGoldIncome);", checkpoint); // login's sync flush

            Assert.Contains("Income = incomeForTick", Read("Domain", "Shared", "CheckpointWriter.cs"));
            Assert.Contains("payload.PendingGoldIncome.Add(in ack.Income);", Read("Domain", "Shared", "CheckpointAckTickCoordinator.cs"));
            Assert.Contains("reloaded.PendingGoldIncome.Add(in live.PendingGoldIncome);", Read("Engine", "StateReloadMerge.cs"));

            // The retry outbox credits gold under a generic item id, which no
            // credit shape above can see.
            Assert.Contains("GoldLedger.RecordIncomeAsync", Read("Engine", "PendingGrantOutbox.cs"));
        }

        [Fact]
        public void TallyIncome_RoutesEachPayloadSource_AndRefusesTheOthers()
        {
            var p = new TickStatePayload();
            GoldLedger.TallyIncome(ref p, GoldIncomeSource.Combat, 10);
            GoldLedger.TallyIncome(ref p, GoldIncomeSource.CombatAway, 20);
            GoldLedger.TallyIncome(ref p, GoldIncomeSource.TownHall, 30);
            GoldLedger.TallyIncome(ref p, GoldIncomeSource.AutoSalvage, 40);
            GoldLedger.TallyIncome(ref p, GoldIncomeSource.Combat, 0); // nothing

            Assert.Equal(10, p.PendingGoldIncome.Combat);
            Assert.Equal(20, p.PendingGoldIncome.CombatAway);
            Assert.Equal(30, p.PendingGoldIncome.TownHall);
            Assert.Equal(40, p.PendingGoldIncome.AutoSalvage);

            // A source an engine credits itself is recorded at that credit;
            // tallying it as well would count it twice.
            Assert.Throws<ArgumentOutOfRangeException>(() => GoldLedger.TallyIncome(ref p, GoldIncomeSource.Market, 5));
        }

        [Fact]
        public void AFailedAck_HandsTheTallyBack_ACommittedOneDoesNot()
        {
            var live = new TickStatePayload { FlushesInFlight = 2 };
            var income = new GoldIncomeTally { Combat = 7, AutoSalvage = 3 };

            CheckpointAckTickCoordinator.Apply(ref live, new FlushAck { Committed = true, CommittedDbEpoch = 1, Income = default, Reason = FlushReason.Periodic });
            Assert.True(live.PendingGoldIncome.IsEmpty);

            CheckpointAckTickCoordinator.Apply(ref live, new FlushAck { Committed = false, GoldDelta = 10, Income = income, Reason = FlushReason.Periodic });
            Assert.Equal(7, live.PendingGoldIncome.Combat);
            Assert.Equal(3, live.PendingGoldIncome.AutoSalvage);
            Assert.Equal(10, live.RedisPendingGoldDelta);
        }

        [Fact]
        public void AReload_CarriesTheLiveTally()
        {
            var live = new TickStatePayload();
            GoldLedger.TallyIncome(ref live, GoldIncomeSource.TownHall, 12);
            var reloaded = new TickStatePayload();

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(12, reloaded.PendingGoldIncome.TownHall);
        }

        [Fact]
        public void APendingGrantIsCountedUnderItsOrigin()
        {
            Assert.Equal(GoldIncomeSource.AutoSalvage, GoldLedger.SourceForPendingGrant(Models.PendingGrantSourceType.CombatLoot));
            Assert.Equal(GoldIncomeSource.TownHall, GoldLedger.SourceForPendingGrant(Models.PendingGrantSourceType.OfflineVillageProduction));
            Assert.Equal(GoldIncomeSource.Other, GoldLedger.SourceForPendingGrant(Models.PendingGrantSourceType.Gathering));
        }
    }
}
