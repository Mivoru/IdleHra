using System;
using System.IO;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// WHO MAY BREED, AND WHY NOBODY COULD.
    ///
    /// Measured against the live database the day this was written:
    ///
    ///     SELECT count(*), count(*) FILTER (WHERE "Level" >= 50), max("Level")
    ///     FROM characters;   -- 80, 0, 1
    ///
    /// Both pairings gated on `Level >= 50` against a column on the character
    /// row, and the only writer of that column anywhere in the server was
    /// DevFixtureSeeder. A player's level is PlayerRecords.CurrentLevel - the
    /// ACCOUNT level. Characters have never had one. So breeding was refused
    /// for every player on every attempt, silently, since launch, and worked
    /// only on the dev fixture, which writes the column by hand. That is the
    /// repo's own "grep for a WRITER as well as a reader" trap, and the fixture
    /// is what hid it.
    ///
    /// The rules were also written THREE TIMES - once in the engine and once in
    /// each of the two preview endpoints - and had already drifted: the roster
    /// preview checked race but not sex, so choosing a woman as the paternal
    /// parent returned an eligible, priced preview for a pairing the engine
    /// then rolled back in silence. One pure gate now, called by all three.
    /// </summary>
    public class BreedingGateTests
    {
        private readonly ITestOutputHelper _output;

        public BreedingGateTests(ITestOutputHelper output) => _output = output;

        private static BreedingGateRules.Parent Adult(bool isFemale = false, byte race = 1) => new()
        {
            AgePhase = 1,
            IsFemale = isFemale,
            RaceId = race,
            IsLockedInEscrow = false,
            IsBreedingActive = false,
            BreedingCooldownEndEpoch = 0,
        };

        [Fact]
        public void AnAdultPairOfOppositeSexAndOneRaceMayBreed()
        {
            var refusal = BreedingGateRules.CheckPair(Adult(), Adult(isFemale: true), nowEpoch: 100);
            Assert.Equal(BreedingRefusal.None, refusal);
        }

        /// <summary>
        /// THE REGRESSION THIS FILE EXISTS FOR. There is no level anywhere in
        /// the gate, so a level-1 character - which on the live box is every
        /// character there has ever been - breeds.
        /// </summary>
        [Fact]
        public void ThereIsNoLevelInTheGateAtAll()
        {
            Assert.Equal(BreedingRefusal.None,
                BreedingGateRules.CheckPair(Adult(), Adult(isFemale: true), nowEpoch: 100));

            string engine = ReadSource("Engine", "BreedingEngine.cs");
            string broadcast = ReadSource("Network", "NetworkBroadcastSystem.cs");
            Assert.DoesNotContain("Level < 50", engine);
            Assert.DoesNotContain("Level < 50", broadcast);
        }

        [Fact]
        public void AChildMayNot()
        {
            var child = Adult();
            child.AgePhase = 0;
            Assert.Equal(BreedingRefusal.ParentNotAdult,
                BreedingGateRules.CheckPair(child, Adult(isFemale: true), nowEpoch: 100));
        }

        [Fact]
        public void TwoOfTheSameSexMayNot()
        {
            Assert.Equal(BreedingRefusal.SameSex,
                BreedingGateRules.CheckPair(Adult(), Adult(), nowEpoch: 100));
        }

        /// <summary>
        /// Stricter than "not the same sex": the ROLES have to be the right way
        /// round, because the labels mean what they say once every race arrives
        /// as a male/female pair. This is the rule the roster preview did not
        /// have, and the one that made the feature look dead.
        /// </summary>
        [Fact]
        public void SwappedRolesAreTheirOwnRefusal()
        {
            Assert.Equal(BreedingRefusal.SexRolesSwapped,
                BreedingGateRules.CheckPair(Adult(isFemale: true), Adult(isFemale: false), nowEpoch: 100));
        }

        [Fact]
        public void TwoRacesMayNot()
        {
            Assert.Equal(BreedingRefusal.RaceMismatch,
                BreedingGateRules.CheckPair(Adult(race: 1), Adult(isFemale: true, race: 2), nowEpoch: 100));
        }

        [Fact]
        public void AParentStillRestingMayNot()
        {
            var resting = Adult();
            resting.IsBreedingActive = true;
            resting.BreedingCooldownEndEpoch = 500;

            Assert.Equal(BreedingRefusal.ParentResting,
                BreedingGateRules.CheckPair(resting, Adult(isFemale: true), nowEpoch: 100));

            // ...and may once the cooldown has actually elapsed. The engine
            // clears the flag lazily rather than sweeping for it, so "active
            // with an expired stamp" has to read as free or a character is
            // retired by a flag nobody clears.
            Assert.Equal(BreedingRefusal.None,
                BreedingGateRules.CheckPair(resting, Adult(isFemale: true), nowEpoch: 501));
        }

        [Fact]
        public void AParentLockedInATradeMayNot()
        {
            var escrowed = Adult();
            escrowed.IsLockedInEscrow = true;
            Assert.Equal(BreedingRefusal.ParentInEscrow,
                BreedingGateRules.CheckPair(escrowed, Adult(isFemale: true), nowEpoch: 100));
        }

        [Fact]
        public void AVillagerWhoHasAlreadyMarriedMayNot()
        {
            Assert.Equal(BreedingRefusal.PartnerAlreadyMarried,
                BreedingGateRules.CheckVillagerPair(
                    Adult(), villagerIsFemale: true, villagerRaceId: 1, villagerHasMarried: true, nowEpoch: 100));
        }

        [Fact]
        public void AHeroAndAVillagerOfOneRaceAndOppositeSexMay()
        {
            Assert.Equal(BreedingRefusal.None,
                BreedingGateRules.CheckVillagerPair(
                    Adult(), villagerIsFemale: true, villagerRaceId: 1, villagerHasMarried: false, nowEpoch: 100));
        }

        /// <summary>
        /// A village pairing does NOT care which way round the sexes are - a
        /// woman of the player's line may marry a man from the village. Only
        /// the roster pairing carries the paternal/maternal role rule, because
        /// only there does the player pick which slot each goes in.
        /// </summary>
        [Fact]
        public void AWomanOfTheLineMayMarryAManFromTheVillage()
        {
            Assert.Equal(BreedingRefusal.None,
                BreedingGateRules.CheckVillagerPair(
                    Adult(isFemale: true), villagerIsFemale: false, villagerRaceId: 1, villagerHasMarried: false, nowEpoch: 100));
        }

        /// <summary>
        /// Every refusal must have something to say. Silent rollback is this
        /// server's documented favourite way to lie, and BreedingEngine was its
        /// largest single concentration: twenty RollbackAsync calls and not one
        /// EnqueueCommandResult between them.
        /// </summary>
        [Fact]
        public void EveryRefusalMapsToACommandResultCode()
        {
            foreach (BreedingRefusal refusal in Enum.GetValues<BreedingRefusal>())
            {
                if (refusal == BreedingRefusal.None) continue;

                var code = BreedingGateRules.ResultCodeFor(refusal);

                // Success is the enum's zero and the fall-through, so a refusal
                // that maps to it is a refusal somebody forgot to give a code -
                // which on the wire reads to the player as "that worked".
                Assert.NotEqual(FolkIdle.Server.Network.CommandResultCode.Success, code);
                _output.WriteLine($"{refusal} -> {code}");
            }
        }

        [Fact]
        public void NoTwoRefusalsShareACode()
        {
            var codes = Enum.GetValues<BreedingRefusal>()
                .Where(r => r != BreedingRefusal.None)
                .Select(BreedingGateRules.ResultCodeFor)
                .ToList();

            Assert.Equal(codes.Count, codes.Distinct().Count());
        }

        /// <summary>
        /// The engine must not carry a second copy of any of this. Three copies
        /// is how the preview came to disagree with the engine in the first
        /// place.
        /// </summary>
        [Fact]
        public void TheEngineAndBothPreviewsGoThroughTheGate()
        {
            string engine = ReadSource("Engine", "BreedingEngine.cs");
            string broadcast = ReadSource("Network", "NetworkBroadcastSystem.cs");

            Assert.Contains("BreedingGateRules.CheckPair", engine);
            Assert.Contains("BreedingGateRules.CheckVillagerPair", engine);
            Assert.Contains("BreedingGateRules.CheckPair", broadcast);
            Assert.Contains("BreedingGateRules.CheckVillagerPair", broadcast);
        }

        /// <summary>
        /// The CONTENTS, not the path. Both source assertions here were written
        /// against LocateSource's return value, which is a file name - so
        /// "there is no Level &lt; 50 in the engine" passed by searching a
        /// string that could never have contained it. A guard that cannot fail
        /// is not a guard.
        /// </summary>
        private static string ReadSource(params string[] parts)
            => File.ReadAllText(LocateSource(parts));

        private static string LocateSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName, "FolkIdle.Server" }.Concat(parts).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(string.Join("/", parts) + " not found from " + AppContext.BaseDirectory);
        }
    }
}
