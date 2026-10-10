using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>What an event shop entry grants.</summary>
    public enum EventShopKind : byte
    {
        Avatar = 1,
        Pet = 2,
    }

    /// <param name="Id">Stable string key - stored on rows, never a position.</param>
    /// <param name="Art">Published sprite path under /sprites/, e.g. Events/samhain/avatars/banshee.webp.</param>
    public sealed record EventShopItem(string Id, EventShopKind Kind, string Name, int Price, string Art);

    /// <summary>Where an event is in its life.</summary>
    public enum SeasonalEventPhase : byte
    {
        /// <summary>No event, or past its grace: the currency is gone.</summary>
        None = 0,
        /// <summary>Running: the currency drops and the shop is open.</summary>
        Live = 1,
        /// <summary>Ended, shop still open so a player can spend what is left (owner: 3 days).</summary>
        Grace = 2,
    }

    /// <param name="Id">Numeric id, stamped on the player's balance so a new event never inherits an old one's currency.</param>
    public sealed record SeasonalEventDefinition(
        int Id,
        string Key,
        string Name,
        string CurrencyName,
        DateTimeOffset Start,
        DateTimeOffset End,
        int GraceDays,
        double KillChance,
        double GatherChance,
        double OfflineFactor,
        IReadOnlyList<EventShopItem> Shop,
        int CurrencyGeneration = 1)
    {
        public DateTimeOffset ShopClose => End.AddDays(GraceDays);

        /// <summary>
        /// What a balance is stamped with (PlayerRecords.EventCurrencyEventId):
        /// the event AND its currency generation. Raising the generation zeroes
        /// every balance of the event at its next normalise - live or at login -
        /// which a SQL update cannot do, because the live payload owns the
        /// balance and the next checkpoint would write the old figure back.
        /// </summary>
        public int CurrencyStamp => Id * 100 + CurrencyGeneration;
    }

    /// <summary>
    /// The seasonal events (Samhain, later Christmas, spring, summer and the
    /// filler between them). Plan: docs/superpowers/plans/2026-10-09-samhain-event.md.
    ///
    /// Modul: INDEPENDENT OF GlobalEventType. Blood Moon and the other three
    /// are a weekly rotating buff that always runs; a seasonal event is a
    /// dated festival with its own currency and shop. The two coexist.
    ///
    /// Modul: A CALENDAR-GATED FEATURE NEEDS A WAY TO OPEN IT (server/CLAUDE.md).
    /// <see cref="ForceLive"/> is set by the dev endpoint so exercise.mjs and a
    /// dev box can reach the event out of season.
    /// </summary>
    public static class SeasonalEventRegistry
    {
        public const int SamhainId = 1;

        // Modul: THE RATES ARE MEASURED, NOT GUESSED (2026-10-09).
        // Combat: production player_stat_samples show 360-670 kills an hour on
        // a live account, 2,000-34,000 a day. Gathering is far faster per
        // action - GatheringToolEngine.ComputeRequiredTicks puts mastery 75 at
        // ~1,600 harvests an hour on a region-5 node, and the top live account
        // (mastery 326) at 3,000-12,000 - so a harvest pays a FIFTH of a kill's
        // chance, which gives an hour of either work roughly the same.
        //
        // Owner, 2026-10-10: HALVED (kill 5% -> 2.5%, harvest 1% -> 0.5%)
        // after the first evening - a fast gatherer at mastery 326 runs ~12,000
        // harvests an hour on a region-1 node, and the owner's account passed
        // 1,000 pumpkins within hours. The per-day figures below halve too.
        //
        // Owner, 2026-10-09: offline pays the SAME as online, and there is NO
        // daily cap - a player who plays more earns more. What that means per
        // day (after the halving): a fighter and two gatherers running around
        // the clock is about 750, a lone new character about 125. The shop is priced against
        // that (owner to confirm): everything costs ~23,000, so a dedicated
        // account finishes in two weeks and a newcomer buys a few things.
        public const int SamhainAvatarPrice = 1000;
        public const int SamhainPetPrice = 3000;

        private static readonly IReadOnlyList<EventShopItem> SamhainShop = new[]
        {
            Avatar("banshee", "Banshee"),
            Avatar("dullahan", "Dullahan"),
            Avatar("jack_o_lantern", "Jack-o'-Lantern"),
            Avatar("pooka", "Pooka"),
            Avatar("pumpkin", "Pumpkin King"),
            Avatar("skeleton", "Skeleton"),
            Avatar("vampire", "Vampire"),
            Avatar("werewolf", "Werewolf"),
            Pet("pet_ghostie"),
            Pet("pet_pixie"),
            Pet("pet_skeleton_dog"),
            Pet("pet_black_cat"),
            Pet("pet_wolf_pup"),
        };

        public static readonly IReadOnlyList<SeasonalEventDefinition> All = new[]
        {
            new SeasonalEventDefinition(
                Id: SamhainId,
                Key: "samhain",
                Name: "Samhain",
                CurrencyName: "Pumpkins",
                // Owner, 2026-10-09: live as soon as it is deployed, so it can
                // be tested on production; ends after Samhain itself (Nov 1).
                Start: new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero),
                // Owner, 2026-10-10: shortened - ends the night after Samhain
                // (Oct 31 / Nov 1) instead of Nov 7.
                End: new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
                GraceDays: 3,
                // Owner, 2026-10-10 morning: an endgame account should afford
                // only 2-3 pets and 2-3 avatars over the whole event (~8-12k
                // pumpkins). Measured on production: the owner's three
                // characters earned 1,187 in ~5 h at 2.5%/0.5% (~240/h, offline
                // included), which is ~130k by the end. Cut 25x to ~10/h: about
                // 5,500 from drops, plus 3,750 for The Cailleach's winters I-V.
                //
                // Owner, 2026-10-10 evening: raised 5x again, and NOT reset.
                // The 25x cut was sized on the owner's own account, and it
                // left an ordinary active player far short: Tomda earned 42
                // in 12 h online (~85 a day) beside 1,500 from winters I-III,
                // so about 3,300 by the end - one pet. The target is that such
                // a player affords 2-3 pets and 2-3 avatars: 5x is ~8,900 from
                // drops plus 1,500, ~10,400. The most active account
                // (~230 a day at the old rate) now buys the whole shop; the
                // owner accepted that over a daily cap.
                KillChance: 0.005,
                GatherChance: 0.001,
                OfflineFactor: 1.0,
                Shop: SamhainShop,
                // Owner, 2026-10-10: everyone back to zero after the rates
                // were halved, so the first evening's double-rate pumpkins
                // (and pre-event offline windfalls) give nobody a head start.
                // Generation 3 (2026-10-10 morning): zeroed again with the
                // 25x cut, so pumpkins earned at the old rate buy nothing.
                CurrencyGeneration: 3),
        };

        // Name and art come from PetRegistry, the one place a pet is defined.
        // PetRegistry reads only this class's CONSTANTS, so neither static
        // initialiser waits on the other.
        private static EventShopItem Pet(string petId)
        {
            var pet = PetRegistry.Find(petId) ?? throw new InvalidOperationException($"no pet {petId}");
            return new EventShopItem(pet.Id, EventShopKind.Pet, pet.Name, SamhainPetPrice, pet.Art);
        }

        private static EventShopItem Avatar(string slug, string name)
            => new("avatar_samhain_" + slug, EventShopKind.Avatar, name, SamhainAvatarPrice,
                   "Events/samhain/avatars_v2/" + slug + ".webp");

        /// <summary>Dev-only: the event to treat as live regardless of the date (0 = none).</summary>
        public static volatile int ForceLive;

        public static SeasonalEventDefinition? Find(int id) => All.FirstOrDefault(e => e.Id == id);

        public static EventShopItem? FindShopItem(string? id)
            => id == null ? null : All.SelectMany(e => e.Shop).FirstOrDefault(i => i.Id == id);

        /// <summary>The event that is live or in its grace at <paramref name="now"/>, if any.</summary>
        public static (SeasonalEventDefinition? Event, SeasonalEventPhase Phase) Current(DateTimeOffset now)
        {
            int forced = ForceLive;
            if (forced != 0 && Find(forced) is { } f) return (f, SeasonalEventPhase.Live);
            foreach (var e in All)
            {
                if (now >= e.Start && now < e.End) return (e, SeasonalEventPhase.Live);
                if (now >= e.End && now < e.ShopClose) return (e, SeasonalEventPhase.Grace);
            }
            return (null, SeasonalEventPhase.None);
        }

        public static (SeasonalEventDefinition? Event, SeasonalEventPhase Phase) Current(long nowEpochSeconds)
            => Current(DateTimeOffset.FromUnixTimeSeconds(nowEpochSeconds));
    }
}
