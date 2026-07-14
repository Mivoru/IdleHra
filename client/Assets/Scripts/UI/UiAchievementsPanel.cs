using System.Collections.Generic;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 13: Achievements panel. Event-driven only - rows are rebuilt from
    // AchievementCache.OnAchievementsUpdated, never from an Update() loop.
    public class UiAchievementsPanel : MonoBehaviour
    {
        [Header("Achievement List - Pooled")]
        public Transform RowContainer;
        public UiAchievementRow RowPrefab;
        public int InitialRowPoolCapacity = 8;

        private UIComponentPool<UiAchievementRow> _rowPool;
        private readonly List<UiAchievementRow> _activeRows = new List<UiAchievementRow>();

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiAchievementRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            AchievementCache.OnAchievementsUpdated += RefreshRows;
            AchievementCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            AchievementCache.OnAchievementsUpdated -= RefreshRows;
        }

        private void RefreshRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<AchievementEntryData> entries = AchievementCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                AchievementEntryData entry = entries[i];
                UiAchievementRow row = _rowPool.Spawn();
                row.Bind(entry.AchievementId, entry.CompletedTier, entry.CurrentProgress, entry.NextTierTarget);
                _activeRows.Add(row);
            }
        }
    }
}
