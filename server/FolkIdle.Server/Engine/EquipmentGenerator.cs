using System;
using FolkIdle.Server.Models;
using System.Text.Json;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public static class EquipmentGenerator
    {
        private const uint ServerSecretSeed = 0xA73F9B2C;

        public static EquipmentInstance GenerateEquipment(long playerId, uint tickToken, CraftingRecipe recipe, uint slotIndex)
        {
            uint seed = tickToken ^ ServerSecretSeed ^ (uint)(playerId & 0xFFFFFFFF) ^ slotIndex;

            int attack = CalculateFlatStat(10, recipe.TierIndex, ref seed);
            int defense = CalculateFlatStat(5, recipe.TierIndex, ref seed);
            int crit = CalculatePercentageStat(100, 50, recipe.TierIndex, ref seed);
            int luck = CalculatePercentageStat(50, 20, recipe.TierIndex, ref seed);

            var affixes = new Dictionary<string, int>
            {
                { "1", attack },
                { "2", defense },
                { "3", crit },
                { "4", luck }
            };

            return new EquipmentInstance
            {
                BaseItemId = recipe.ResultBaseItemId,
                PlayerId = playerId,
                QualityTier = recipe.TierIndex,
                AffixPayload = JsonSerializer.Serialize(affixes),
                IsAffixLocked = false
            };
        }

        private static int CalculateFlatStat(int baseValue, int tierIndex, ref uint state)
        {
            double scale = Math.Pow(1.45, tierIndex);
            int max = (int)(baseValue * scale * 1.2);
            int min = (int)(baseValue * scale * 0.8);
            if (min < 1) min = 1;
            if (max <= min) max = min + 1;
            
            uint roll = NextXorshift32(ref state);
            return min + (int)(roll % (uint)(max - min + 1));
        }

        private static int CalculatePercentageStat(int baseValue, int modifierScale, int tierIndex, ref uint state)
        {
            int max = baseValue + (modifierScale * tierIndex);
            int min = max / 2;
            if (min < 1) min = 1;
            if (max <= min) max = min + 1;

            uint roll = NextXorshift32(ref state);
            int rolledValue = min + (int)(roll % (uint)(max - min + 1));
            return Math.Min(rolledValue, 10000);
        }

        private static uint NextXorshift32(ref uint state)
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }
    }
}
