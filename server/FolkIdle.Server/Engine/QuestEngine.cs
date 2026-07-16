using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    // Modul: daily quest generation and progress tracking. Generation and
    // DB flush (EnsureAndLoadDailyQuestsAsync, UpsertDailyQuestProgressAsync)
    // operate on an already-open FolkIdleDbContext supplied by the caller
    // (StateCheckpointManager's login-hydration and periodic-flush
    // transactions respectively) - the same TransactionDedupEngine
    // convention, since quest rows must commit atomically alongside the
    // rest of a player's checkpointed state, not in a separately-opened
    // transaction that could partially apply.
    //
    // IncrementProgress is the one piece that genuinely runs inside the
    // 10 Hz tick (called from the monster-kill resolution and
    // CraftingCompletionQueue drain in SimulationEngine) - it is pure
    // struct field arithmetic against TickStatePayload's pre-allocated
    // QuestSlot0-2 fields, satisfying the "quest-tracking must use
    // pre-allocated buffers" constraint. It never touches the database;
    // QuestProgressDirty just marks the payload for the next periodic
    // flush, mirroring every other "cheap in-tick mutation, batched DB
    // write" field on TickStatePayload (RedisPendingGoldDelta, IsDirty).
    public static class QuestEngine
    {
        public const byte QuestTypeKillMonsters = 0;
        public const byte QuestTypeCraftItems = 1;

        private const int QuestSlotCount = 3;
        private const long SecondsPerUtcDay = 86400L;

        private const int KillQuestMinTarget = 10;
        private const int KillQuestTargetRange = 21; // 10..30 inclusive
        private const int CraftQuestMinTarget = 3;
        private const int CraftQuestTargetRange = 6; // 3..8 inclusive

        public static long GetUtcDateKey(long nowEpochSeconds) => nowEpochSeconds / SecondsPerUtcDay;

        // Modul: deterministic per-slot generation - the same
        // (playerId, dateKey, slot) triple always produces the same
        // (QuestType, TargetAmount) pair, so regenerating today's quests
        // (e.g. a retried transaction after a transient failure) can never
        // silently reshuffle what a player is already working toward mid-day.
        public static (byte QuestType, int TargetAmount) GenerateSlotQuest(long playerId, long dateKey, int slot)
        {
            uint hash = MurmurHash3.Hash64(playerId ^ (dateKey << 4) ^ slot, 0x9E3779B9U);
            byte questType = (byte)(hash % 2);
            int variance = questType == QuestTypeKillMonsters
                ? (int)((hash / 2) % KillQuestTargetRange)
                : (int)((hash / 2) % CraftQuestTargetRange);
            int baseTarget = questType == QuestTypeKillMonsters ? KillQuestMinTarget : CraftQuestMinTarget;
            return (questType, baseTarget + variance);
        }

        // Ensures today's 3 quest rows exist (regenerating if the stored
        // DateKeyUtc has rolled past UTC midnight, or if a player has no
        // rows at all), and returns them ordered by QuestSlot. Does not
        // call SaveChangesAsync itself - the caller's own transaction
        // commits this alongside everything else it is writing.
        public static async Task<DailyQuestRecord[]> EnsureAndLoadDailyQuestsAsync(FolkIdleDbContext dbContext, long playerId, long nowEpochSeconds)
        {
            long dateKey = GetUtcDateKey(nowEpochSeconds);

            var existing = await dbContext.DailyQuestRecords
                .Where(q => q.PlayerId == playerId)
                .ToListAsync();

            bool needsRegeneration = existing.Count != QuestSlotCount || existing.Any(q => q.DateKeyUtc != dateKey);
            if (!needsRegeneration)
            {
                return existing.OrderBy(q => q.QuestSlot).ToArray();
            }

            if (existing.Count > 0)
            {
                dbContext.DailyQuestRecords.RemoveRange(existing);
            }

            var fresh = new DailyQuestRecord[QuestSlotCount];
            for (int slot = 0; slot < QuestSlotCount; slot++)
            {
                (byte questType, int target) = GenerateSlotQuest(playerId, dateKey, slot);
                fresh[slot] = new DailyQuestRecord
                {
                    PlayerId = playerId,
                    QuestSlot = slot,
                    QuestType = questType,
                    TargetAmount = target,
                    CurrentProgress = 0,
                    RewardClaimed = false,
                    DateKeyUtc = dateKey
                };
            }

            dbContext.DailyQuestRecords.AddRange(fresh);
            return fresh;
        }

        // Copies EnsureAndLoadDailyQuestsAsync's result into the fixed
        // TickStatePayload quest fields - called once at login hydration.
        public static void ApplyToPayload(ref TickStatePayload payload, DailyQuestRecord[] quests, long dateKey)
        {
            payload.DailyQuestDateKeyUtc = dateKey;
            payload.QuestProgressDirty = false;

            foreach (var quest in quests)
            {
                switch (quest.QuestSlot)
                {
                    case 0:
                        payload.QuestSlot0Type = (byte)quest.QuestType;
                        payload.QuestSlot0Target = quest.TargetAmount;
                        payload.QuestSlot0Progress = quest.CurrentProgress;
                        payload.QuestSlot0Claimed = quest.RewardClaimed;
                        break;
                    case 1:
                        payload.QuestSlot1Type = (byte)quest.QuestType;
                        payload.QuestSlot1Target = quest.TargetAmount;
                        payload.QuestSlot1Progress = quest.CurrentProgress;
                        payload.QuestSlot1Claimed = quest.RewardClaimed;
                        break;
                    case 2:
                        payload.QuestSlot2Type = (byte)quest.QuestType;
                        payload.QuestSlot2Target = quest.TargetAmount;
                        payload.QuestSlot2Progress = quest.CurrentProgress;
                        payload.QuestSlot2Claimed = quest.RewardClaimed;
                        break;
                }
            }
        }

        // Zero-allocation live-tick progress increment. Matches every slot
        // whose QuestType equals questType and whose progress has not
        // already reached its target - pure struct field reads/writes, no
        // heap allocation, safe to call from the 10 Hz combat/crafting
        // completion sites.
        public static void IncrementProgress(ref TickStatePayload payload, byte questType, int amount)
        {
            if (payload.QuestSlot0Type == questType && payload.QuestSlot0Progress < payload.QuestSlot0Target)
            {
                payload.QuestSlot0Progress = Math.Min(payload.QuestSlot0Target, payload.QuestSlot0Progress + amount);
                payload.QuestProgressDirty = true;
            }
            if (payload.QuestSlot1Type == questType && payload.QuestSlot1Progress < payload.QuestSlot1Target)
            {
                payload.QuestSlot1Progress = Math.Min(payload.QuestSlot1Target, payload.QuestSlot1Progress + amount);
                payload.QuestProgressDirty = true;
            }
            if (payload.QuestSlot2Type == questType && payload.QuestSlot2Progress < payload.QuestSlot2Target)
            {
                payload.QuestSlot2Progress = Math.Min(payload.QuestSlot2Target, payload.QuestSlot2Progress + amount);
                payload.QuestProgressDirty = true;
            }
        }

        // Writes the tick-thread-owned progress fields back to the
        // DailyQuestRecords rows - called from StateCheckpointManager's
        // periodic FlushState alongside every other persisted field, never
        // from the 10 Hz tick. A stale DailyQuestDateKeyUtc (the player has
        // been online across a UTC midnight rollover without reconnecting)
        // is intentionally not regenerated here - EnsureAndLoadDailyQuestsAsync
        // at the next login is the one and only regeneration path, keeping
        // "when do quests reset" a single well-defined rule.
        public static async Task UpsertDailyQuestProgressAsync(FolkIdleDbContext dbContext, TickStatePayload state)
        {
            if (!state.QuestProgressDirty) return;

            var rows = await dbContext.DailyQuestRecords
                .Where(q => q.PlayerId == state.PlayerId && q.DateKeyUtc == state.DailyQuestDateKeyUtc)
                .ToListAsync();

            foreach (var row in rows)
            {
                switch (row.QuestSlot)
                {
                    case 0:
                        row.CurrentProgress = state.QuestSlot0Progress;
                        row.RewardClaimed = state.QuestSlot0Claimed;
                        break;
                    case 1:
                        row.CurrentProgress = state.QuestSlot1Progress;
                        row.RewardClaimed = state.QuestSlot1Claimed;
                        break;
                    case 2:
                        row.CurrentProgress = state.QuestSlot2Progress;
                        row.RewardClaimed = state.QuestSlot2Claimed;
                        break;
                }
            }
        }
    }
}
