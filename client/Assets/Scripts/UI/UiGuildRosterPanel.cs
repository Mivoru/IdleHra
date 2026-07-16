using System.Collections.Generic;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1. Guild
    // Roster panel - a clean member list showing each member's database
    // Role (0=Member, 1=Leader) and online status, fetched via
    // GuildRosterCache (NetworkBroadcastSystem.HandleGuildRoster). Rows are
    // pooled via UIComponentPool, mirroring every other list-style panel in
    // this codebase.
    public class UiGuildRosterPanel : MonoBehaviour
    {
        [Header("Guild Roster HUD")]
        public Transform RowContainer;
        public UiGuildRosterEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 20;

        private UIComponentPool<UiGuildRosterEntryRow> _rowPool;
        private readonly List<UiGuildRosterEntryRow> _activeRows = new List<UiGuildRosterEntryRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiGuildRosterEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            GuildRosterCache.OnGuildRosterCacheUpdated += HandleCacheUpdated;
            GuildRosterCache.Refresh();
        }

        private void OnDisable()
        {
            GuildRosterCache.OnGuildRosterCacheUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty) return;

            RefreshRows();
            _isDirty = false;
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void RefreshRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<GuildRosterEntryData> entries = GuildRosterCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                GuildRosterEntryData entry = entries[i];
                UiGuildRosterEntryRow row = _rowPool.Spawn();
                row.Bind(entry.PlayerId, entry.Role, entry.ContributionPoints, entry.IsOnline);
                _activeRows.Add(row);
            }
        }
    }
}
