namespace FolkIdle.Client.UI
{
    public static class FastStringCache
    {
        public const int TaxBracketLow = 0;
        public const int TaxBracketMid = 1;
        public const int TaxBracketHigh = 2;

        public const int WorldBossMaxAttempts = 3;

        public const int TimeUnitDays = 0;
        public const int TimeUnitHours = 1;
        public const int TimeUnitMinutes = 2;
        public const int TimeUnitSeconds = 3;

        private static readonly string[] _timeUnitLabels = new string[4]
        {
            "d",
            "h",
            "m",
            "s"
        };

        private static readonly string[] _taxBracketLabels = new string[3]
        {
            "6%",
            "10%",
            "18%"
        };

        private static readonly string[] _worldBossRemainingRunsLabels = new string[4]
        {
            "0/3",
            "1/3",
            "2/3",
            "3/3"
        };

        public static string GetTaxBracketLabel(int tierIndex)
        {
            if (tierIndex < 0) tierIndex = 0;
            else if (tierIndex >= _taxBracketLabels.Length) tierIndex = _taxBracketLabels.Length - 1;

            return _taxBracketLabels[tierIndex];
        }

        public static string GetWorldBossRemainingRunsLabel(int remainingRuns)
        {
            if (remainingRuns < 0) remainingRuns = 0;
            else if (remainingRuns >= _worldBossRemainingRunsLabels.Length) remainingRuns = _worldBossRemainingRunsLabels.Length - 1;

            return _worldBossRemainingRunsLabels[remainingRuns];
        }

        public static string GetTimeUnitLabel(int unitIndex)
        {
            if (unitIndex < 0) unitIndex = 0;
            else if (unitIndex >= _timeUnitLabels.Length) unitIndex = _timeUnitLabels.Length - 1;

            return _timeUnitLabels[unitIndex];
        }
    }
}
