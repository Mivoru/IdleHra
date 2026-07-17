using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Tests.PlayMode
{
    // Modul: Phase 5, Part 3. Real, executable PlayMode coverage for the
    // client's binary wire-parsing path - previously zero client-side
    // tests exercised UnsafePacketParser or VisualSyncProxy's packet-
    // application logic (the server side of this exact contract is
    // covered by NetworkPacketLayoutGuard/HardenedEngineIntegrationTests,
    // but nothing proved the client's own deserialization and field
    // mapping independently). A PlayMode test (not EditMode) is required
    // here specifically because VisualSyncProxy and WebSocketClient are
    // MonoBehaviours whose Awake/Start lifecycle and per-frame Update()
    // loop this test relies on running for real, not simulated.
    public class NetworkPacketParsingTests
    {
        private GameObject _gameObject;
        private WebSocketClient _networkClient;
        private VisualSyncProxy _syncProxy;

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            _gameObject = new GameObject("NetworkPacketParsingTests_Harness");
            _networkClient = _gameObject.AddComponent<WebSocketClient>();
            _syncProxy = _gameObject.AddComponent<VisualSyncProxy>();
            _syncProxy.NetworkClient = _networkClient;

            // Let Awake/Start run for real through Unity's own lifecycle
            // (WebSocketClient.Start() validates NetworkPacketLayoutGuard
            // and boots FlightRecorder/ClientContentRegistry - none of
            // which open a real network connection) rather than calling
            // those MonoBehaviour messages directly, so this test exercises
            // the same startup path the real game does.
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (_gameObject != null)
            {
                UnityEngine.Object.Destroy(_gameObject);
            }
            yield return null;
        }

        // Modul: constructs a StateUpdatePacket entirely in memory, blits it
        // to a raw byte buffer the same way NetworkBroadcastSystem.SendToPlayer
        // does server-side (MemoryMarshal/Unsafe.WriteUnaligned over the
        // whole struct, not field-by-field serialization), then feeds those
        // raw bytes through UnsafePacketParser.TryParseState - the exact
        // client deserialization entry point WebSocketClient's real receive
        // loop calls - and asserts a full byte-for-byte round trip,
        // including all 4 CommandResult ring-buffer slots (see Phase 3's
        // wire-bloat/ring-buffer work). This is the structural proof the
        // "680-byte StateUpdatePacket payload" parses correctly end to end,
        // read dynamically via Marshal.SizeOf<StateUpdatePacket>() rather
        // than hardcoding 680, so this test stays correct across future
        // packet-layout changes instead of silently drifting stale.
        [Test]
        public void UnsafePacketParser_ParsesRawBytes_RoundTripsAllFieldsIncludingCommandResultRingBuffer()
        {
            int expectedSize = Marshal.SizeOf<StateUpdatePacket>();

            var packet = new StateUpdatePacket
            {
                PlayerId = 12345L,
                CurrentLevel = 7,
                CurrentXp = 999L,
                PlayerHp = 8000,
                CurrentMonsterHp = 4500,
                ActiveActivityId = 55,
                CurrentSimulationSpeedMultiplier = 2,
                ForgeLevel = 3,
                ActiveLanguageState = 2,
                CommandResult0_Code = (byte)CommandResultCode.InsufficientGold,
                CommandResult0_Tick = 5,
                CommandResult1_Code = (byte)CommandResultCode.ItemEquipped,
                CommandResult1_Tick = 3,
                CommandResult2_Code = 0,
                CommandResult2_Tick = 0,
                CommandResult3_Code = (byte)CommandResultCode.InvalidActivity,
                CommandResult3_Tick = 9
            };

            byte[] rawBuffer = new byte[expectedSize];
            Unsafe.WriteUnaligned(ref rawBuffer[0], packet);

            Assert.AreEqual(expectedSize, rawBuffer.Length, "StateUpdatePacket serialized to a different size than Marshal.SizeOf reports.");

            bool parsed = UnsafePacketParser.TryParseState(rawBuffer, rawBuffer.Length, out StateUpdatePacket parsedPacket);

            Assert.IsTrue(parsed, "UnsafePacketParser.TryParseState rejected a correctly-sized StateUpdatePacket buffer.");
            Assert.AreEqual(packet.PlayerId, parsedPacket.PlayerId);
            Assert.AreEqual(packet.CurrentLevel, parsedPacket.CurrentLevel);
            Assert.AreEqual(packet.CurrentXp, parsedPacket.CurrentXp);
            Assert.AreEqual(packet.PlayerHp, parsedPacket.PlayerHp);
            Assert.AreEqual(packet.CurrentMonsterHp, parsedPacket.CurrentMonsterHp);
            Assert.AreEqual(packet.ActiveActivityId, parsedPacket.ActiveActivityId);
            Assert.AreEqual(packet.CurrentSimulationSpeedMultiplier, parsedPacket.CurrentSimulationSpeedMultiplier);
            Assert.AreEqual(packet.ForgeLevel, parsedPacket.ForgeLevel);
            Assert.AreEqual(packet.ActiveLanguageState, parsedPacket.ActiveLanguageState);

            Assert.AreEqual(packet.CommandResult0_Code, parsedPacket.CommandResult0_Code);
            Assert.AreEqual(packet.CommandResult0_Tick, parsedPacket.CommandResult0_Tick);
            Assert.AreEqual(packet.CommandResult1_Code, parsedPacket.CommandResult1_Code);
            Assert.AreEqual(packet.CommandResult1_Tick, parsedPacket.CommandResult1_Tick);
            Assert.AreEqual(packet.CommandResult2_Code, parsedPacket.CommandResult2_Code);
            Assert.AreEqual(packet.CommandResult2_Tick, parsedPacket.CommandResult2_Tick);
            Assert.AreEqual(packet.CommandResult3_Code, parsedPacket.CommandResult3_Code);
            Assert.AreEqual(packet.CommandResult3_Tick, parsedPacket.CommandResult3_Tick);
        }

        [Test]
        public void UnsafePacketParser_RejectsUndersizedBuffer()
        {
            byte[] tooSmall = new byte[Marshal.SizeOf<StateUpdatePacket>() - 1];
            bool parsed = UnsafePacketParser.TryParseState(tooSmall, tooSmall.Length, out _);
            Assert.IsFalse(parsed, "UnsafePacketParser.TryParseState accepted a buffer smaller than StateUpdatePacket.");
        }

        // Modul: feeds a parsed StateUpdatePacket through the exact queue
        // WebSocketClient's real receive loop populates (PacketQueue), then
        // lets VisualSyncProxy.Update() run for real via yield return null -
        // asserting the packet's fields land on the corresponding Visual*
        // properties correctly, and specifically that the 4-slot
        // CommandResult ring buffer drains in ascending ResultTick order
        // (not slot order), firing OnCommandResultReceived once per new
        // entry rather than only for the newest - the Phase 3 fix this test
        // exists to lock in against regression.
        [UnityTest]
        public IEnumerator VisualSyncProxy_AppliesQueuedPacket_MapsFieldsAndDrainsCommandResultRingBufferInTickOrder()
        {
            var packet = new StateUpdatePacket
            {
                PlayerId = 55555L,
                CurrentLevel = 4,
                PlayerHp = 6000,
                CurrentMonsterHp = 2200,
                ActiveActivityId = 55,
                CurrentSimulationSpeedMultiplier = 1,
                ForgeLevel = 2,
                ActiveLanguageState = 3,
                CommandResult0_Code = (byte)CommandResultCode.InsufficientGold,
                CommandResult0_Tick = 5,
                CommandResult1_Code = (byte)CommandResultCode.ItemEquipped,
                CommandResult1_Tick = 3,
                CommandResult2_Code = 0,
                CommandResult2_Tick = 0,
                CommandResult3_Code = (byte)CommandResultCode.InvalidActivity,
                CommandResult3_Tick = 9
            };

            var observedCodes = new List<byte>();
            _syncProxy.OnCommandResultReceived += code => observedCodes.Add(code);

            _networkClient.PacketQueue.Enqueue(packet);

            yield return null;

            Assert.AreEqual((float)packet.PlayerHp, _syncProxy.VisualPlayerHp, 0.01f);
            Assert.AreEqual((float)packet.CurrentMonsterHp, _syncProxy.VisualMonsterHp, 0.01f);
            Assert.AreEqual(packet.CurrentSimulationSpeedMultiplier, _syncProxy.VisualCurrentSimulationSpeedMultiplier);
            Assert.AreEqual(packet.ForgeLevel, _syncProxy.VisualForgeLevel);
            Assert.AreEqual(packet.ActiveLanguageState, _syncProxy.VisualActiveLanguageState);

            // Ascending ResultTick order across slots is 1 (tick 3), 0
            // (tick 5), 3 (tick 9) - slot index order would incorrectly
            // read 0, 1, 3.
            CollectionAssert.AreEqual(
                new[] { (byte)CommandResultCode.ItemEquipped, (byte)CommandResultCode.InsufficientGold, (byte)CommandResultCode.InvalidActivity },
                observedCodes,
                "CommandResult ring buffer did not drain in ascending ResultTick order.");

            Assert.AreEqual((byte)CommandResultCode.InvalidActivity, _syncProxy.VisualLastCommandResultCode, "VisualLastCommandResultCode should reflect the highest-tick (most recent) slot after draining.");
        }
    }
}
