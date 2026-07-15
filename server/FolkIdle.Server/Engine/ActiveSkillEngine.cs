using System;
using System.Runtime.InteropServices;

namespace FolkIdle.Server.Engine
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SkillDefinition
    {
        public int SkillId;
        public int ManaCost;
        public int CooldownMs;
        public int DamageMultiplierPct;
        public int RequiredSkillPointCost;
    }

    // Modul: struct-based active skill registry, mirroring ContentRegistry's
    // MonsterDefinition/ItemDefinition convention (fixed-layout struct array,
    // no allocation on lookup). Four skills map directly onto the action bar
    // hotkeys (1/2/3/4) described in the Active Skill Tree task - unlocking is
    // gated by AvailableSkillPoints (see PlayerRecord/TickStatePayload),
    // casting is gated by CurrentMana and a per-skill cooldown timestamp (see
    // SimulationEngine's RequestCastSkill handler and GetSkillCooldownExpiry/
    // SetSkillCooldownExpiry).
    public static class ActiveSkillEngine
    {
        public const int MaxSkillId = 4;
        public const int BaseMaxMana = 100;
        public const int MaxManaPerLevel = 2;
        public const int ManaRegenPerTick = 1;

        private static readonly SkillDefinition[] _skills = new SkillDefinition[]
        {
            new SkillDefinition { SkillId = 1, ManaCost = 10, CooldownMs = 3000, DamageMultiplierPct = 150, RequiredSkillPointCost = 1 },
            new SkillDefinition { SkillId = 2, ManaCost = 20, CooldownMs = 6000, DamageMultiplierPct = 200, RequiredSkillPointCost = 1 },
            new SkillDefinition { SkillId = 3, ManaCost = 30, CooldownMs = 10000, DamageMultiplierPct = 300, RequiredSkillPointCost = 2 },
            new SkillDefinition { SkillId = 4, ManaCost = 50, CooldownMs = 20000, DamageMultiplierPct = 500, RequiredSkillPointCost = 3 },
        };

        public static ReadOnlySpan<SkillDefinition> Skills => _skills;

        public static bool TryGetSkill(int skillId, out SkillDefinition skill)
        {
            for (int i = 0; i < _skills.Length; i++)
            {
                if (_skills[i].SkillId == skillId)
                {
                    skill = _skills[i];
                    return true;
                }
            }

            skill = default;
            return false;
        }

        public static int ComputeMaxMana(int level)
        {
            return BaseMaxMana + level * MaxManaPerLevel;
        }

        public static bool IsSkillUnlocked(uint unlockedSkillsBitmask, int skillId)
        {
            if (skillId < 1 || skillId > MaxSkillId)
            {
                return false;
            }

            return (unlockedSkillsBitmask & (1u << (skillId - 1))) != 0;
        }

        public static uint WithSkillUnlocked(uint unlockedSkillsBitmask, int skillId)
        {
            return unlockedSkillsBitmask | (1u << (skillId - 1));
        }

        // Modul: cooldown expiry timestamps live as four named fields on
        // TickStatePayload (Skill1CooldownExpiresAtMs..Skill4CooldownExpiresAtMs)
        // rather than a fixed array, matching this codebase's established
        // per-slot-field convention (see Food1_ItemId/Food2_ItemId/Food3_ItemId)
        // instead of introducing an unsafe fixed buffer into an otherwise
        // plain struct that is copied by value throughout the engine.
        public static long GetSkillCooldownExpiresAtMs(in TickStatePayload payload, int skillId)
        {
            switch (skillId)
            {
                case 1: return payload.Skill1CooldownExpiresAtMs;
                case 2: return payload.Skill2CooldownExpiresAtMs;
                case 3: return payload.Skill3CooldownExpiresAtMs;
                case 4: return payload.Skill4CooldownExpiresAtMs;
                default: return 0L;
            }
        }

        public static void SetSkillCooldownExpiresAtMs(ref TickStatePayload payload, int skillId, long expiresAtMs)
        {
            switch (skillId)
            {
                case 1: payload.Skill1CooldownExpiresAtMs = expiresAtMs; break;
                case 2: payload.Skill2CooldownExpiresAtMs = expiresAtMs; break;
                case 3: payload.Skill3CooldownExpiresAtMs = expiresAtMs; break;
                case 4: payload.Skill4CooldownExpiresAtMs = expiresAtMs; break;
            }
        }
    }
}
