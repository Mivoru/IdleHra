using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Engine
{
    public class LootDrop
    {
        public string ItemId { get; set; } = string.Empty;
        public int Weight { get; set; }
        public int MinQuantity { get; set; }
        public int MaxQuantity { get; set; }
    }

    public class LootTableEngine
    {
        private readonly Dictionary<long, List<LootDrop>> _activityDropTables = new();
        private readonly Random _random = new Random();

        public LootTableEngine()
        {
            _activityDropTables[1] = new List<LootDrop>
            {
                new LootDrop { ItemId = "wood", Weight = 50, MinQuantity = 1, MaxQuantity = 3 },
                new LootDrop { ItemId = "rare_wood", Weight = 5, MinQuantity = 1, MaxQuantity = 1 },
                new LootDrop { ItemId = "stone", Weight = 45, MinQuantity = 1, MaxQuantity = 2 }
            };
        }

        public LootDrop? EvaluateDrop(long activityId)
        {
            if (!_activityDropTables.TryGetValue(activityId, out var drops))
            {
                return null;
            }

            int totalWeight = drops.Sum(d => d.Weight);
            if (totalWeight <= 0) return null;

            int roll = _random.Next(0, totalWeight);
            int currentWeight = 0;

            foreach (var drop in drops)
            {
                currentWeight += drop.Weight;
                if (roll < currentWeight)
                {
                    return drop;
                }
            }

            return null;
        }
    }
}
