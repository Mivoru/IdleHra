using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FolkIdle.Server.Network
{
    // Modul: JSON WebSocket mode, 2026-08-02. Phase 0, step 2 of the web
    // client port plan (docs/architecture/WEB_CLIENT_PORT_PLAN.md, 3.2).
    //
    // The wire this project has always spoken is six fixed-layout C# structs,
    // demultiplexed on the receiving side BY EXACT BYTE LENGTH - workable
    // between two C# processes where Marshal.SizeOf proves both sides agree
    // (see NetworkPacketLayoutGuard), and a trap for anything else. A browser
    // client would have to hand-maintain a DataView parser for 151 fields in
    // the largest struct alone, which is precisely the two-sources-of-truth
    // drift that produced this project's worst bugs.
    //
    // This codec is the alternative: the same six packets, rendered as JSON
    // with an explicit "type" discriminator so nothing anywhere dispatches on
    // length.
    //
    // THE DESIGN PROPERTY THAT MATTERS: the field list is never written down
    // here. It is derived by reflection from the struct itself, and
    // BuildPlan then asserts the derived plan covers every single byte of the
    // struct contiguously from offset 0 to Unsafe.SizeOf<T>(). So a field
    // added to StateUpdatePacket appears in the JSON automatically, and a
    // field this codec somehow failed to see is not a silently-missing JSON
    // property - it is a gap in the byte coverage, which throws at first use.
    // Drift is not caught here by a test that someone has to remember to
    // update; it is unrepresentable.
    //
    // Conventions on the wire:
    //   - Property names are the C# field names verbatim (PascalCase). No
    //     camelCase translation layer, because a translation layer is one
    //     more place the two sides can disagree.
    //   - "type" (lowercase) is the discriminator, and cannot collide with a
    //     field name since every field name is PascalCase.
    //   - Integers are JSON numbers. Guid is a JSON string ("d" format).
    //   - `fixed byte X[N]` buffers are base64 strings of the FULL fixed
    //     capacity. Base64 rather than the decoded text, even though all four
    //     such buffers happen to carry text today (JWT, chat message, device
    //     token, store receipt): base64 round-trips byte for byte with no
    //     assumption about content, and it keeps the buffer and its paired
    //     length field (e.g. JwtToken/JwtTokenLength) as two independent
    //     fields rather than teaching this codec which pairs with which -
    //     that pairing knowledge would be hand-maintained, which is the thing
    //     this file exists to avoid.
    //   - Non-finite floats are written as the strings "NaN"/"Infinity"/
    //     "-Infinity" and read back, so a divide-by-zero somewhere in combat
    //     math cannot throw on the broadcast path.
    public static class PacketJsonCodec
    {
        public const string TypePropertyName = "type";

        // Modul: the per-connection protocol switch. Carried on the auth
        // handshake object only - see NetworkBroadcastSystem's handshake
        // branch, which treats the WebSocket frame type (Text vs Binary) as
        // the real switch and this field as the explicit, legible
        // declaration of intent. Binary remains the default in every sense:
        // a client that says nothing gets the byte protocol the Unity client
        // has always spoken.
        public const string ModePropertyName = "mode";
        public const string ModeJson = "json";
        public const string ModeBinary = "binary";

        public const string TypeAuthHandshake = "AuthHandshake";
        public const string TypeClientCommand = "ClientCommand";
        public const string TypeStateUpdate = "StateUpdate";
        public const string TypeRequestChatMessage = "RequestChatMessage";
        public const string TypeResponseChatMessage = "ResponseChatMessage";
        public const string TypeResponseLootDrop = "ResponseLootDrop";

        // Every packet on this wire.
        //
        // Exposed through Discriminators below so the contract test can
        // cross-check this set against its OWN independently-restated list of
        // the six packet types. Deliberately a cross-check rather than the
        // test reading its list from here: a test that enumerated the
        // protocol from the codec would silently follow the codec anywhere it
        // went, including into forgetting a packet type. Two lists that must
        // agree catch an addition to either side.
        private static readonly Dictionary<Type, string> DiscriminatorsByType = new()
        {
            { typeof(AuthHandshakePacket), TypeAuthHandshake },
            { typeof(ClientCommandPacket), TypeClientCommand },
            { typeof(StateUpdatePacket), TypeStateUpdate },
            { typeof(RequestChatMessagePacket), TypeRequestChatMessage },
            { typeof(ResponseChatMessagePacket), TypeResponseChatMessage },
            { typeof(ResponseLootDropPacket), TypeResponseLootDrop },
        };

        public static IReadOnlyDictionary<Type, string> Discriminators => DiscriminatorsByType;

        public static string DiscriminatorFor(Type packetType)
        {
            if (!DiscriminatorsByType.TryGetValue(packetType, out string? discriminator))
            {
                throw new InvalidOperationException(
                    $"{packetType.Name} has no JSON type discriminator. Every packet on this wire must declare one in PacketJsonCodec.DiscriminatorsByType.");
            }

            return discriminator;
        }

        // ---------------------------------------------------------------
        // Field plan
        // ---------------------------------------------------------------

        private enum FieldKind : byte
        {
            U8,
            I8,
            U16,
            I16,
            U32,
            I32,
            U64,
            I64,
            F32,
            F64,
            GuidValue,
            FixedBytes
        }

        private readonly struct PacketField
        {
            public readonly string Name;
            public readonly int Offset;
            public readonly int Size;
            public readonly FieldKind Kind;

            public PacketField(string name, int offset, int size, FieldKind kind)
            {
                Name = name;
                Offset = offset;
                Size = size;
                Kind = kind;
            }
        }

        // Built once per packet type on first use; the CLR's static-field
        // initialization guarantees are what makes this thread safe without
        // a lock, which matters because SendToPlayer reaches it from the
        // 10Hz tick thread while the receive loops reach it from theirs.
        private static class Plan<T> where T : unmanaged
        {
            internal static readonly PacketField[] Fields = BuildPlanChecked(typeof(T), Unsafe.SizeOf<T>());
        }

        // Marshal.OffsetOf reports the MARSHALLED layout while the wire (and
        // MemoryMarshal.Read/Write below) uses the MANAGED one. For a
        // [StructLayout(Sequential, Pack = 1)] struct of blittable fields
        // those are the same thing - which is the whole reason this binary
        // protocol works at all - but that is an assumption, so it is
        // asserted rather than trusted.
        private static PacketField[] BuildPlanChecked(Type type, int managedSize)
        {
            int marshalledSize = Marshal.SizeOf(type);
            if (marshalledSize != managedSize)
            {
                throw new InvalidOperationException(
                    $"{type.Name}: marshalled size {marshalledSize} differs from managed size {managedSize}. " +
                    "The byte offsets this codec reads from would not match the bytes the binary wire writes.");
            }

            return BuildPlan(type, managedSize);
        }

        // Non-generic accessor for the contract test, which iterates packet
        // types as Type objects rather than as generic arguments.
        public static IReadOnlyList<string> FieldNames(Type packetType)
        {
            PacketField[] plan = BuildPlanChecked(packetType, Marshal.SizeOf(packetType));

            var names = new string[plan.Length];
            for (int i = 0; i < plan.Length; i++)
            {
                names[i] = plan[i].Name;
            }

            return names;
        }

        // Modul: web client port, Phase 1. Emits the whole wire contract as
        // JSON so the TypeScript client's types can be GENERATED from it
        // rather than hand-written.
        //
        // This is the port plan's single most important rule made mechanical:
        // "every feature must exist in exactly one place per layer". A
        // hand-written TypeScript mirror of 151 `StateUpdatePacket` fields
        // would be the largest two-sources-of-truth surface this project has
        // ever had. The schema comes from the same reflected field plan the
        // encoder uses, so the generated types cannot describe a packet the
        // server does not actually send.
        //
        // Deliberately a CLI dump (Program.cs `--dump-protocol`) rather than
        // an HTTP endpoint: type generation is a build-time concern and must
        // work in CI with no database, no Redis and no listening socket.
        // Chosen to exercise what a JavaScript port is most likely to get
        // wrong: the sign bit of a uint32 (0x80000000 and above), a value that
        // overflows int32 when added, a playerId whose HIGH word is non-zero
        // (real ids never are, so an implementation that ignores it would pass
        // in production and fail only if the id space grew), and the trivial
        // all-small case that must still not be zero.
        private static readonly (uint Seed, long PlayerId, long Epoch)[] ChallengeVectorInputs =
        {
            (1u, 1L, 0L),
            (0xA341316Cu, 9L, 23L),
            (0xFFFFFFFFu, 1042L, 1L),
            (0x80000000u, 7L, 0xFFFFFFFFL),
            (0x12345678u, 0x1_0000_0002L, 5L),
            (0x6D2B79F5u, 2147483647L, 2147483647L),
        };

        // Chosen so that (uint)epoch * 0x9E3779B9 overflows uint32 in most of
        // them - the case a double-precision `*` gets wrong - plus a playerId
        // with a non-zero high word, which the hash mixes separately.
        private static readonly (long PlayerId, long Epoch)[] GdprVectorInputs =
        {
            (1L, 0L),
            (1042L, 7L),
            (9L, 2147483647L),
            (7L, 4294967295L),
            (0x1_0000_0002L, 123456789L),
            (2147483647L, 1L),
        };

        public static string ExportSchemaJson()
        {
            var buffer = new ArrayBufferWriter<byte>(16 * 1024);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("typeProperty", TypePropertyName);
                writer.WriteString("modeProperty", ModePropertyName);

                writer.WriteStartArray("packets");
                foreach (KeyValuePair<Type, string> entry in DiscriminatorsByType)
                {
                    PacketField[] plan = BuildPlanChecked(entry.Key, Marshal.SizeOf(entry.Key));

                    writer.WriteStartObject();
                    writer.WriteString("name", entry.Key.Name);
                    writer.WriteString("discriminator", entry.Value);
                    writer.WriteNumber("byteSize", Marshal.SizeOf(entry.Key));

                    writer.WriteStartArray("fields");
                    foreach (PacketField field in plan)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", field.Name);
                        writer.WriteString("kind", field.Kind.ToString());
                        writer.WriteNumber("offset", field.Offset);
                        writer.WriteNumber("size", field.Size);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                // The 64 opcodes, so the client's CommandType union is
                // generated too - the alternative is a second copy of an enum
                // whose numbering already has deliberate gaps (20, 36, 37).
                writer.WriteStartArray("commandTypes");
                foreach (CommandType value in Enum.GetValues<CommandType>())
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", value.ToString());
                    writer.WriteNumber("value", (byte)value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                // Modul: anti-cheat challenge vectors.
                //
                // These belong in the wire contract as much as the field list
                // does. The server issues a challenge on the broadcast path
                // and quarantines an account after four unanswered ones, so a
                // client that cannot reproduce ComputeChallengeHash is not
                // merely missing a feature - it gets the player's account
                // flagged as a cheater within about a minute, persistently.
                //
                // A wrong answer is worse than none, and the hash is uint32
                // arithmetic, which is the single thing JavaScript is most
                // likely to get subtly wrong. So the server publishes known
                // answers and the TypeScript client tests against them: if
                // the two implementations ever disagree, the test says so
                // instead of a player getting banned for it.
                writer.WriteStartArray("challengeVectors");
                foreach ((uint seed, long playerId, long epoch) in ChallengeVectorInputs)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("seed", seed);
                    writer.WriteNumber("playerId", playerId);
                    writer.WriteNumber("logicEpochCounter", epoch);
                    writer.WriteNumber(
                        "expectedHash",
                        FolkIdle.Server.Engine.AntiCheatTelemetryEngine.ComputeChallengeHash(seed, playerId, epoch));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                // Modul: GDPR confirmation vectors, published for the same
                // reason as the challenge vectors above and with one extra
                // hazard of its own.
                //
                // ComputeGdprConfirmationHash multiplies the epoch by
                // 0x9E3779B9 in WRAPPING uint32 arithmetic. JavaScript's `*`
                // computes the true product in a double and silently loses the
                // low bits once it exceeds 2^53, so a naive port agrees with
                // the server for small epochs and diverges for large ones -
                // exactly the failure a hand-written test picks the wrong
                // inputs to catch. The vectors below therefore include epochs
                // whose product overflows, and the one command they gate
                // erases an account, so a silent divergence is expensive in
                // both directions: a failed purge looks identical to a
                // successful one from the client side.
                writer.WriteStartArray("gdprConfirmationVectors");
                foreach ((long playerId, long epoch) in GdprVectorInputs)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("playerId", playerId);
                    writer.WriteNumber("logicEpochCounter", epoch);
                    writer.WriteNumber(
                        "expectedHash",
                        FolkIdle.Server.Engine.ClientCommandValidator.ComputeGdprConfirmationHash(playerId, epoch));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static PacketField[] BuildPlan(Type type, int structSize)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var plan = new List<PacketField>(fields.Length);

            foreach (FieldInfo field in fields)
            {
                int offset = checked((int)Marshal.OffsetOf(type, field.Name));

                FixedBufferAttribute? fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
                if (fixedBuffer != null)
                {
                    if (fixedBuffer.ElementType != typeof(byte))
                    {
                        throw new InvalidOperationException(
                            $"{type.Name}.{field.Name}: only fixed byte buffers are representable on this wire, found {fixedBuffer.ElementType.Name}.");
                    }

                    plan.Add(new PacketField(field.Name, offset, fixedBuffer.Length, FieldKind.FixedBytes));
                    continue;
                }

                Type fieldType = field.FieldType;
                if (fieldType.IsEnum)
                {
                    // CommandType is a byte enum; the JSON carries the
                    // numeric opcode, exactly as the binary struct does.
                    fieldType = Enum.GetUnderlyingType(fieldType);
                }

                FieldKind kind;
                int size;
                if (fieldType == typeof(byte)) { kind = FieldKind.U8; size = 1; }
                else if (fieldType == typeof(sbyte)) { kind = FieldKind.I8; size = 1; }
                else if (fieldType == typeof(ushort)) { kind = FieldKind.U16; size = 2; }
                else if (fieldType == typeof(short)) { kind = FieldKind.I16; size = 2; }
                else if (fieldType == typeof(uint)) { kind = FieldKind.U32; size = 4; }
                else if (fieldType == typeof(int)) { kind = FieldKind.I32; size = 4; }
                else if (fieldType == typeof(ulong)) { kind = FieldKind.U64; size = 8; }
                else if (fieldType == typeof(long)) { kind = FieldKind.I64; size = 8; }
                else if (fieldType == typeof(float)) { kind = FieldKind.F32; size = 4; }
                else if (fieldType == typeof(double)) { kind = FieldKind.F64; size = 8; }
                else if (fieldType == typeof(Guid)) { kind = FieldKind.GuidValue; size = 16; }
                else
                {
                    throw new InvalidOperationException(
                        $"{type.Name}.{field.Name}: type {fieldType.Name} has no JSON representation on this wire. Add one to PacketJsonCodec.BuildPlan.");
                }

                plan.Add(new PacketField(field.Name, offset, size, kind));
            }

            plan.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

            // The anti-drift assertion. If the reflected plan does not
            // account for every byte from 0 to the struct's real size, then
            // some field is invisible to this codec - and an invisible field
            // is exactly the silently-omitted-from-JSON failure the port
            // plan warns is the largest risk in the whole project. Fail
            // loudly at first use rather than shipping a lossy protocol.
            int cursor = 0;
            foreach (PacketField field in plan)
            {
                if (field.Offset != cursor)
                {
                    throw new InvalidOperationException(
                        $"{type.Name}: JSON field plan is not contiguous - '{field.Name}' starts at byte {field.Offset}, expected {cursor}. " +
                        "Every byte of a wire packet must be carried by exactly one JSON property.");
                }

                cursor += field.Size;
            }

            if (cursor != structSize)
            {
                throw new InvalidOperationException(
                    $"{type.Name}: JSON field plan covers {cursor} bytes but the struct is {structSize}. " +
                    "Some field is invisible to the codec and would be silently dropped from JSON.");
            }

            return plan.ToArray();
        }

        // ---------------------------------------------------------------
        // Serialization
        // ---------------------------------------------------------------

        // Allocates a fresh array per call. Deliberate and acceptable: the
        // binary path (SendToPlayer's reusable per-session buffer) is
        // untouched and keeps its zero-allocation property, and this one only
        // runs for connections that explicitly asked for JSON, where the
        // whole premise (see the port plan, 3.2) is that ~2 KB of JSON at
        // 10Hz per browser player is not the constraint.
        public static byte[] SerializeToUtf8<T>(ref T packet) where T : unmanaged
        {
            var buffer = new ArrayBufferWriter<byte>(Math.Max(256, Unsafe.SizeOf<T>() * 3));
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString(TypePropertyName, DiscriminatorFor(typeof(T)));
                WriteFields(writer, Plan<T>.Fields, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)));
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        public static string SerializeToString<T>(ref T packet) where T : unmanaged
        {
            return System.Text.Encoding.UTF8.GetString(SerializeToUtf8(ref packet));
        }

        private static void WriteFields(Utf8JsonWriter writer, PacketField[] plan, ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < plan.Length; i++)
            {
                PacketField field = plan[i];
                ReadOnlySpan<byte> slice = bytes.Slice(field.Offset, field.Size);

                switch (field.Kind)
                {
                    case FieldKind.U8:
                        writer.WriteNumber(field.Name, slice[0]);
                        break;
                    case FieldKind.I8:
                        writer.WriteNumber(field.Name, unchecked((sbyte)slice[0]));
                        break;
                    case FieldKind.U16:
                        writer.WriteNumber(field.Name, MemoryMarshal.Read<ushort>(slice));
                        break;
                    case FieldKind.I16:
                        writer.WriteNumber(field.Name, MemoryMarshal.Read<short>(slice));
                        break;
                    case FieldKind.U32:
                        writer.WriteNumber(field.Name, MemoryMarshal.Read<uint>(slice));
                        break;
                    case FieldKind.I32:
                        writer.WriteNumber(field.Name, MemoryMarshal.Read<int>(slice));
                        break;
                    case FieldKind.U64:
                        writer.WriteNumber(field.Name, MemoryMarshal.Read<ulong>(slice));
                        break;
                    case FieldKind.I64:
                        writer.WriteNumber(field.Name, MemoryMarshal.Read<long>(slice));
                        break;
                    case FieldKind.F32:
                    {
                        float value = MemoryMarshal.Read<float>(slice);
                        if (float.IsFinite(value))
                        {
                            writer.WriteNumber(field.Name, value);
                        }
                        else
                        {
                            writer.WriteString(field.Name, value.ToString(CultureInfo.InvariantCulture));
                        }
                        break;
                    }
                    case FieldKind.F64:
                    {
                        double value = MemoryMarshal.Read<double>(slice);
                        if (double.IsFinite(value))
                        {
                            writer.WriteNumber(field.Name, value);
                        }
                        else
                        {
                            writer.WriteString(field.Name, value.ToString(CultureInfo.InvariantCulture));
                        }
                        break;
                    }
                    case FieldKind.GuidValue:
                        writer.WriteString(field.Name, MemoryMarshal.Read<Guid>(slice));
                        break;
                    case FieldKind.FixedBytes:
                        writer.WriteString(field.Name, Convert.ToBase64String(slice));
                        break;
                }
            }
        }

        // ---------------------------------------------------------------
        // Deserialization
        // ---------------------------------------------------------------

        // Reads the envelope once so the caller can dispatch on "type" and
        // then hand the same JsonElement to the matching TryRead<T> - the
        // client never has to parse twice, and nothing ever infers a packet
        // type from its size.
        public static bool TryParseEnvelope(ReadOnlyMemory<byte> utf8Json, out JsonDocument? document, out string type, out string? error)
        {
            document = null;
            type = string.Empty;
            error = null;

            try
            {
                document = JsonDocument.Parse(utf8Json);
            }
            catch (JsonException ex)
            {
                error = $"malformed JSON: {ex.Message}";
                return false;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "top-level value is not an object";
                document.Dispose();
                document = null;
                return false;
            }

            if (!document.RootElement.TryGetProperty(TypePropertyName, out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                error = $"missing or non-string '{TypePropertyName}' discriminator";
                document.Dispose();
                document = null;
                return false;
            }

            type = typeElement.GetString() ?? string.Empty;
            return true;
        }

        // Absent properties leave their field at its default. Inbound packets
        // are commands where a client legitimately fills three fields out of
        // fifty, so requiring every property on the wire would make the
        // protocol unusable by hand. A property that IS present but malformed
        // is a hard failure - that is a client bug, not an omission.
        public static bool TryRead<T>(JsonElement root, out T packet, out string? error) where T : unmanaged
        {
            packet = default;
            error = null;

            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref packet, 1));
            PacketField[] plan = Plan<T>.Fields;

            for (int i = 0; i < plan.Length; i++)
            {
                PacketField field = plan[i];
                if (!root.TryGetProperty(field.Name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (!TryReadField(field, element, bytes, out error))
                {
                    error = $"{typeof(T).Name}.{field.Name}: {error}";
                    packet = default;
                    return false;
                }
            }

            return true;
        }

        public static bool TryDeserialize<T>(ReadOnlyMemory<byte> utf8Json, out T packet, out string? error) where T : unmanaged
        {
            packet = default;

            if (!TryParseEnvelope(utf8Json, out JsonDocument? document, out string type, out error))
            {
                return false;
            }

            using (document)
            {
                string expected = DiscriminatorFor(typeof(T));
                if (type != expected)
                {
                    error = $"expected '{TypePropertyName}' of '{expected}', got '{type}'";
                    return false;
                }

                return TryRead(document!.RootElement, out packet, out error);
            }
        }

        private static bool TryReadField(PacketField field, JsonElement element, Span<byte> bytes, out string? error)
        {
            error = null;
            Span<byte> slice = bytes.Slice(field.Offset, field.Size);

            try
            {
                switch (field.Kind)
                {
                    case FieldKind.U8:
                        slice[0] = element.GetByte();
                        return true;
                    case FieldKind.I8:
                        slice[0] = unchecked((byte)element.GetSByte());
                        return true;
                    case FieldKind.U16:
                        MemoryMarshal.Write(slice, element.GetUInt16());
                        return true;
                    case FieldKind.I16:
                        MemoryMarshal.Write(slice, element.GetInt16());
                        return true;
                    case FieldKind.U32:
                        MemoryMarshal.Write(slice, element.GetUInt32());
                        return true;
                    case FieldKind.I32:
                        MemoryMarshal.Write(slice, element.GetInt32());
                        return true;
                    case FieldKind.U64:
                        MemoryMarshal.Write(slice, element.GetUInt64());
                        return true;
                    case FieldKind.I64:
                        MemoryMarshal.Write(slice, element.GetInt64());
                        return true;
                    case FieldKind.F32:
                    {
                        float value = element.ValueKind == JsonValueKind.String
                            ? float.Parse(element.GetString() ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture)
                            : element.GetSingle();
                        MemoryMarshal.Write(slice, value);
                        return true;
                    }
                    case FieldKind.F64:
                    {
                        double value = element.ValueKind == JsonValueKind.String
                            ? double.Parse(element.GetString() ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture)
                            : element.GetDouble();
                        MemoryMarshal.Write(slice, value);
                        return true;
                    }
                    case FieldKind.GuidValue:
                        MemoryMarshal.Write(slice, element.GetGuid());
                        return true;
                    case FieldKind.FixedBytes:
                    {
                        string encoded = element.GetString() ?? string.Empty;
                        byte[] decoded = Convert.FromBase64String(encoded);
                        if (decoded.Length > field.Size)
                        {
                            error = $"base64 decodes to {decoded.Length} bytes, exceeding the {field.Size}-byte fixed buffer";
                            return false;
                        }

                        // Zero-pad short input to the fixed capacity, exactly
                        // as the binary senders do - the paired *Length field
                        // is what says how much of it is real.
                        slice.Clear();
                        decoded.CopyTo(slice);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            {
                error = ex.Message;
                return false;
            }

            error = "unsupported field kind";
            return false;
        }
    }
}
