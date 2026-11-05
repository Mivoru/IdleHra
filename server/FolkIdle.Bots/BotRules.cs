namespace FolkIdle.Bots
{
    /// <summary>
    /// The bot's small decision rules, kept pure so the tests can pin them
    /// without a server. Each one exists because a night run measured the
    /// bot getting it wrong; the reasons are on the callers in BotBrain.
    /// </summary>
    public static class BotRules
    {
        /// <summary>
        /// Modul: A REFUSAL NEEDS AN ANSWER, NOT JUST A CLOCK (night
        /// 2026-11-05). The window was 8 game seconds, which is 0.4 wall
        /// seconds at x20 - shorter than a command's round trip plus the next
        /// snapshot. The x20 reference logged 394 "refused (halt 0, result 0)"
        /// changes against 56 in lockstep, and 153 trips ended as refused
        /// against 17: the paced bot was striking fights the server had
        /// accepted. Paced, the window now also waits two wall seconds and two
        /// state frames after the command (ten wall seconds at most). Lockstep
        /// keeps the game-time rule: its frames follow the conductor's steps,
        /// and the server answers inside the step that carried the command.
        /// At x1, 8 game seconds is already longer than the wall floor.
        /// </summary>
        public static bool ConfirmationWindowOver(bool lockstep, double gameSeconds, double wallSeconds, long framesSince)
        {
            if (gameSeconds <= 8) return false;
            if (lockstep) return true;
            return wallSeconds >= 10 || (wallSeconds >= 2 && framesSince >= 2);
        }

        /// <summary>
        /// Go fishing before the larder is empty: when the best fight that needs
        /// food beats every fight that holds without it, and the food held
        /// covers less than half an hour of that fight.
        /// </summary>
        public static bool NeedsFoodTopUp(int foodHeld, double foodFightXpPerHour, double bestSafeXpPerHour, double foodPerHour)
        {
            if (foodPerHour <= 0) return false;
            if (foodFightXpPerHour <= bestSafeXpPerHour) return false;
            return foodHeld < foodPerHour * 0.5;
        }
    }
}
