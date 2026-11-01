using System.Text.Json;

namespace FolkIdle.Bots
{
    /// <summary>
    /// Tolerant reads of REST answers. The bot reads the server's JSON by
    /// property name rather than mirroring DTOs (see BotClient), so a missing
    /// or differently-typed property reads as the default instead of throwing
    /// in the middle of a decision.
    /// </summary>
    internal static class J
    {
        public static int Int(this JsonElement e, string name, int fallback = 0)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

        public static long Long(this JsonElement e, string name, long fallback = 0)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : fallback;

        public static double Num(this JsonElement e, string name, double fallback = 0)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

        public static bool Bool(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        public static string Str(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        public static IEnumerable<JsonElement> Items(this JsonElement e)
            => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();

        public static JsonElement Obj(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

        public static bool Has(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
