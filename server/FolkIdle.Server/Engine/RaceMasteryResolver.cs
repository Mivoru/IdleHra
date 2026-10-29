namespace FolkIdle.Server.Engine
{
    // Modul 13: stateless, allocation-free passive-modifier lookups for Race
    // Mastery levels. Every method is a pure threshold check - callers pass in
    // whichever TickStatePayload.*MasteryLevel field is relevant and get back a
    // plain numeric modifier to fold into their own calculation. No lookup here
    // performs I/O, allocates, or mutates state.
    //
    // Fomoiri is intentionally absent: it does not exist anywhere in RaceIds or
    // the genetics/breeding system, so there is no mastery level to gate a
    // damage-reduction bonus on.
    public static class RaceMasteryResolver
    {
        public static int GetHumanXpBonusPct(int humanMasteryLevel)
        {
            if (humanMasteryLevel >= RaceBonusRegistry.HumanXpSecondLevel) return RaceBonusRegistry.HumanXpSecondPct;
            if (humanMasteryLevel >= RaceBonusRegistry.HumanXpFirstLevel) return RaceBonusRegistry.HumanXpFirstPct;
            return 0;
        }

        public static int GetHumanVaultBonusSlots(int humanMasteryLevel)
        {
            return humanMasteryLevel >= RaceBonusRegistry.HumanVaultLevel ? RaceBonusRegistry.HumanVaultSlots : 0;
        }

        public static float GetVilaCritBonusPct(int vilaMasteryLevel)
        {
            return vilaMasteryLevel >= RaceBonusRegistry.VilaCritLevel ? RaceBonusRegistry.VilaCritPoints : 0f;
        }

        public static float GetDraugrLifestealBonusPct(int draugrMasteryLevel)
        {
            if (draugrMasteryLevel >= RaceBonusRegistry.DraugrLifestealSecondLevel) return RaceBonusRegistry.DraugrLifestealSecondPct;
            if (draugrMasteryLevel >= RaceBonusRegistry.DraugrLifestealFirstLevel) return RaceBonusRegistry.DraugrLifestealFirstPct;
            return 0f;
        }

        public static float GetKoboldOreDuplicationBonusPct(int koboldMasteryLevel)
        {
            return koboldMasteryLevel >= RaceBonusRegistry.KoboldMiningLevel ? RaceBonusRegistry.KoboldMiningPoints : 0f;
        }

        public static float GetMoosleuteDoubleHarvestBonusPct(int moosleuteMasteryLevel)
        {
            return moosleuteMasteryLevel >= RaceBonusRegistry.MoosleuteHarvestLevel ? RaceBonusRegistry.MoosleuteHarvestPoints : 0f;
        }

        // The brief's example value (10 hours) is below the current 12-hour
        // universal baseline (OfflineSimulationEngine.MaxOfflineSeconds), which
        // would make this a downgrade rather than an extension. 18 hours is a
        // clearly-larger, tunable placeholder that honors the "extend AFK
        // timeout" intent instead.
        public static long GetVodnikExtendedOfflineSeconds(int vodnikMasteryLevel, long baseOfflineSeconds)
        {
            return vodnikMasteryLevel >= RaceBonusRegistry.VodnikOfflineLevel ? System.Math.Max(baseOfflineSeconds, RaceBonusRegistry.VodnikOfflineSeconds) : baseOfflineSeconds;
        }
    }
}
