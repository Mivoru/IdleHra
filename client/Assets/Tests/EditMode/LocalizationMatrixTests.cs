using System;
using System.Reflection;
using NUnit.Framework;
using FolkIdle.Client.UI;

namespace FolkIdle.Client.Tests.EditMode
{
    // Modul: Phase 5, Part 3. Real, executable EditMode coverage for
    // LocalizationMatrix - previously this dynamic-JSON-parsing,
    // unmanaged-memory-backed lookup system had zero client-side test
    // coverage (server-side ContentRegistry.TryGetLocalization tests
    // exist against the same source JSON, but LocalizationMatrix itself
    // is UnityEngine-dependent and cannot run inside the headless xunit
    // server test project - see HardenedEngineIntegrationTests.cs's own
    // comment on that gap). Application.streamingAssetsPath resolves to
    // Assets/StreamingAssets directly inside the Editor (both EditMode and
    // PlayMode), so Boot() reads the real, live
    // Assets/StreamingAssets/GameData/localizations.json here, not a
    // mock - these assertions exercise the exact file the game ships.
    public class LocalizationMatrixTests
    {
        [SetUp]
        public void SetUp()
        {
            LocalizationMatrix.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            LocalizationMatrix.Shutdown();
        }

        [Test]
        public void Boot_ParsesLocalizationsJson_AndResolvesKnownKeysAcrossAllLanguages()
        {
            LocalizationMatrix.Boot();

            char[] buffer = new char[64];

            int enLength = LocalizationMatrix.WriteToCharBuffer(1, LocalizationKey.HeaderMailbox, buffer, 0);
            Assert.AreEqual("Mailbox", new string(buffer, 0, enLength));

            int csLength = LocalizationMatrix.WriteToCharBuffer(2, LocalizationKey.ActiveEventPrefix, buffer, 0);
            Assert.AreEqual("Aktivni event: ", new string(buffer, 0, csLength));

            int deLength = LocalizationMatrix.WriteToCharBuffer(3, LocalizationKey.BossHpPrefix, buffer, 0);
            Assert.AreEqual("Boss LP: ", new string(buffer, 0, deLength));

            int plLength = LocalizationMatrix.WriteToCharBuffer(4, LocalizationKey.GuildWarStatusActive, buffer, 0);
            Assert.AreEqual("Wojna trwa", new string(buffer, 0, plLength));
        }

        // Modul: reflects into the private static _booted/_keyCount/
        // _matrixBlock fields to directly prove Boot() actually parsed the
        // file and registered an unmanaged memory block, rather than only
        // inferring it indirectly from successful lookups above - a
        // regression that left _matrixBlock as IntPtr.Zero while somehow
        // still returning plausible-looking strings (e.g. a stale value
        // from a previous test run) would not be caught by the lookup
        // assertions alone.
        [Test]
        public void Boot_RegistersUnmanagedMemoryBlock_WithNonZeroKeyCount()
        {
            LocalizationMatrix.Boot();

            Type matrixType = typeof(LocalizationMatrix);
            FieldInfo? bootedField = matrixType.GetField("_booted", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo? keyCountField = matrixType.GetField("_keyCount", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo? matrixBlockField = matrixType.GetField("_matrixBlock", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(bootedField, "LocalizationMatrix._booted field not found - has the field been renamed.");
            Assert.IsNotNull(keyCountField, "LocalizationMatrix._keyCount field not found - has the field been renamed.");
            Assert.IsNotNull(matrixBlockField, "LocalizationMatrix._matrixBlock field not found - has the field been renamed.");

            Assert.IsTrue((bool)bootedField!.GetValue(null)!);
            Assert.Greater((int)keyCountField!.GetValue(null)!, 0, "Boot() parsed zero keys out of localizations.json.");

            IntPtr matrixBlock = (IntPtr)matrixBlockField!.GetValue(null)!;
            Assert.AreNotEqual(IntPtr.Zero, matrixBlock, "Boot() did not allocate the unmanaged lookup block.");
        }

        [Test]
        public void Lookup_MissingLanguageId_FallsBackToEnglish()
        {
            LocalizationMatrix.Boot();
            char[] buffer = new char[64];

            // languageId 99 is not one of the 4 recognized codes (1=en,
            // 2=cs, 3=de, 4=pl - see Lookup's own langIndex clamp) - must
            // degrade to English rather than throw or return empty.
            int length = LocalizationMatrix.WriteToCharBuffer(99, LocalizationKey.EventNone, buffer, 0);
            Assert.AreEqual("None", new string(buffer, 0, length));
        }

        [Test]
        public void Lookup_LanguageIdZero_AlsoFallsBackToEnglish()
        {
            // Mirrors ContentRegistry/VisualSyncProxy's own convention
            // elsewhere in this codebase: languageId 0 means "unset," not
            // a distinct fifth language, and degrades to English (1) the
            // same way a genuinely unrecognized id does.
            LocalizationMatrix.Boot();
            char[] buffer = new char[64];

            int length = LocalizationMatrix.WriteToCharBuffer(0, LocalizationKey.EventNone, buffer, 0);
            Assert.AreEqual("None", new string(buffer, 0, length));
        }

        [Test]
        public void Boot_IsIdempotent_SecondCallDoesNotReparseOrThrow()
        {
            LocalizationMatrix.Boot();
            char[] buffer = new char[64];
            int firstLength = LocalizationMatrix.WriteToCharBuffer(1, LocalizationKey.HeaderMailbox, buffer, 0);

            Assert.DoesNotThrow(() => LocalizationMatrix.Boot());

            int secondLength = LocalizationMatrix.WriteToCharBuffer(1, LocalizationKey.HeaderMailbox, buffer, 0);
            Assert.AreEqual(firstLength, secondLength);
        }

        // Modul: proves the actual zero-allocation claim, not just the
        // absence of an obvious allocation in the source - warms up once
        // first (excluding one-time JIT/lazy-init cost, matching the
        // server-side Test_NetworkBroadcastSystem_LogSendFaultDelegate
        // test's identical warm-up rationale) before measuring a steady-
        // state call via GC.GetAllocatedBytesForCurrentThread().
        [Test]
        public void Lookup_IsAllocationFree_OnWarmSteadyStateCall()
        {
            LocalizationMatrix.Boot();
            char[] buffer = new char[64];

            LocalizationMatrix.WriteToCharBuffer(1, LocalizationKey.HeaderMailbox, buffer, 0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            LocalizationMatrix.WriteToCharBuffer(1, LocalizationKey.HeaderMailbox, buffer, 0);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before, "LocalizationMatrix.WriteToCharBuffer allocated managed heap memory on a warm call.");
        }
    }
}
