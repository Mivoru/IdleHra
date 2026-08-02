using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: JSON WebSocket mode, 2026-08-02. Phase 0, step 2 of the web
    // client port plan (docs/architecture/WEB_CLIENT_PORT_PLAN.md).
    //
    // THIS FILE IS THE ENTIRE DEFENCE against the failure mode that plan
    // names as its highest risk: two clients whose idea of the wire drifts
    // apart. The dominant bug class in this codebase is two sources of truth
    // diverging silently - diamonds in two stores, gold credited to a
    // consumer that might not exist, affix ordering as an unwritten wire
    // contract - and a second client speaking a second encoding of the same
    // six packets is the largest possible instance of it.
    //
    // The load-bearing design choice these tests protect: the expected field
    // list is derived HERE by reflecting over the packet structs directly,
    // never by asking PacketJsonCodec what it thinks the fields are. A test
    // that asked the codec to agree with itself would pass forever while the
    // JSON silently omitted whatever the codec failed to see - which is
    // precisely how a JSON mode that quietly dropped chat and loot would have
    // shipped.
    //
    // Deliberately fixture-free: pure encoding, no Postgres, no Redis.
    public class WebClientJsonProtocolTests
    {
        // The six packets this wire carries. Restated here rather than read
        // out of PacketJsonCodec, for the same reason as the field lists: a
        // seventh packet type added to the protocol and forgotten by the
        // codec must fail a test, not silently become unsendable.
        public static IEnumerable<object[]> AllPacketTypes()
        {
            yield return new object[] { typeof(AuthHandshakePacket) };
            yield return new object[] { typeof(ClientCommandPacket) };
            yield return new object[] { typeof(StateUpdatePacket) };
            yield return new object[] { typeof(RequestChatMessagePacket) };
            yield return new object[] { typeof(ResponseChatMessagePacket) };
            yield return new object[] { typeof(ResponseLootDropPacket) };
        }

        // ---------- the contract ----------

        // The headline assertion of Phase 0: every field the binary struct
        // declares is present in the JSON shape. Not "the important ones" -
        // all of them, for all six, including the 151 on StateUpdatePacket.
        [Theory]
        [MemberData(nameof(AllPacketTypes))]
        public void JsonShapeCarriesEveryFieldTheBinaryStructDeclares(Type packetType)
        {
            string json = SerializeDefault(packetType);
            using JsonDocument document = JsonDocument.Parse(json);

            var present = new HashSet<string>(
                document.RootElement.EnumerateObject().Select(property => property.Name),
                StringComparer.Ordinal);

            string[] declared = packetType
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(field => field.Name)
                .ToArray();

            Assert.NotEmpty(declared);

            var missing = declared.Where(name => !present.Contains(name)).ToArray();
            Assert.True(missing.Length == 0,
                $"{packetType.Name}: the JSON shape is missing {missing.Length} field(s) the binary struct declares: {string.Join(", ", missing)}");
        }

        // Names alone would not catch a field carried at the wrong offset or
        // with the wrong width. A byte-exact round-trip over randomized
        // content does: any field the codec mis-sizes, mis-places or drops
        // shows up as a differing byte.
        [Theory]
        [MemberData(nameof(AllPacketTypes))]
        public void EveryPacketRoundTripsByteForByte(Type packetType)
        {
            MethodInfo generic = typeof(WebClientJsonProtocolTests)
                .GetMethod(nameof(AssertRoundTrip), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(packetType);

            try
            {
                generic.Invoke(null, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static void AssertRoundTrip<T>() where T : unmanaged
        {
            // Fixed seed: a failure must be reproducible, and this is a
            // structural property, not a fuzz target.
            var rng = new Random(20260802);

            for (int iteration = 0; iteration < 16; iteration++)
            {
                T original = RandomizePacket<T>(rng);

                byte[] json = PacketJsonCodec.SerializeToUtf8(ref original);

                Assert.True(PacketJsonCodec.TryDeserialize(json, out T restored, out string? error),
                    $"{typeof(T).Name}: round-trip failed to deserialize: {error}");

                byte[] originalBytes = ToBytes(ref original);
                byte[] restoredBytes = ToBytes(ref restored);

                if (!originalBytes.AsSpan().SequenceEqual(restoredBytes))
                {
                    int firstDiff = 0;
                    while (firstDiff < originalBytes.Length && originalBytes[firstDiff] == restoredBytes[firstDiff])
                    {
                        firstDiff++;
                    }

                    Assert.Fail(
                        $"{typeof(T).Name}: round-trip lost data at byte {firstDiff} " +
                        $"(field '{FieldNameAtOffset(typeof(T), firstDiff)}'). " +
                        $"Sent 0x{originalBytes[firstDiff]:x2}, got back 0x{restoredBytes[firstDiff]:x2}.\n{Encoding.UTF8.GetString(json)}");
                }
            }
        }

        // Ties the JSON contract back to the binary one: the sum of the JSON
        // field widths must equal the byte size NetworkPacketLayoutGuard pins
        // for that packet. If someone adds a field to a struct and updates
        // only one of the two guards, this fails.
        [Fact]
        public void JsonFieldPlanAccountsForEveryPinnedWireByte()
        {
            AssertPlanMatchesPinnedSize<AuthHandshakePacket>(NetworkPacketLayoutGuard.ExpectedAuthHandshakeSize);
            AssertPlanMatchesPinnedSize<ClientCommandPacket>(NetworkPacketLayoutGuard.ExpectedClientCommandSize);
            AssertPlanMatchesPinnedSize<StateUpdatePacket>(NetworkPacketLayoutGuard.ExpectedStateUpdateSize);
            AssertPlanMatchesPinnedSize<RequestChatMessagePacket>(NetworkPacketLayoutGuard.ExpectedRequestChatMessageSize);
            AssertPlanMatchesPinnedSize<ResponseChatMessagePacket>(NetworkPacketLayoutGuard.ExpectedResponseChatMessageSize);
            AssertPlanMatchesPinnedSize<ResponseLootDropPacket>(NetworkPacketLayoutGuard.ExpectedResponseLootDropSize);
        }

        private static void AssertPlanMatchesPinnedSize<T>(int pinnedSize) where T : unmanaged
        {
            // PacketJsonCodec's own plan builder throws unless its fields
            // cover the struct contiguously, so simply asking for the plan
            // proves coverage; this then proves the struct is still the size
            // the binary guard pins.
            Assert.NotEmpty(PacketJsonCodec.FieldNames(typeof(T)));
            Assert.Equal(pinnedSize, Marshal.SizeOf<T>());
        }

        // ---------- the discriminator ----------

        // The reason a "type" field exists at all: the C# receive loops tell
        // packets apart by exact byte length, which NetworkPacketLayoutGuard
        // has to defend with an explicit no-two-sizes-collide check. Nothing
        // outside C# can rely on that, so the JSON wire must never need to.
        [Theory]
        [MemberData(nameof(AllPacketTypes))]
        public void EveryPacketCarriesItsOwnTypeDiscriminator(Type packetType)
        {
            using JsonDocument document = JsonDocument.Parse(SerializeDefault(packetType));

            Assert.True(document.RootElement.TryGetProperty(PacketJsonCodec.TypePropertyName, out JsonElement type),
                $"{packetType.Name}: no '{PacketJsonCodec.TypePropertyName}' discriminator, so a client would have to guess.");
            Assert.Equal(JsonValueKind.String, type.ValueKind);
            Assert.Equal(PacketJsonCodec.DiscriminatorFor(packetType), type.GetString());
        }

        [Fact]
        public void DiscriminatorsAreDistinctAcrossAllSixPacketTypes()
        {
            string[] discriminators = AllPacketTypes()
                .Select(row => PacketJsonCodec.DiscriminatorFor((Type)row[0]))
                .ToArray();

            Assert.Equal(6, discriminators.Length);
            Assert.Equal(6, discriminators.Distinct(StringComparer.Ordinal).Count());
            Assert.All(discriminators, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }

        [Fact]
        public void DeserializeRejectsAPayloadWhoseDiscriminatorNamesAnotherPacket()
        {
            var loot = new ResponseLootDropPacket { PlayerId = 7, ItemId = 42, Quantity = 3 };
            byte[] json = PacketJsonCodec.SerializeToUtf8(ref loot);

            Assert.False(PacketJsonCodec.TryDeserialize(json, out StateUpdatePacket _, out string? error));
            Assert.Contains(PacketJsonCodec.TypeStateUpdate, error);
        }

        [Fact]
        public void DeserializeRejectsAPayloadWithNoDiscriminatorAtAll()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"PlayerId\":7,\"ItemId\":42}");

            Assert.False(PacketJsonCodec.TryDeserialize(json, out ResponseLootDropPacket _, out string? error));
            Assert.Contains(PacketJsonCodec.TypePropertyName, error);
        }

        // ---------- individual field encodings ----------

        // A client filling three fields of ClientCommandPacket's fifty must
        // not have to send the other forty-seven. This is what makes the
        // protocol usable by hand, and by wscat, which is Phase 0's stated
        // exit criterion.
        [Fact]
        public void AbsentPropertiesLeaveTheirFieldsAtDefault()
        {
            byte[] json = Encoding.UTF8.GetBytes(
                $"{{\"{PacketJsonCodec.TypePropertyName}\":\"{PacketJsonCodec.TypeClientCommand}\"," +
                "\"Command\":1,\"TargetId\":101}");

            Assert.True(PacketJsonCodec.TryDeserialize(json, out ClientCommandPacket command, out string? error), error);

            Assert.Equal(CommandType.ChangeActivity, command.Command);
            Assert.Equal(101, command.TargetId);
            Assert.Equal(0, command.SecondaryId);
            Assert.Equal(Guid.Empty, command.TargetGuid);
            Assert.Equal(0u, command.RerollAutoMaxAttempts);
        }

        // The opposite of the above: a property that IS present but malformed
        // is a client bug, and silently coercing it to zero would turn a bad
        // command into a plausible-looking different command.
        [Fact]
        public void DeserializeRejectsAMalformedValueRatherThanCoercingIt()
        {
            byte[] json = Encoding.UTF8.GetBytes(
                $"{{\"{PacketJsonCodec.TypePropertyName}\":\"{PacketJsonCodec.TypeClientCommand}\"," +
                "\"TargetId\":\"not a number\"}");

            Assert.False(PacketJsonCodec.TryDeserialize(json, out ClientCommandPacket _, out string? error));
            Assert.Contains("TargetId", error);
        }

        // `fixed byte X[N]` is the one field shape with no natural JSON
        // representation. Base64 of the full capacity round-trips byte for
        // byte and keeps the buffer independent of its paired *Length field -
        // teaching the codec which length field pairs with which buffer would
        // be hand-maintained knowledge, which is the thing being avoided.
        [Fact]
        public void FixedByteBuffersTravelAsBase64OfTheFullCapacity()
        {
            var handshake = new AuthHandshakePacket();
            string token = "header.payload.signature";
            byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
            unsafe
            {
                for (int i = 0; i < tokenBytes.Length; i++)
                {
                    handshake.JwtToken[i] = tokenBytes[i];
                }
            }
            handshake.JwtTokenLength = (ushort)tokenBytes.Length;

            byte[] json = PacketJsonCodec.SerializeToUtf8(ref handshake);
            using JsonDocument document = JsonDocument.Parse(json);

            string encoded = document.RootElement.GetProperty("JwtToken").GetString()!;
            byte[] decoded = Convert.FromBase64String(encoded);

            Assert.Equal(AuthHandshakePacket.JwtTokenCapacity, decoded.Length);
            Assert.Equal(token, Encoding.UTF8.GetString(decoded, 0, handshake.JwtTokenLength));

            // And the length field stays its own, independent property.
            Assert.Equal(tokenBytes.Length, document.RootElement.GetProperty("JwtTokenLength").GetInt32());
        }

        // A short base64 payload is the normal case for a hand-written
        // client, which has no reason to pad its JWT out to 512 bytes.
        [Fact]
        public void ShortFixedBufferPayloadsAreZeroPaddedToCapacity()
        {
            string token = "abc.def.ghi";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
            byte[] json = Encoding.UTF8.GetBytes(
                $"{{\"{PacketJsonCodec.TypePropertyName}\":\"{PacketJsonCodec.TypeAuthHandshake}\"," +
                $"\"JwtToken\":\"{encoded}\",\"JwtTokenLength\":{token.Length}}}");

            Assert.True(PacketJsonCodec.TryDeserialize(json, out AuthHandshakePacket handshake, out string? error), error);
            Assert.Equal(token.Length, handshake.JwtTokenLength);

            unsafe
            {
                for (int i = 0; i < token.Length; i++)
                {
                    Assert.Equal((byte)token[i], handshake.JwtToken[i]);
                }

                Assert.Equal(0, handshake.JwtToken[token.Length]);
                Assert.Equal(0, handshake.JwtToken[AuthHandshakePacket.JwtTokenCapacity - 1]);
            }
        }

        // An oversized buffer must be rejected, not truncated: truncation
        // would write past a fixed array's capacity or silently corrupt the
        // neighbouring field, which on this wire is the adjacent packet field.
        [Fact]
        public void OversizedFixedBufferPayloadsAreRejected()
        {
            string encoded = Convert.ToBase64String(new byte[AuthHandshakePacket.JwtTokenCapacity + 1]);
            byte[] json = Encoding.UTF8.GetBytes(
                $"{{\"{PacketJsonCodec.TypePropertyName}\":\"{PacketJsonCodec.TypeAuthHandshake}\",\"JwtToken\":\"{encoded}\"}}");

            Assert.False(PacketJsonCodec.TryDeserialize(json, out AuthHandshakePacket _, out string? error));
            Assert.Contains("JwtToken", error);
        }

        // Combat math can produce a non-finite float (a divide by zero in a
        // block-strength ratio, say). Utf8JsonWriter throws on NaN by
        // default, and a throw here would abort a broadcast on the hot path -
        // so non-finite values travel as the named literals instead.
        [Fact]
        public void NonFiniteFloatsSurviveInsteadOfThrowingOnTheBroadcastPath()
        {
            var state = new StateUpdatePacket
            {
                PlayerId = 5,
                PlayerBlockStrengthPct = float.NaN,
                MentorshipExpBonusMultiplier = double.PositiveInfinity,
                CachedWarMultiplier = float.NegativeInfinity
            };

            byte[] json = PacketJsonCodec.SerializeToUtf8(ref state);

            Assert.True(PacketJsonCodec.TryDeserialize(json, out StateUpdatePacket restored, out string? error), error);
            Assert.True(float.IsNaN(restored.PlayerBlockStrengthPct));
            Assert.True(double.IsPositiveInfinity(restored.MentorshipExpBonusMultiplier));
            Assert.True(float.IsNegativeInfinity(restored.CachedWarMultiplier));
        }

        // Guid is 16 opaque bytes on the binary wire; on the JSON wire it is
        // a string, and the two must describe the same value.
        [Fact]
        public void GuidFieldsTravelAsStringsAndKeepTheirValue()
        {
            var expected = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
            var state = new StateUpdatePacket { Slot1_CharacterId = expected };

            byte[] json = PacketJsonCodec.SerializeToUtf8(ref state);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("Slot1_CharacterId").ValueKind);
            }

            Assert.True(PacketJsonCodec.TryDeserialize(json, out StateUpdatePacket restored, out string? error), error);
            Assert.Equal(expected, restored.Slot1_CharacterId);
        }

        // The 64 command opcodes are the whole point of ClientCommandPacket,
        // and an enum that serialized as its NAME would be a second spelling
        // of each opcode for both sides to maintain.
        [Fact]
        public void CommandOpcodesTravelAsTheirNumericValue()
        {
            var command = new ClientCommandPacket { Command = CommandType.StockFoodSlot };

            byte[] json = PacketJsonCodec.SerializeToUtf8(ref command);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                Assert.Equal((byte)CommandType.StockFoodSlot, document.RootElement.GetProperty("Command").GetByte());
            }

            Assert.True(PacketJsonCodec.TryDeserialize(json, out ClientCommandPacket restored, out string? error), error);
            Assert.Equal(CommandType.StockFoodSlot, restored.Command);
        }

        // ---------- the mode switch ----------

        // Binary stays the default in the most literal sense available: a
        // session constructed without saying anything speaks bytes, so no
        // existing call site can accidentally opt the Unity client into JSON.
        [Fact]
        public void SessionsDefaultToTheBinaryProtocol()
        {
            var session = new WebSocketSession(new System.Net.WebSockets.ClientWebSocket(), "token");
            Assert.False(session.UseJsonProtocol);

            var jsonSession = new WebSocketSession(new System.Net.WebSockets.ClientWebSocket(), "token", useJsonProtocol: true);
            Assert.True(jsonSession.UseJsonProtocol);
        }

        // ---------- helpers ----------

        private static string SerializeDefault(Type packetType)
        {
            MethodInfo generic = typeof(WebClientJsonProtocolTests)
                .GetMethod(nameof(SerializeDefaultCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(packetType);

            return (string)generic.Invoke(null, null)!;
        }

        private static string SerializeDefaultCore<T>() where T : unmanaged
        {
            T packet = default;
            return PacketJsonCodec.SerializeToString(ref packet);
        }

        private static T RandomizePacket<T>(Random rng) where T : unmanaged
        {
            T packet = default;
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref packet, 1));
            rng.NextBytes(bytes);

            // Random bytes reinterpreted as a float are frequently NaN, and a
            // NaN's exact bit pattern is not preserved by any textual format -
            // so float fields get finite random values instead. Their
            // placement is discovered by reflecting over the struct here,
            // independently of the codec, so this cannot accidentally paper
            // over a codec that mislocated one.
            foreach (FieldInfo field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                int offset = checked((int)Marshal.OffsetOf<T>(field.Name));
                if (field.FieldType == typeof(float))
                {
                    MemoryMarshal.Write(bytes.Slice(offset), (float)((rng.NextDouble() - 0.5) * 2048.0));
                }
                else if (field.FieldType == typeof(double))
                {
                    MemoryMarshal.Write(bytes.Slice(offset), (rng.NextDouble() - 0.5) * 2048.0);
                }
            }

            return packet;
        }

        private static byte[] ToBytes<T>(ref T packet) where T : unmanaged
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)).ToArray();
        }

        private static string FieldNameAtOffset(Type packetType, int offset)
        {
            string best = "<unknown>";
            int bestOffset = -1;

            foreach (FieldInfo field in packetType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                int fieldOffset = checked((int)Marshal.OffsetOf(packetType, field.Name));
                if (fieldOffset <= offset && fieldOffset > bestOffset)
                {
                    bestOffset = fieldOffset;
                    best = field.Name;
                }
            }

            return best;
        }
    }
}
