using System.Collections.Generic;
using UnityEngine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    // Modul: UI rework. "What has this character actually farmed since I
    // sent it out?" - the running tally the Combat screen shows underneath
    // the fight.
    //
    // Every number here is a delta against a baseline captured at deploy
    // time, taken from data the game already reports authoritatively:
    // per-monster kill counts from the Codex snapshot (the same rows the
    // server's kill-event cron maintains) plus gold and XP from the live
    // state packet. Nothing is invented or estimated client-side.
    //
    // Item drops now come through the real server-side loot feed added
    // alongside this (ResponseLootDropPacket, one message per item actually
    // granted, published by CombatLootEngine only after its transaction
    // commits) - so the drop list is the server's own record of what it
    // granted, not a client-side guess from a changed material stock.
    public sealed class SessionKillEntry
    {
        public int MonsterId;
        public string MonsterName;
        public long Kills;
    }

    // One accumulated line in the session drop list. Aggregated by item so a
    // long session shows "Mouse Fur x412" rather than 412 separate lines.
    public sealed class SessionDropEntry
    {
        public int ItemId;
        public string ItemName;
        public long Quantity;
        public byte DropKind;

        // Highest rarity tier seen for this item this session (equipment
        // only; 0 for materials and scrap, which have no rarity roll).
        public byte BestQualityTier;
    }

    public class CombatSessionTracker : MonoBehaviour
    {
        public static CombatSessionTracker Instance { get; private set; }

        public VisualSyncProxy SyncProxy;

        public static event System.Action OnSessionProgressUpdated;

        public WebSocketClient NetworkClient;

        public bool HasSession { get; private set; }
        public long GoldGained { get; private set; }
        public long XpGained { get; private set; }
        public long TotalKills { get; private set; }
        public long TotalItemsDropped { get; private set; }

        public IReadOnlyList<SessionKillEntry> Kills => _killEntries;
        public IReadOnlyList<SessionDropEntry> Drops => _dropEntries;

        private readonly Dictionary<int, long> _baselineKillsByMonster = new Dictionary<int, long>(64);
        private readonly List<SessionKillEntry> _killEntries = new List<SessionKillEntry>(16);

        // Drop lines, plus an id index so accumulating a repeat drop is a
        // dictionary hit rather than a linear scan. Both are allocated once;
        // a repeat drop of an already-seen item allocates nothing at all, and
        // a first-ever drop of one item allocates a single small entry.
        private readonly List<SessionDropEntry> _dropEntries = new List<SessionDropEntry>(32);
        private readonly Dictionary<int, SessionDropEntry> _dropEntriesByItemId = new Dictionary<int, SessionDropEntry>(32);

        private long _baselineGold;
        private long _baselineXp;

        // A baseline is only meaningful once the Codex snapshot that defines
        // it has actually arrived. Deploy fires the request; this flag keeps
        // the first response afterwards from being counted as progress.
        private bool _awaitingBaseline;

        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            CodexInventoryCache.OnCodexCacheUpdated += HandleCodexUpdated;
        }

        private void OnDisable()
        {
            CodexInventoryCache.OnCodexCacheUpdated -= HandleCodexUpdated;
        }

        // Called by the Combat screen the moment a character is deployed.
        public void BeginSession()
        {
            HasSession = true;
            _awaitingBaseline = true;

            GoldGained = 0;
            XpGained = 0;
            TotalKills = 0;
            TotalItemsDropped = 0;
            _killEntries.Clear();
            _baselineKillsByMonster.Clear();
            _dropEntries.Clear();
            _dropEntriesByItemId.Clear();

            if (SyncProxy != null)
            {
                _baselineGold = SyncProxy.GetGoldBalance();
                _baselineXp = SyncProxy.VisualPlayerXp;
            }

            CodexInventoryCache.RequestSnapshot();
            OnSessionProgressUpdated?.Invoke();
        }

        // Pulls a fresh Codex snapshot so the kill tally advances. Called by
        // the Combat screen on a slow timer while it is open, rather than
        // polling on its own - there is no point refreshing kills for a
        // screen nobody is looking at.
        public void RequestRefresh()
        {
            if (!HasSession) return;
            CodexInventoryCache.RequestSnapshot();
        }

        // Modul: Loot Event Feed. Drained every frame rather than on the
        // slow Codex timer, so a drop appears the moment the server reports
        // it. Allocation-free in the steady state: the packet is an
        // unmanaged struct, the aggregation is a dictionary lookup on an int
        // key, and the item name is resolved once on first sight and then
        // cached on the entry.
        //
        // Runs even when no session is active so that drops from a fight
        // already in progress are not silently dropped on the floor; they
        // are simply discarded if the player has not deployed yet.
        private void Update()
        {
            if (NetworkClient == null) return;

            bool changed = false;
            while (NetworkClient.LootDropQueue.TryDequeue(out ResponseLootDropPacket drop))
            {
                if (!HasSession) continue;

                AccumulateDrop(drop);
                changed = true;
            }

            if (changed)
            {
                OnSessionProgressUpdated?.Invoke();
            }
        }

        private void AccumulateDrop(ResponseLootDropPacket drop)
        {
            if (drop.ItemId <= 0 || drop.Quantity <= 0) return;

            TotalItemsDropped += drop.Quantity;

            if (_dropEntriesByItemId.TryGetValue(drop.ItemId, out SessionDropEntry entry))
            {
                entry.Quantity += drop.Quantity;
                if (drop.QualityTier > entry.BestQualityTier)
                {
                    entry.BestQualityTier = drop.QualityTier;
                }
                return;
            }

            entry = new SessionDropEntry
            {
                ItemId = drop.ItemId,
                ItemName = ResolveItemName(drop.ItemId),
                Quantity = drop.Quantity,
                DropKind = drop.DropKind,
                BestQualityTier = drop.QualityTier
            };

            _dropEntriesByItemId[drop.ItemId] = entry;
            _dropEntries.Add(entry);
        }

        private static string ResolveItemName(int itemId)
        {
            return ClientContentRegistry.TryGetItemById(itemId, out ItemEntry item)
                ? ClientContentRegistry.GetItemDisplayName(item)
                : "Item #" + itemId;
        }

        private void HandleCodexUpdated()
        {
            if (!HasSession) return;

            IReadOnlyList<CodexSnapshotEntryData> entries = CodexInventoryCache.Entries;

            if (_awaitingBaseline)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    _baselineKillsByMonster[entries[i].MonsterId] = entries[i].Kills;
                }
                _awaitingBaseline = false;
                OnSessionProgressUpdated?.Invoke();
                return;
            }

            _killEntries.Clear();
            TotalKills = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                CodexSnapshotEntryData entry = entries[i];
                _baselineKillsByMonster.TryGetValue(entry.MonsterId, out long baseline);

                long gained = entry.Kills - baseline;
                if (gained <= 0) continue;

                TotalKills += gained;
                _killEntries.Add(new SessionKillEntry
                {
                    MonsterId = entry.MonsterId,
                    MonsterName = ClientContentRegistry.TryGetMonster(entry.MonsterId, out MonsterEntry monster) ? monster.Name : "Monster #" + entry.MonsterId,
                    Kills = gained
                });
            }

            _killEntries.Sort((a, b) => b.Kills.CompareTo(a.Kills));

            RecomputeCurrencyDeltas();
            OnSessionProgressUpdated?.Invoke();
        }

        private void RecomputeCurrencyDeltas()
        {
            if (SyncProxy == null) return;

            // Clamped at zero: spending gold mid-session (a Forge craft, a
            // Market buy) would otherwise show a negative "farmed" figure,
            // which reads as a bug rather than as the true statement it is.
            long goldDelta = SyncProxy.GetGoldBalance() - _baselineGold;
            GoldGained = goldDelta > 0 ? goldDelta : 0;

            long xpDelta = SyncProxy.VisualPlayerXp - _baselineXp;
            XpGained = xpDelta > 0 ? xpDelta : 0;
        }
    }
}
