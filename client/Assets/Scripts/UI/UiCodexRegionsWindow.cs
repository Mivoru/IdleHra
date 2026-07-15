using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 13.4.3: Codex region-completion milestone window. Rows are pooled
    // via UIComponentPool<UiCodexRegionRow> and only rebuilt when
    // CodexRegionsCache actually changes (dirty-flag pattern), matching
    // UiCodexListBinder. Each row shows a CurrentKills/1000 progress bar (the
    // minimum kill count across the region's monsters - see
    // NetworkBroadcastSystem.HandleCodexRegionsSnapshot for why it is a
    // minimum rather than a sum) plus the permanent Loot Luck bonus a
    // completed region grants.
    public class UiCodexRegionsWindow : MonoBehaviour
    {
        [Header("Codex Regions HUD - Canvas Isolation")]
        public Canvas CodexRegionsSubCanvas;
        public RectTransform CodexRegionsPanelRect;

        [Header("Codex Regions HUD")]
        public ScrollRect ListScrollRect;
        public Transform RowContainer;
        public UiCodexRegionRow RowPrefab;
        public int InitialRowPoolCapacity = 10;

        private UIComponentPool<UiCodexRegionRow> _rowPool;
        private readonly List<UiCodexRegionRow> _activeRows = new List<UiCodexRegionRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (CodexRegionsPanelRect != null)
            {
                LayoutGroup layoutGroup = CodexRegionsPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }
            }

            Transform poolParent = RowContainer != null ? RowContainer : (ListScrollRect != null ? ListScrollRect.content : null);
            if (RowPrefab != null && poolParent != null)
            {
                _rowPool = new UIComponentPool<UiCodexRegionRow>(RowPrefab, poolParent, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            CodexRegionsCache.OnCodexRegionsCacheUpdated += HandleCacheUpdated;
            CodexRegionsCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            CodexRegionsCache.OnCodexRegionsCacheUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty)
            {
                return;
            }

            RefreshRows();
            _isDirty = false;
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void RefreshRows()
        {
            if (_rowPool == null)
            {
                return;
            }

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<RegionProgressData> regions = CodexRegionsCache.Regions;
            for (int i = 0; i < regions.Count; i++)
            {
                RegionProgressData region = regions[i];
                UiCodexRegionRow row = _rowPool.Spawn();
                row.Bind(region.RegionId, region.CurrentKills, region.RequiredKills, region.IsCompleted, region.LootLuckBonusPct);
                _activeRows.Add(row);
            }
        }
    }
}
