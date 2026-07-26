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
    // Deliberately NOT included: individual item drops. CombatLootEngine
    // resolves drops entirely server-side and there is no per-drop
    // notification anywhere in the wire protocol - the client only ever
    // learns about loot indirectly, as a changed material stock in a
    // separate HTTP snapshot. Listing item drops honestly needs a real
    // server-side loot feed, so this reports the three things it can
    // actually stand behind rather than guessing at drops.
    public sealed class SessionKillEntry
    {
        public int MonsterId;
        public string MonsterName;
        public long Kills;
    }

    public class CombatSessionTracker : MonoBehaviour
    {
        public static CombatSessionTracker Instance { get; private set; }

        public VisualSyncProxy SyncProxy;

        public static event System.Action OnSessionProgressUpdated;

        public bool HasSession { get; private set; }
        public long GoldGained { get; private set; }
        public long XpGained { get; private set; }
        public long TotalKills { get; private set; }

        public IReadOnlyList<SessionKillEntry> Kills => _killEntries;

        private readonly Dictionary<int, long> _baselineKillsByMonster = new Dictionary<int, long>(64);
        private readonly List<SessionKillEntry> _killEntries = new List<SessionKillEntry>(16);

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
            _killEntries.Clear();
            _baselineKillsByMonster.Clear();

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
