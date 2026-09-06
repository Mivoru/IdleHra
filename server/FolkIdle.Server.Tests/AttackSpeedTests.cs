using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    // Modul: ATTACK SPEED WAS A PERCENT BEING READ AS A FRACTION.
    //
    // Reported from the live game, not found by reading: level 49 inside an
    // hour, 86,000 gold, and every region's boss dying to starting gear "like
    // butter" while the monster's health bar never appeared to move. All of it
    // is one line. `1500 * (1 - AttackSpeedPct)` with a percent-shaped value
    // meant eleven percent was read as eleven hundred, the interval went
    // negative, and it clamped to the 200 ms floor.
    //
    // DEX alone did it. `dex * 0.05` against a documented "+0.05% per point"
    // puts a level-ten character over the line, so essentially every player was
    // swinging seven and a half times faster than the game was ever balanced
    // for - and the pacing model never saw it, because the model computes with
    // DEX 0 and therefore never leaves 1500 ms.
    public class AttackSpeedTests
    {
        private readonly ITestOutputHelper _output;

        public AttackSpeedTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Test_AttackSpeed_ABareCharacterSwingsAtTheBaseInterval()
        {
            var stats = new CombatStats { AttackSpeedPct = 0f };
            Assert.Equal(1500, CombatDamageModel.AttackIntervalMs(in stats));
        }

        [Fact]
        public void Test_AttackSpeed_APercentIsAPercent()
        {
            // Twenty percent faster is 1200 ms, not "twenty times faster".
            var twentyPercent = new CombatStats { AttackSpeedPct = 20f };
            Assert.Equal(1200, CombatDamageModel.AttackIntervalMs(in twentyPercent));

            var fivePercent = new CombatStats { AttackSpeedPct = 5f };
            Assert.Equal(1425, CombatDamageModel.AttackIntervalMs(in fivePercent));
        }

        [Fact]
        public void Test_AttackSpeed_StackingCannotReachTheFloor()
        {
            // THE FAILURE THIS PREVENTS. A real level-49 character measured on
            // the live server carried DEX in the nineties and four pieces with
            // attack_speed_pct affixes - and some items carry the same affix
            // twice. Uncapped, that is the 200 ms floor and a game running at
            // seven times its own pace.
            var absurd = new CombatStats { AttackSpeedPct = 900f };
            int interval = CombatDamageModel.AttackIntervalMs(in absurd);

            _output.WriteLine($"900% attack speed resolves to {interval} ms");
            Assert.Equal(600, interval);
            Assert.True(interval >= 1500 * (1f - CombatDamageModel.MaxAttackSpeedReduction) - 1);
        }

        [Fact]
        public void Test_AttackSpeed_DexterityPaysWhatItsDocumentationPromises()
        {
            // Modul: DEXTERITY IS A CURVE NOW, not a flat 0.05% a point.
            //
            // A level pays 7 attribute points and nothing spends them for you,
            // so a long-played character holds hundreds in one attribute - at
            // the old flat rate that is +29% attack speed and +59% crit chance
            // from Finesse alone, which is the linear-and-uncapped shape
            // PowerCeilingTests refuses. See AttributeRegistry.
            //
            // A hundred points is 0.8 * sqrt(100) = 8%, plus the Quick Step
            // milestone at 25. The point of this test survives the change: DEX
            // pays what its documentation promises, and the documentation is
            // now the curve.
            var hundredDex = StatsCalculator.Calculate(str: 0, dex: 100, con: 0, lck: 0);

            float curve = AttributeRegistry.DiminishedPercent(
                AttributeRegistry.AttackSpeedPerRootPoint, 100);
            _output.WriteLine($"100 DEX: {hundredDex.AttackSpeedPct:F1}% attack speed ({curve:F1}% from the curve, the rest from Quick Step)");

            Assert.True(hundredDex.AttackSpeedPct >= curve,
                "the curve alone should be a floor - the milestone adds to it.");
            Assert.Equal(11f, hundredDex.AttackSpeedPct, 3);

            // And it still reaches the interval, which is what the whole file
            // is about: attack speed was read as a fraction and written as a
            // percent once, and every player past level ten attacked at the
            // 200 ms floor for the whole game.
            Assert.Equal(1335, CombatDamageModel.AttackIntervalMs(in hundredDex));
        }

        // The live tick used to compute the interval itself, so the two could
        // disagree - and after the fix they would have, since only one of them
        // was corrected. Both call the model now, and this pins that they agree
        // for the stat range a real character actually reaches.
        [Fact]
        public void Test_AttackSpeed_TheModelAndTheLiveTickAgree()
        {
            foreach (int dex in new[] { 0, 20, 60, 120, 400 })
            {
                var stats = StatsCalculator.Calculate(str: 0, dex: dex, con: 0, lck: 0);
                int fromModel = CombatDamageModel.AttackIntervalMs(in stats);

                Assert.InRange(fromModel, 600, 1500);
                _output.WriteLine($"DEX {dex,4}: {stats.AttackSpeedPct,6:F2}% -> {fromModel} ms");
            }
        }
    }
}
