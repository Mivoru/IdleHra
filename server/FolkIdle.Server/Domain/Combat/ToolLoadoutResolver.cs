using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// What a character's equipped tools are worth.
    ///
    /// Modul: tools used to be stackable materials, so "which tool" was
    /// answered by scanning the chest for the best BaseId a player owned - a
    /// stack cannot carry a rarity or an affix, so every axe of a given wood
    /// was identical to every other one. Now a tool is an EquipmentInstance in
    /// its own slot, exactly like a sword, and it brings its rolled affixes
    /// with it.
    ///
    /// Resolved once when gear changes rather than on the 10Hz tick: the tick
    /// needs a tier and three percentages, and reading three equipment rows and
    /// parsing three affix payloads sixty thousand times a minute to learn the
    /// same six numbers would be absurd.
    /// </summary>
    public readonly struct ToolLoadout
    {
        public readonly byte AxeTier;
        public readonly byte PickaxeTier;
        public readonly byte RodTier;
        public readonly ushort GatherSpeedPct;
        public readonly ushort GatherYieldPct;
        public readonly ushort RareFindPct;

        public ToolLoadout(byte axeTier, byte pickaxeTier, byte rodTier,
            ushort gatherSpeedPct, ushort gatherYieldPct, ushort rareFindPct)
        {
            AxeTier = axeTier;
            PickaxeTier = pickaxeTier;
            RodTier = rodTier;
            GatherSpeedPct = gatherSpeedPct;
            GatherYieldPct = gatherYieldPct;
            RareFindPct = rareFindPct;
        }

        public static readonly ToolLoadout Empty = new(0, 0, 0, 0, 0, 0);
    }

    public static class ToolLoadoutResolver
    {
        // The three affix ids a tool can roll - see AffixRegistry's tool block.
        // Read by id rather than by index so re-ordering the definition table
        // cannot silently reassign a bonus to a different stat.
        public const string GatherSpeedAffix = "gather_speed_pct";
        public const string GatherYieldAffix = "gather_yield_pct";
        public const string RareFindAffix = "gather_rare_find_pct";

        /// <summary>
        /// Resolves from equipment rows already in memory, so callers that have
        /// loaded them (hydration, the equip path) do not query again.
        /// </summary>
        public static ToolLoadout Resolve(CharacterRecord? character, IReadOnlyDictionary<long, EquipmentInstance> byInstanceId)
        {
            if (character == null || byInstanceId == null)
            {
                return ToolLoadout.Empty;
            }

            byte axeTier = 0, pickaxeTier = 0, rodTier = 0;
            int speed = 0, yield = 0, rareFind = 0;

            void Take(long? instanceId, ref byte tier)
            {
                if (!instanceId.HasValue) return;
                if (!byInstanceId.TryGetValue(instanceId.Value, out var instance)) return;

                tier = (byte)ContentRegistry.GetToolTier(instance.BaseItemId);
                AccumulateAffixes(instance.AffixPayload, ref speed, ref yield, ref rareFind);
            }

            Take(character.EquippedAxeId, ref axeTier);
            Take(character.EquippedPickaxeId, ref pickaxeTier);
            Take(character.EquippedRodId, ref rodTier);

            return new ToolLoadout(
                axeTier, pickaxeTier, rodTier,
                ClampToUShort(speed), ClampToUShort(yield), ClampToUShort(rareFind));
        }

        // Affix payloads are a flat object of key -> magnitude, where the key
        // carries a stack suffix and a rarity suffix ("gather_yield_pct#2@4").
        // Both have to come off before the id is recognisable - a reader that
        // forgets is a reader that silently scores every stacked or
        // rarity-tagged affix as zero.
        private static void AccumulateAffixes(string? affixPayload, ref int speed, ref int yield, ref int rareFind)
        {
            if (string.IsNullOrWhiteSpace(affixPayload)) return;

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(affixPayload);
            }
            catch (Exception)
            {
                return;
            }

            if (parsed is not JsonObject affixes) return;

            foreach (var pair in affixes)
            {
                if (pair.Value is not JsonValue value) continue;
                if (!value.TryGetValue(out int magnitude)) continue;

                string id = AffixRegistry.StripStackSuffix(pair.Key);
                if (id == GatherSpeedAffix) speed += magnitude;
                else if (id == GatherYieldAffix) yield += magnitude;
                else if (id == RareFindAffix) rareFind += magnitude;
            }
        }

        private static ushort ClampToUShort(int value)
        {
            if (value <= 0) return 0;
            return value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
        }
    }
}
