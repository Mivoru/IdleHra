using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 15/23: list-of-monsters HUD that drives UiCodex3DViewer.Instance.ShowMonster.
    // Rows are pooled via UIComponentPool<UiCodexListRow> and only rebuilt when the
    // underlying Codex data actually changes (dirty-flag pattern) - Update() never
    // recreates rows or touches layout on its own, it only checks a bool set from
    // CodexInventoryCache.OnCodexCacheUpdated.
    //
    // AssetKey resolution note: CodexInventoryCache's HTTP snapshot carries MonsterId/
    // Level/Kills only (no AssetKey - see NetworkBroadcastSystem.HandleCodexSnapshot).
    // No MonsterId-to-Addressable-key naming convention exists anywhere else in this
    // client yet, so ResolveAssetKey below is a placeholder ("Monster_{id}") until a
    // real one is established; a lookup miss just means UiCodex3DViewer.ShowMonster
    // silently fails to load a preview, same as any other missing Addressable today.
    public class UiCodexListBinder : MonoBehaviour
    {
        [Header("Codex List HUD - Canvas Isolation")]
        public Canvas CodexListSubCanvas;
        public RectTransform CodexListPanelRect;

        [Header("Codex List HUD")]
        public ScrollRect ListScrollRect;
        public Transform RowContainer;
        public UiCodexListRow RowPrefab;
        public int InitialRowPoolCapacity = 16;

        private UIComponentPool<UiCodexListRow> _rowPool;
        private readonly List<UiCodexListRow> _activeRows = new List<UiCodexListRow>();
        private readonly List<MonsterCodexEntryView> _currentEntries = new List<MonsterCodexEntryView>();
        private readonly List<MonsterCodexEntryView> _convertedEntries = new List<MonsterCodexEntryView>();
        private bool _isDirty;

        private void Awake()
        {
            if (CodexListPanelRect != null)
            {
                LayoutGroup layoutGroup = CodexListPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }
            }

            Transform poolParent = RowContainer != null ? RowContainer : (ListScrollRect != null ? ListScrollRect.content : null);

            if (RowPrefab != null && poolParent != null)
            {
                _rowPool = new UIComponentPool<UiCodexListRow>(RowPrefab, poolParent, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            CodexInventoryCache.OnCodexCacheUpdated += HandleCodexCacheUpdated;
            CodexInventoryCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            CodexInventoryCache.OnCodexCacheUpdated -= HandleCodexCacheUpdated;
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

        private void HandleCodexCacheUpdated()
        {
            IReadOnlyList<CodexSnapshotEntryData> snapshot = CodexInventoryCache.Entries;

            _convertedEntries.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                CodexSnapshotEntryData entry = snapshot[i];
                _convertedEntries.Add(new MonsterCodexEntryView(entry.MonsterId, ResolveAssetKey(entry.MonsterId), entry.Level));
            }

            SetCodexEntries(_convertedEntries);
        }

        private static string ResolveAssetKey(int monsterId)
        {
            return "Monster_" + monsterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public void SetCodexEntries(IReadOnlyList<MonsterCodexEntryView> entries)
        {
            if (entries == null || !HasEntriesChanged(entries))
            {
                return;
            }

            _currentEntries.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                _currentEntries.Add(entries[i]);
            }

            _isDirty = true;
        }

        private bool HasEntriesChanged(IReadOnlyList<MonsterCodexEntryView> entries)
        {
            if (entries.Count != _currentEntries.Count)
            {
                return true;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                MonsterCodexEntryView existing = _currentEntries[i];
                MonsterCodexEntryView incoming = entries[i];
                if (existing.MonsterId != incoming.MonsterId ||
                    existing.Level != incoming.Level ||
                    existing.AssetKey != incoming.AssetKey)
                {
                    return true;
                }
            }

            return false;
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

            for (int i = 0; i < _currentEntries.Count; i++)
            {
                MonsterCodexEntryView entry = _currentEntries[i];
                UiCodexListRow row = _rowPool.Spawn();
                row.Bind(entry.MonsterId, entry.AssetKey, entry.Level, HandleRowSelected);
                _activeRows.Add(row);
            }
        }

        private static void HandleRowSelected(string assetKey)
        {
            if (UiCodex3DViewer.Instance != null)
            {
                UiCodex3DViewer.Instance.ShowMonster(assetKey);
            }
        }
    }
}
