using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 79: every debit of a player's gold row is recorded in the ledger.
    /// </summary>
    /// <remarks>
    /// Modul: A SINK ADDED WITHOUT A CATEGORY VANISHES IN SILENCE. The ledger
    /// is a writer beside fifteen debits, so the failure is a new sixteenth
    /// debit that nobody wired. The spend still happens, the Treasury never
    /// counts it, and the statistics simply omit it. This test reads the
    /// source. Each `goldâ€¦.Quantity -= â€¦` must have a GoldLedger.RecordSpendAsync
    /// call, or a "// GoldLedger:" comment that says why it is not a spend
    /// (the order book's escrow), within three lines of it.
    /// </remarks>
    public class GoldLedgerTests
    {
        private static readonly Regex GoldDebit = new(
            @"\b(gold\w*|buyerGold|currencyRecord)!?\.Quantity\s*-=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnyDebit = new(@"\w+!?\.Quantity\s*-=", RegexOptions.Compiled);

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

        [Fact]
        public void EveryGoldDebit_IsRecordedOrSaysWhyNot()
        {
            var unrecorded = new List<string>();
            int debits = 0;

            foreach (string path in Directory.EnumerateFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")) continue;
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*")) continue;
                    // A debit is either on a variable NAMED for gold, or any
                    // `.Quantity -=` in code that mentions the "gold" item id
                    // just above it - the guild depot debits gold through a
                    // generic `playerCommodity` and the name-only pattern
                    // missed it on the first run.
                    bool isGoldDebit = GoldDebit.IsMatch(lines[i]);
                    if (!isGoldDebit && AnyDebit.IsMatch(lines[i]))
                    {
                        for (int k = Math.Max(0, i - 30); k < i && !isGoldDebit; k++)
                        {
                            isGoldDebit = lines[k].Contains("\"gold\"") || lines[k].Contains("'gold'");
                        }
                    }
                    if (!isGoldDebit) continue;

                    debits++;
                    bool covered = false;
                    for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                    {
                        if (lines[j].Contains("GoldLedger.RecordSpendAsync") || lines[j].Contains("// GoldLedger:"))
                        {
                            covered = true;
                            break;
                        }
                    }
                    if (!covered) unrecorded.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            // Fifteen when this was written. Fewer means the pattern stopped
            // finding them, and then the assertion below proves nothing.
            Assert.True(debits >= 15, $"only {debits} gold debits found - has the pattern gone blind?");
            Assert.True(unrecorded.Count == 0, "gold debited with no ledger entry:\n" + string.Join("\n", unrecorded));
        }

        [Fact]
        public void TheTreasuryPaysOnSpending_WithTheSameThresholds()
        {
            Assert.Equal(0, AchievementMilestones.EvaluateTreasuryTier(99_999));
            Assert.Equal(1, AchievementMilestones.EvaluateTreasuryTier(100_000));
            Assert.Equal(2, AchievementMilestones.EvaluateTreasuryTier(5_000_000));
            Assert.Equal(4, AchievementMilestones.EvaluateTreasuryTier(2_500_000_000));
            Assert.Contains("Spend", AchievementMilestones.TiersFor(AchievementMilestones.TreasuryAchievementId)[0].Goal);
        }

        [Fact]
        public void TheLiveCopyOnlyMovesUp()
        {
            GoldLedger.NoteLifetimeSpent(979_001, 5_000);
            GoldLedger.NoteLifetimeSpent(979_001, 3_000); // an older checkpoint's read, arriving late
            Assert.Equal(5_000, GoldLedger.KnownLifetimeSpent(979_001));
            Assert.Equal(0, GoldLedger.KnownLifetimeSpent(979_002));
        }
    }
}
