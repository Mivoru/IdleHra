using System;
using System.Linq;
using FolkIdle.Bots;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Every persona sleeps (owner, 2026-11-03): 6-8 game hours a night, with a
    /// 00:30-06:30 core shared by all sleepers so the lockstep conductor can
    /// jump it, and an hour of per-bot jitter either side so logins spread out.
    /// </summary>
    public class BotPersonaSleepTests
    {
        private static DateTime At(int day, double hour) => new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc).AddDays(day).AddHours(hour);

        private static readonly string[] Bots = Enumerable.Range(1, 40).Select(i => $"optimal{i}").ToArray();

        [Fact]
        public void EverySleeperIsOfflineThroughTheSharedCore()
        {
            foreach (var bot in Bots)
            {
                for (int day = 0; day < 30; day++)
                {
                    for (double h = 0.5; h < 6.5; h += 0.25)
                    {
                        Assert.True(Persona.Optimal.IsAsleep(At(day, h), bot), $"{bot} awake at day {day} {h:0.00}h");
                    }
                }
            }
        }

        [Fact]
        public void EverySleeperIsAwakeAcrossTheDay()
        {
            foreach (var bot in Bots)
            {
                for (int day = 0; day < 30; day++)
                {
                    for (double h = 7.5; h < 23.5; h += 0.25)
                    {
                        Assert.False(Persona.Optimal.IsAsleep(At(day, h), bot), $"{bot} asleep at day {day} {h:0.00}h");
                    }
                }
            }
        }

        [Fact]
        public void ANightLastsSixToEightHoursAndTheEdgesSpread()
        {
            var logouts = new System.Collections.Generic.List<double>();
            foreach (var bot in Bots)
            {
                for (int day = 1; day < 20; day++)
                {
                    // Walk the evening in game minutes to find the logout and the login.
                    double? asleepAt = null, awakeAt = null;
                    for (int m = 0; m < 12 * 60; m++)
                    {
                        var t = At(day, 22).AddMinutes(m);
                        bool asleep = Persona.Optimal.IsAsleep(t, bot);
                        if (asleep && asleepAt == null) asleepAt = m / 60.0;
                        if (!asleep && asleepAt != null && awakeAt == null) awakeAt = m / 60.0;
                    }
                    Assert.NotNull(asleepAt);
                    Assert.NotNull(awakeAt);
                    double night = awakeAt!.Value - asleepAt!.Value;
                    Assert.InRange(night, 6.0 - 1e-9, 8.0 + 1.0 / 60);
                    logouts.Add(asleepAt.Value);
                }
            }
            // Spread: logouts do not all land in one ten-minute slot.
            Assert.True(logouts.Max() - logouts.Min() > 0.75, $"logouts spread only {logouts.Max() - logouts.Min():0.00} h");
        }

        [Fact]
        public void HoursUntilAwakeLandsOnTheMorningLogin()
        {
            const string bot = "patient3";
            var night = At(5, 3);
            Assert.True(Persona.Patient.IsAsleep(night, bot));
            double wait = Persona.Patient.HoursUntilAwake(night, bot);
            Assert.False(Persona.Patient.IsAsleep(night.AddHours(wait + 1.0 / 3600), bot));
            Assert.True(Persona.Patient.IsAsleep(night.AddHours(wait - 1.0 / 60), bot));
        }

        [Fact]
        public void HoursUntilAsleepLandsOnTheEveningLogout()
        {
            const string bot = "eagerkit2";
            var noon = At(7, 12);
            double wait = Persona.EagerKit.HoursUntilAsleep(noon, bot);
            Assert.InRange(wait, 11.5 - 1e-9, 12.5 + 1e-9);
            Assert.True(Persona.EagerKit.IsAsleep(noon.AddHours(wait + 1.0 / 3600), bot));
            Assert.False(Persona.EagerKit.IsAsleep(noon.AddHours(wait - 1.0 / 60), bot));
        }

        [Fact]
        public void CasualAndIdleDoNotGainANightOnTopOfTheirCheckIns()
        {
            Assert.False(Persona.Casual.IsAsleep(At(1, 3), "casual1"));
            Assert.False(Persona.Idle.IsAsleep(At(1, 3), "idle1"));
            Assert.Equal(double.PositiveInfinity, Persona.Casual.HoursUntilAsleep(At(1, 12), "casual1"));
        }

        [Fact]
        public void CasualWaitsForItsNextCheckIn()
        {
            // Checked in for the first 20 minutes of every 8 hours.
            Assert.Equal(0, Persona.Casual.HoursUntilOnline(8.1, At(0, 12), "casual1"));
            Assert.Equal(7.5, Persona.Casual.HoursUntilOnline(8.5, At(0, 12), "casual1"), 6);
        }

        [Fact]
        public void TheNightIsTheSameOnARerun()
        {
            var t = At(3, 0.2);
            bool first = Persona.Optimal.IsAsleep(t, "optimal7");
            for (int i = 0; i < 5; i++) Assert.Equal(first, Persona.Optimal.IsAsleep(t, "optimal7"));
        }
    }
}
