namespace FolkIdle.Bots
{
    /// <summary>What a refusal depends on: when any of these moves, the refusal is forgotten.</summary>
    public readonly record struct RefusalConditions(int Level, int Location, int Region, int Deaths);

    /// <summary>
    /// The activities the server refused, and when the bot may try each again.
    ///
    /// Modul: A REFUSAL IS A COOLDOWN, NOT A BAN (night 2026-11-04, val-x20).
    /// A refusal used to hold until the character's level, location or region
    /// changed. eagerkit11 had one fight refused at level 3 (halt None, result 0
    /// - the activity simply was not confirmed in eight game seconds, which is
    /// 0.4 wall seconds at x20), then died twice to each of the other four
    /// monsters in reach and set them aside "until level 4". Level 4 needed a
    /// fight, so the bot stood idle for 71 of its 72 game hours, and so did
    /// seven more bots of that run. Now a refusal holds for a cooldown that
    /// starts at one game minute and doubles per repeat (at most two game
    /// hours), and a death, a level, a location or a region forgets it at
    /// once. After <see cref="MaxStrikes"/> refusals under the same conditions
    /// the bot stops retrying until something changes - a refusal that is real
    /// is not hammered for ever.
    /// </summary>
    public sealed class RefusalMemory
    {
        public const int MaxStrikes = 6;
        public static readonly double FirstCooldownHours = 1.0 / 60;
        public static readonly double MaxCooldownHours = 2.0;

        private sealed record Entry(RefusalConditions At, int Strikes, double UntilHours);

        private readonly Dictionary<long, Entry> _entries = new();

        /// <summary>Records a refusal; returns the cooldown in game hours, or null when the bot now waits for the conditions to change.</summary>
        public double? Mark(long key, RefusalConditions at, double nowHours)
        {
            int strikes = _entries.TryGetValue(key, out var old) && old.At == at ? old.Strikes + 1 : 1;
            if (strikes >= MaxStrikes)
            {
                _entries[key] = new Entry(at, strikes, double.PositiveInfinity);
                return null;
            }
            double cooldown = CooldownHours(strikes);
            _entries[key] = new Entry(at, strikes, nowHours + cooldown);
            return cooldown;
        }

        /// <summary>One game minute, doubled for each repeat, at most two game hours.</summary>
        public static double CooldownHours(int strikes)
            => Math.Min(MaxCooldownHours, FirstCooldownHours * Math.Pow(2, Math.Max(0, strikes - 1)));

        /// <summary>True while the refusal still holds: same conditions and the cooldown not over.</summary>
        public bool IsRefused(long key, RefusalConditions now, double nowHours)
        {
            if (!_entries.TryGetValue(key, out var e)) return false;
            if (e.At != now)
            {
                _entries.Remove(key);
                return false;
            }
            // The strike count stays for the next refusal under these conditions.
            return nowHours < e.UntilHours;
        }

        public int Strikes(long key) => _entries.TryGetValue(key, out var e) ? e.Strikes : 0;

        public void Clear() => _entries.Clear();
    }
}
