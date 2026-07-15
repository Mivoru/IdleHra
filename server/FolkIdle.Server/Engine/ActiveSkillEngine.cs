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

        private static SkillDefinition[] _skills = Array.Empty<SkillDefinition>();

        public static ReadOnlySpan<SkillDefinition> Skills => _skills;

        private sealed class SkillJson
        {
            public int SkillId { get; set; }
            public int ManaCost { get; set; }
            public int CooldownMs { get; set; }
            public int DamageMultiplierPct { get; set; }
            public int RequiredSkillPointCost { get; set; }
        }

        // Modul: parses server/GameData/skills.json into the flat _skills
        // array, replacing what used to be a hardcoded 4-entry C# array
        // literal - see ContentRegistry.Initialize for the identical
        // local-build/atomic-commit/throw-on-failure design this mirrors.
        // Requires exactly MaxSkillId entries with SkillId values exactly
        // {1, 2, 3, 4} - GetSkillCooldownExpiresAtMs/SetSkillCooldownExpiresAtMs
        // switch on skillId 1-4 to select one of TickStatePayload's four
        // dedicated cooldown fields, so any other SkillId would silently
        // no-op there instead of failing loudly, which is exactly the kind
        // of corrupted-content bug this validation exists to catch at boot
        // instead of at runtime.
        public static void Initialize(string? gameDataDirectory = null)
        {
            string dir = gameDataDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "GameData");

            if (!System.IO.Directory.Exists(dir))
            {
                throw new InvalidOperationException($"ActiveSkillEngine.Initialize: GameData directory not found at '{dir}'.");
            }

            string path = System.IO.Path.Combine(dir, "skills.json");
            if (!System.IO.File.Exists(path))
            {
                throw new InvalidOperationException($"ActiveSkillEngine.Initialize: required content file 'skills.json' was not found at '{path}'.");
            }

            string text = System.IO.File.ReadAllText(path);
            System.Collections.Generic.List<SkillJson>? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<SkillJson>>(text);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"ActiveSkillEngine.Initialize: 'skills.json' contains malformed JSON: {ex.Message}", ex);
            }

            if (parsed == null || parsed.Count == 0)
            {
                throw new InvalidOperationException("ActiveSkillEngine.Initialize: 'skills.json' parsed to null or an empty list.");
            }

            if (parsed.Count != MaxSkillId)
            {
                throw new InvalidOperationException($"ActiveSkillEngine.Initialize: 'skills.json' must contain exactly {MaxSkillId} entries, found {parsed.Count}.");
            }

            var newSkills = new SkillDefinition[MaxSkillId];
            var seenSkillIds = new bool[MaxSkillId];
            foreach (SkillJson s in parsed)
            {
                if (s.SkillId < 1 || s.SkillId > MaxSkillId)
                {
                    throw new InvalidOperationException($"ActiveSkillEngine.Initialize: 'skills.json' has a SkillId ({s.SkillId}) outside the required range 1..{MaxSkillId}.");
                }
                if (seenSkillIds[s.SkillId - 1])
                {
                    throw new InvalidOperationException($"ActiveSkillEngine.Initialize: 'skills.json' has a duplicate SkillId ({s.SkillId}).");
                }
                if (s.ManaCost <= 0 || s.CooldownMs <= 0 || s.DamageMultiplierPct <= 0 || s.RequiredSkillPointCost <= 0)
                {
                    throw new InvalidOperationException($"ActiveSkillEngine.Initialize: 'skills.json' SkillId={s.SkillId} has a non-positive ManaCost, CooldownMs, DamageMultiplierPct, or RequiredSkillPointCost.");
                }

                seenSkillIds[s.SkillId - 1] = true;
                newSkills[s.SkillId - 1] = new SkillDefinition
                {
                    SkillId = s.SkillId,
                    ManaCost = s.ManaCost,
                    CooldownMs = s.CooldownMs,
                    DamageMultiplierPct = s.DamageMultiplierPct,
                    RequiredSkillPointCost = s.RequiredSkillPointCost
                };
            }

            _skills = newSkills;
        }

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
