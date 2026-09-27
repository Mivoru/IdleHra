using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 46 (audit item 8, "8a - measure"). Nothing had ever measured how
    /// big a JSON state frame actually is, or how long serializing one takes -
    /// the go/no-go call in docs/TASK_BOARD.md #46 (close the item outright,
    /// or add WebSocket compression and maybe deltas) needs a real number, not
    /// a guess. CLAUDE.md: "a number a test PRINTS is not a number a test
    /// CHECKS" - so the size assertion below is a ratchet, not decoration.
    /// </summary>
    public class StateFrameSizeTests
    {
        /// <summary>
        /// Builds a StateUpdatePacket with every field set to a representative
        /// non-zero, multi-digit value, by reflection over the struct's own
        /// fields rather than a hand-written 230-field initializer. This is a
        /// test fixture, not the wire itself, so it is not the "never
        /// hand-write a wire type" trap CLAUDE.md warns about - but the same
        /// drift risk applies to any list of 230 field names copied out by
        /// hand, so reflection is used anyway: a new field is picked up and
        /// filled automatically instead of silently defaulting to zero.
        ///
        /// A default-constructed packet understates real size. Every numeric
        /// field on this wire serializes as a JSON number with as many bytes
        /// as it has digits, and a fresh payload's zeros collapse every one of
        /// them to a single "0". A mid-to-late-game character - levelled,
        /// geared, in a village, in a guild - has multi-digit gold, XP,
        /// ticks, ids and durations in nearly every field, so representative
        /// non-zero values are a much closer stand-in for what a real session
        /// broadcasts than an all-zero packet would be.
        /// </summary>
        internal static StateUpdatePacket BuildRealisticMidGamePacket()
        {
            object boxed = new StateUpdatePacket();

            // Modul: SetValue on a BOXED struct mutates that same box in
            // place - a documented reflection property, not the copy a
            // property setter would produce. Sorted by name only for a
            // stable, reproducible byte count across runs and machines;
            // FieldInfo enumeration order is not otherwise guaranteed.
            FieldInfo[] fields = typeof(StateUpdatePacket)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToArray();

            int seed = 0;
            foreach (FieldInfo field in fields)
            {
                seed++;
                field.SetValue(boxed, RepresentativeValue(field.FieldType, seed));
            }

            return (StateUpdatePacket)boxed;
        }

        private static object RepresentativeValue(Type type, int seed)
        {
            if (type == typeof(byte)) return (byte)(1 + seed % 200);
            if (type == typeof(ushort)) return (ushort)(1000 + (seed * 37) % 60000);
            if (type == typeof(uint)) return (uint)(100000 + (long)seed * 9973 % 4_000_000_000L);
            if (type == typeof(int)) return 100000 + seed * 7919;
            if (type == typeof(long)) return 1_000_000_000L + (long)seed * 999_999_937L;
            if (type == typeof(float)) return 123.456f + seed;
            if (type == typeof(double)) return 123456.789 + seed;
            if (type == typeof(Guid)) return Guid.Parse($"{seed:D8}-1234-5678-9abc-def012345678");

            // Modul: StateUpdatePacket is currently exactly seven scalar
            // types (byte/ushort/uint/int/long/float/double) plus Guid - see
            // the codec's own field-kind switch in PacketJsonCodec. A new
            // field type this generator does not know would otherwise
            // default to zero and silently understate the measured size,
            // which is the one failure mode this whole test exists to avoid.
            throw new InvalidOperationException(
                $"StateUpdatePacket gained a field of type {type} that this size test's " +
                "representative-value generator does not fill. Add a case here before " +
                "trusting the byte count below.");
        }

        // Modul: task 46 (8a). Measured at write time: a realistic mid-game
        // packet serializes to 6,205 bytes of JSON (see the trace output) -
        // nearly 8x the 801-byte binary layout, and well past the ~2 KB this
        // codec's own file header once estimated. Field NAMES, not values,
        // are most of that: an all-zero packet (every value collapsed to a
        // single "0") still serializes to 5,186 bytes, because 230
        // PascalCase property names plus JSON punctuation is a fixed cost
        // paid before a single stat is written. 7,800 is roughly 25% above
        // the realistic measurement - a RATCHET against an unnoticed
        // regression (a new field, or a field whose values grew), not the
        // real budget. The real threshold is bandwidth
        // (~150 KB/player/minute, docs/TASK_BOARD.md #46) computed from this
        // measurement in the task board write-up, not from this constant -
        // and at 6,205 bytes/frame and one frame/second for an active
        // player, that threshold is already crossed (see the task board
        // entry for the arithmetic).
        private const int MaxRealisticJsonBytes = 7800;

        [Fact]
        public void RealisticMidGamePacket_JsonSizeIsMeasuredAndBounded()
        {
            StateUpdatePacket packet = BuildRealisticMidGamePacket();

            byte[] json = PacketJsonCodec.SerializeToUtf8(ref packet);
            int binarySize = Marshal.SizeOf<StateUpdatePacket>();

            // The all-zero comparison isolates how much of the frame is
            // field-name overhead versus actual gameplay values - see the
            // Modul comment above the ratchet constant.
            var zero = new StateUpdatePacket();
            byte[] zeroJson = PacketJsonCodec.SerializeToUtf8(ref zero);

            Console.WriteLine(
                $"Realistic mid-game StateUpdatePacket: {json.Length} bytes JSON, {binarySize} bytes binary. " +
                $"All-zero packet for comparison: {zeroJson.Length} bytes JSON.");

            Assert.True(json.Length > binarySize,
                "A realistic (non-zero, multi-digit) packet should serialize larger than the " +
                "raw struct - PascalCase field names and JSON punctuation alone add more than " +
                "the binary layout, so a JSON size at or below the binary size would mean this " +
                "generator left fields at their zero default.");

            Assert.True(json.Length <= MaxRealisticJsonBytes,
                $"Realistic StateUpdatePacket JSON grew to {json.Length} bytes, over the " +
                $"{MaxRealisticJsonBytes}-byte ratchet. If this is a deliberate new field, " +
                "re-measure, move this constant in the same PR, and update the bandwidth " +
                "estimate recorded in docs/TASK_BOARD.md #46.");
        }

        // Modul: task 46 (8a). Serialize time is measured AND asserted, but
        // against a deliberately generous bound - a single Utf8JsonWriter
        // pass over ~230 fields is expected to be tens of microseconds, and a
        // few thousand would already be a dramatic, worth-investigating
        // regression, so 5ms leaves wide margin for CI noise while still
        // catching an accidental O(n^2) or a blocking call added to this
        // path. The median (not the first sample) is what is asserted,
        // because JIT warmup and the writer's own initial buffer growth cost
        // more on the very first calls than on the steady state that matters
        // in production.
        [Fact]
        public void RealisticMidGamePacket_SerializeTimeIsMeasuredAndGenerouslyBounded()
        {
            StateUpdatePacket packet = BuildRealisticMidGamePacket();

            for (int i = 0; i < 50; i++)
            {
                _ = PacketJsonCodec.SerializeToUtf8(ref packet);
            }

            const int iterations = 2000;
            var samplesMicroseconds = new long[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long startTimestamp = Stopwatch.GetTimestamp();
                _ = PacketJsonCodec.SerializeToUtf8(ref packet);
                samplesMicroseconds[i] = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000L / Stopwatch.Frequency;
            }

            Array.Sort(samplesMicroseconds);
            long medianMicroseconds = samplesMicroseconds[iterations / 2];
            long p99Microseconds = samplesMicroseconds[(int)(iterations * 0.99)];

            Console.WriteLine(
                $"SerializeToUtf8 over {iterations} runs on a realistic mid-game packet: " +
                $"median {medianMicroseconds}us, p99 {p99Microseconds}us.");

            const long generousMedianBoundMicroseconds = 5000;
            Assert.True(medianMicroseconds < generousMedianBoundMicroseconds,
                $"Median SerializeToUtf8 time was {medianMicroseconds}us, over the generous " +
                $"{generousMedianBoundMicroseconds}us bound - that is a real regression, not noise.");
        }
    }
}
