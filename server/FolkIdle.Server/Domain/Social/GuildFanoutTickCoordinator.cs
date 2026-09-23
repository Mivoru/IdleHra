using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The tick thread's five guild-scoped fan-out hand-offs. All five READ
    /// _guildMembersIndex and write through refs into _activePlayers for
    /// every online member of a guild; none of them may write the index -
    /// see the plan's _guildMembersIndex ownership ruling (session lifecycle
    /// owns it; guild coordinators borrow read access). The writer
    /// (GuildMembershipChangeQueue) stays in SimulationEngine.EngineLoop and
    /// is out of scope here.
    /// </summary>
    internal static class GuildFanoutTickCoordinator
    {
        // Modul: Guild War scoreboard sync. Fans one authoritative
        // per-guild snapshot out to every online member of that guild
        // via the tick-thread-owned guild index, so a scoreboard costs
        // one query per warring guild rather than one per member.
        internal static void DrainWarScoreboard(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            IReadOnlyDictionary<long, List<long>> guildMembersIndex)
        {
            while (registry.GuildWarScoreboardQueue.TryDequeue(out var warScoreboard))
            {
                if (!guildMembersIndex.TryGetValue(warScoreboard.GuildId, out var warMembers))
                {
                    continue;
                }

                for (int memberIndex = 0; memberIndex < warMembers.Count; memberIndex++)
                {
                    ref var memberPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, warMembers[memberIndex]);
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref memberPayload))
                    {
                        continue;
                    }

                    // Modul: Guild War scoreboard sync. A concluded war
                    // clears rather than freezing its final score on screen.
                    // The client decides "a war is on" from
                    // ActiveGuildWarId > 0, so that has to go to zero too or
                    // the panel keeps rendering a finished match as live.
                    if (warScoreboard.WarEnded)
                    {
                        memberPayload.ActiveGuildWarId = 0L;
                        memberPayload.GuildCombatVanguardPoints = 0;
                        memberPayload.GuildProductionLogisticsPoints = 0;
                        memberPayload.GuildGatheringSupplyChainPoints = 0;
                        memberPayload.EnemyCombatVanguardPoints = 0;
                        memberPayload.EnemyProductionLogisticsPoints = 0;
                        memberPayload.EnemyGatheringSupplyChainPoints = 0;
                        memberPayload.CachedWarMultiplier = 0f;
                        memberPayload.IsDirty = true;
                        continue;
                    }

                    memberPayload.GuildCombatVanguardPoints = warScoreboard.OurCombatVanguardPoints;
                    memberPayload.GuildProductionLogisticsPoints = warScoreboard.OurProductionLogisticsPoints;
                    memberPayload.GuildGatheringSupplyChainPoints = warScoreboard.OurGatheringSupplyChainPoints;
                    memberPayload.EnemyCombatVanguardPoints = warScoreboard.EnemyCombatVanguardPoints;
                    memberPayload.EnemyProductionLogisticsPoints = warScoreboard.EnemyProductionLogisticsPoints;
                    memberPayload.EnemyGatheringSupplyChainPoints = warScoreboard.EnemyGatheringSupplyChainPoints;
                    memberPayload.CachedWarMultiplier = warScoreboard.ScoreShare;
                    memberPayload.IsDirty = true;
                }
            }
        }

        internal static void DrainGuildUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            IReadOnlyDictionary<long, List<long>> guildMembersIndex)
        {
            while (registry.GuildUpdateQueue.TryDequeue(out var guildUpdate))
            {
                // Real-time updates for guild members - O(guild_size)
                // via _guildMembersIndex instead of O(active_player_count).
                if (guildMembersIndex.TryGetValue(guildUpdate.GuildId, out var guildUpdateMembers))
                {
                    foreach (long memberId in guildUpdateMembers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, memberId);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            if (guildUpdate.IsMining)
                            {
                                currentPayload.CachedMiningMonolithLevel = guildUpdate.NewLevel;
                            }
                            else
                            {
                                currentPayload.CachedWoodcuttingMonolithLevel = guildUpdate.NewLevel;
                            }
                            currentPayload.IsDirty = true;
                        }
                    }
                }
            }
        }

        internal static void DrainLogisticsDepotUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            IReadOnlyDictionary<long, List<long>> guildMembersIndex)
        {
            while (registry.GuildLogisticsDepotUpdateQueue.TryDequeue(out var depotNotif))
            {
                if (guildMembersIndex.TryGetValue(depotNotif.GuildId, out var depotMembers))
                {
                    foreach (long memberId in depotMembers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, memberId);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            currentPayload.GuildLogisticsCurrentStock = depotNotif.CurrentStock;
                            currentPayload.GuildLogisticsTargetRequirement = depotNotif.TargetRequirement;
                            currentPayload.CachedGuildLogisticsLevel = depotNotif.Level;
                        }
                    }
                }
            }
        }

        internal static void DrainCombatSimulationUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            IReadOnlyDictionary<long, List<long>> guildMembersIndex)
        {
            while (registry.GuildCombatSimulationUpdateQueue.TryDequeue(out var combatNotif))
            {
                // Two guilds are in this match - a player's fixed
                // per-session GuildId can only ever match one of them,
                // so no dedup is needed when both index lookups happen
                // to return non-empty lists.
                if (guildMembersIndex.TryGetValue(combatNotif.AttackingGuildId, out var attackingMembers))
                {
                    foreach (long memberId in attackingMembers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, memberId);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            currentPayload.CombatSimulationMatchId = combatNotif.MatchId;
                            currentPayload.CombatSimulationTurnCounter = combatNotif.TurnCounter;
                            currentPayload.CombatSimulationDamageDelta = combatNotif.DamageDelta;
                        }
                    }
                }

                if (combatNotif.DefendingGuildId != combatNotif.AttackingGuildId &&
                    guildMembersIndex.TryGetValue(combatNotif.DefendingGuildId, out var defendingMembers))
                {
                    foreach (long memberId in defendingMembers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, memberId);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            currentPayload.CombatSimulationMatchId = combatNotif.MatchId;
                            currentPayload.CombatSimulationTurnCounter = combatNotif.TurnCounter;
                            currentPayload.CombatSimulationDamageDelta = combatNotif.DamageDelta;
                        }
                    }
                }
            }
        }

        internal static void DrainRaidBossUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            IReadOnlyDictionary<long, List<long>> guildMembersIndex)
        {
            while (registry.GuildRaidBossUpdateQueue.TryDequeue(out var raidNotif))
            {
                if (guildMembersIndex.TryGetValue(raidNotif.GuildId, out var raidMembers))
                {
                    foreach (long memberId in raidMembers)
                    {
                        ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, memberId);
                        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                        {
                            currentPayload.CachedGuildRaidTier = raidNotif.RaidTier;
                            currentPayload.CachedGuildRaidBossCurrentHp = raidNotif.RaidBossCurrentHp;
                            currentPayload.CachedGuildRaidBossMaxHp = raidNotif.RaidBossMaxHp;
                        }
                    }
                }
            }
        }
    }
}
