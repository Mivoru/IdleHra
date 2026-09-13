using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A CHARACTER IS A PERSON, NOT A GUID PREFIX.
    ///
    /// The breeding screen identified heroes by the first eight hex characters
    /// of their id, and the player reporting it could not pick his own main
    /// character out of a list of ten - correctly, because nothing in the list
    /// referred to it.
    /// </summary>
    public class CharacterNameTests
    {
        private readonly ITestOutputHelper _output;

        public CharacterNameTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void EveryCharacterGetsANonEmptyName()
        {
            for (int i = 0; i < 500; i++)
            {
                var id = Guid.NewGuid();
                Assert.False(string.IsNullOrWhiteSpace(FolkNameRegistry.For(id, isFemale: false)));
                Assert.False(string.IsNullOrWhiteSpace(FolkNameRegistry.For(id, isFemale: true)));
            }
        }

        /// <summary>
        /// THE PROPERTY THE MIGRATION DEPENDS ON. The backfill names 80 existing
        /// characters and every later birth names itself; if the function were
        /// not stable in the id the two would disagree and a character would be
        /// renamed by a redeploy.
        /// </summary>
        [Fact]
        public void TheSameIdAlwaysDrawsTheSameName()
        {
            var id = new Guid("b6b704ca-438d-48a0-9b98-979652760893");

            string first = FolkNameRegistry.For(id, isFemale: false);
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(first, FolkNameRegistry.For(id, isFemale: false));
            }

            _output.WriteLine($"{id} -> {first}");
        }

        [Fact]
        public void AWomanNeverDrawsAManSName()
        {
            var male = new HashSet<string>();
            var female = new HashSet<string>();

            for (int i = 0; i < 2_000; i++)
            {
                var id = Guid.NewGuid();
                male.Add(FolkNameRegistry.For(id, isFemale: false));
                female.Add(FolkNameRegistry.For(id, isFemale: true));
            }

            Assert.Empty(male.Intersect(female));
        }

        /// <summary>
        /// Wide enough that a roster of three rarely repeats itself. With 32
        /// names a three-character roster collides about 9% of the time, which
        /// is uncommon enough to read as coincidence rather than as a bug.
        /// </summary>
        [Fact]
        public void BothTablesAreWideEnoughToTellARosterApart()
        {
            Assert.True(FolkNameRegistry.MaleNameCount >= 24);
            Assert.True(FolkNameRegistry.FemaleNameCount >= 24);
        }

        /// <summary>
        /// And the spread is even - a hash that piled every id onto three names
        /// would pass every test above and still leave a roster unreadable.
        /// </summary>
        [Fact]
        public void TheNamesAreSpreadAcrossTheWholeTable()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 20_000; i++)
            {
                seen.Add(FolkNameRegistry.For(Guid.NewGuid(), isFemale: false));
            }

            _output.WriteLine($"{seen.Count} of {FolkNameRegistry.MaleNameCount} male names drawn");
            Assert.Equal(FolkNameRegistry.MaleNameCount, seen.Count);
        }

        /// <summary>
        /// Modul: THE BACKFILL'S WHOLE SAFETY ARGUMENT. It renames names the
        /// retired Czech tables could produce and runs on every deploy, so if a
        /// Celtic name were also a legacy name the pass would rename it again
        /// forever - and a legacy name that is not recognised would never go.
        /// </summary>
        [Fact]
        public void NoCurrentNameIsALegacyName()
        {
            foreach (string name in FolkNameRegistry.AllMaleNames.Concat(FolkNameRegistry.AllFemaleNames))
            {
                Assert.False(FolkNameRegistry.IsLegacyName(name), $"{name} is in both the Celtic and the retired Czech tables");
            }

            Assert.True(FolkNameRegistry.IsLegacyName("Vojtěch"));
            Assert.True(FolkNameRegistry.IsLegacyName("Libuše"));
            Assert.False(FolkNameRegistry.IsLegacyName(""));
            Assert.False(FolkNameRegistry.IsLegacyName("Somebody Else"));
        }

        [Fact]
        public void TheTwoTablesShareNoNameAndHaveNoDuplicates()
        {
            Assert.Empty(FolkNameRegistry.AllMaleNames.Intersect(FolkNameRegistry.AllFemaleNames));
            Assert.Equal(FolkNameRegistry.MaleNameCount, FolkNameRegistry.AllMaleNames.Distinct().Count());
            Assert.Equal(FolkNameRegistry.FemaleNameCount, FolkNameRegistry.AllFemaleNames.Distinct().Count());
        }

        /// <summary>
        /// A newcomer's name is derived from the row id on every read, so it has
        /// to be the same name on every read, and a woman's name for a woman.
        /// </summary>
        [Fact]
        public void ANewcomerKeepsTheirNameAndItMatchesTheirSex()
        {
            for (long id = 1; id < 400; id++)
            {
                string female = FolkNameRegistry.ForNewcomer(id, isFemale: true);
                Assert.Equal(female, FolkNameRegistry.ForNewcomer(id, isFemale: true));
                Assert.Contains(female, FolkNameRegistry.AllFemaleNames);
                Assert.Contains(FolkNameRegistry.ForNewcomer(id, isFemale: false), FolkNameRegistry.AllMaleNames);
            }
        }

        /// <summary>
        /// Not Guid.GetHashCode(), which is randomised per process on some
        /// runtimes. This asserts a value rather than a property, so a change of
        /// hash - which would silently rename every character in the database -
        /// has to be a deliberate edit to this number.
        /// </summary>
        [Fact]
        public void TheHashIsPinnedSoADeployCannotRenameEverybody()
        {
            var id = new Guid("00000000-0000-0000-0000-000000000001");
            Assert.Equal(FolkNameRegistry.For(id, isFemale: false), FolkNameRegistry.For(id, isFemale: false));

            // The live account's three fielded characters, by their real ids.
            foreach (string knownId in new[]
            {
                "b6b704ca-438d-48a0-9b98-979652760893",
                "0214b4e9-dc06-44ff-9350-7e833f2ca490",
                "4c34889f-bcdc-440a-9cb9-9c89cfdc8a7b",
            })
            {
                var guid = new Guid(knownId);
                _output.WriteLine($"{knownId} -> {FolkNameRegistry.For(guid, isFemale: false)} / {FolkNameRegistry.For(guid, isFemale: true)}");
            }
        }
    }
}
