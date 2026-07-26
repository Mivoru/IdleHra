using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Inventory screen. The game had no inventory view of any kind -
    // a player could see equipment inside the Forge (only as crafting/reroll
    // input) and nothing else. Materials and the village stash were entirely
    // invisible despite being what the whole crafting and village-upgrade
    // economy spends.
    //
    // Three sections in one scrolling list, backed by PlayerInventoryCache:
    //   Equipped   - the gear actually on the character right now
    //   Backpack   - carried equipment instances, and carried material stacks
    //   Stash      - the village overflow store CombatLootEngine spills into
    //                when the backpack is full
    //
    // Material rows show both tiers because that is how they are actually
    // spent: InventoryAndStashSystem consumes the unified balance, backpack
    // first, so a player needs to see both numbers to understand what a
    // craft will actually draw from.
    public class UiInventoryPanel : MonoBehaviour
    {
        // Modul: interactive inventory. Equipping is asynchronous
        // server-side (EquipmentSlotEngine runs the swap on a background
        // dispatch and reports back through EquipmentSlotUpdateNotification),
        // so there is nothing to read back the instant the command is sent.
        // Rather than guess at the outcome client-side, the panel re-pulls
        // the authoritative snapshot shortly afterwards.
        private const float PostEquipRefreshDelaySeconds = 0.75f;

        public WebSocketClient NetworkClient;
        public EquipmentInventoryCache InventoryCache;
        public AssetRegistry Registry;
        public VisualSyncProxy SyncProxy;

        [Header("Header")]
        public TMP_Text SummaryText;
        public TMP_Text StatusText;
        public Button RefreshButton;

        [Header("Rows - pooled")]
        public Transform RowContainer;
        public UiInventoryEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 24;

        [Header("Section headers - pooled separately")]
        public UiSectionHeaderRow SectionHeaderPrefab;

        private UIComponentPool<UiInventoryEntryRow> _rowPool;
        private UIComponentPool<UiSectionHeaderRow> _headerPool;
        private readonly List<UiInventoryEntryRow> _activeRows = new List<UiInventoryEntryRow>();
        private readonly List<UiSectionHeaderRow> _activeHeaders = new List<UiSectionHeaderRow>();

        private readonly System.Text.StringBuilder _detailBuilder = new System.Text.StringBuilder(256);
        private bool _isDirty;
        private float _pendingRefreshTimer;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiInventoryEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (SectionHeaderPrefab != null && RowContainer != null)
            {
                _headerPool = new UIComponentPool<UiSectionHeaderRow>(SectionHeaderPrefab, RowContainer, 4);
            }

            if (RefreshButton != null)
            {
                RefreshButton.onClick.AddListener(PlayerInventoryCache.RequestSnapshot);
            }
        }

        private void OnEnable()
        {
            PlayerInventoryCache.OnInventoryUpdated += HandleInventoryUpdated;
            PlayerInventoryCache.RequestSnapshot();
            _isDirty = true;
        }

        private void OnDisable()
        {
            PlayerInventoryCache.OnInventoryUpdated -= HandleInventoryUpdated;
        }

        // Rebuild is deferred to the next frame rather than run straight from
        // the cache callback, which arrives off an await continuation -
        // matching UiFriendsWindow/UiMailboxWindow's existing dirty-flag
        // convention so pooled row churn always happens on a normal frame.
        private void Update()
        {
            if (_pendingRefreshTimer > 0f)
            {
                _pendingRefreshTimer -= Time.deltaTime;
                if (_pendingRefreshTimer <= 0f)
                {
                    PlayerInventoryCache.RequestSnapshot();

                    // Modul: the Forge reads its owned-equipment list from
                    // this separate cache, so leaving it stale would show a
                    // just-equipped item as still freely available there.
                    InventoryCache?.RequestSnapshot();
                }
            }

            if (!_isDirty) return;
            _isDirty = false;
            RebuildRows();
        }

        private void HandleEquipClicked(long instanceId)
        {
            if (NetworkClient == null || instanceId <= 0) return;

            NetworkClient.SendEquipItemCommandZeroAlloc(instanceId);
            _pendingRefreshTimer = PostEquipRefreshDelaySeconds;

            if (StatusText != null)
            {
                StatusText.text = "Equipping...";
            }
        }

        private void HandleInventoryUpdated()
        {
            _isDirty = true;
        }

        private void RebuildRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++) _rowPool.Despawn(_activeRows[i]);
            _activeRows.Clear();

            if (_headerPool != null)
            {
                for (int i = 0; i < _activeHeaders.Count; i++) _headerPool.Despawn(_activeHeaders[i]);
                _activeHeaders.Clear();
            }

            IReadOnlyList<InventoryEquipmentData> equipment = PlayerInventoryCache.Equipment;
            IReadOnlyList<InventoryStackData> stacks = PlayerInventoryCache.Stacks;

            int equippedCount = 0;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (equipment[i].IsEquipped) equippedCount++;
            }

            if (SummaryText != null)
            {
                SummaryText.text =
                    equippedCount + " equipped   -   " +
                    (equipment.Count - equippedCount) + " carried items   -   " +
                    stacks.Count + " material types   -   stacks cap at " + PlayerInventoryCache.MaxStackQuantity;
            }

            // ---- Equipped ----
            AddSectionHeader("EQUIPPED");
            bool anyEquipped = false;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (!equipment[i].IsEquipped) continue;
                AddEquipmentRow(equipment[i]);
                anyEquipped = true;
            }
            if (!anyEquipped)
            {
                AddPlainRow("Nothing equipped", "Equip gear from the Forge or your backpack.", string.Empty);
            }

            // ---- Backpack ----
            AddSectionHeader("BACKPACK");
            bool anyCarried = false;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (equipment[i].IsEquipped) continue;
                AddEquipmentRow(equipment[i]);
                anyCarried = true;
            }

            for (int i = 0; i < stacks.Count; i++)
            {
                if (stacks[i].BackpackQuantity <= 0) continue;
                AddStackRow(stacks[i], stacks[i].BackpackQuantity, "carried");
                anyCarried = true;
            }

            if (!anyCarried)
            {
                AddPlainRow("Backpack empty", "Kill monsters to start collecting materials and gear.", string.Empty);
            }

            // ---- Stash ----
            AddSectionHeader("VILLAGE STASH");
            bool anyStashed = false;
            for (int i = 0; i < stacks.Count; i++)
            {
                if (stacks[i].StashQuantity <= 0) continue;
                AddStackRow(stacks[i], stacks[i].StashQuantity, "stashed");
                anyStashed = true;
            }

            if (!anyStashed)
            {
                AddPlainRow("Stash empty", "Drops overflow here automatically when your backpack is full.", string.Empty);
            }
        }

        private void AddSectionHeader(string title)
        {
            if (_headerPool == null) return;

            UiSectionHeaderRow header = _headerPool.Spawn();
            header.Bind(title);
            header.transform.SetAsLastSibling();
            _activeHeaders.Add(header);
        }

        private void AddEquipmentRow(InventoryEquipmentData item)
        {
            Sprite icon = null;
            Registry?.TryGetItemSprite(item.BaseItemId, out icon);

            UiInventoryEntryRow row = _rowPool.Spawn();
            row.BindWithAction(
                ClientContentRegistry.GetItemDisplayName(item.BaseItemId),
                BuildEquipmentDetail(item),
                item.IsEquipped ? "equipped" : string.Empty,
                item.IsEquipped,
                icon,
                item.Id,
                // Already-equipped gear offers no action here; unequipping
                // stays where it already lives, on the character HUD's
                // equipment slots.
                item.IsEquipped ? null : "Equip",
                item.IsEquipped ? (System.Action<long>)null : HandleEquipClicked);
            row.transform.SetAsLastSibling();
            _activeRows.Add(row);
        }

        private void AddStackRow(InventoryStackData stack, long quantity, string whereLabel)
        {
            Sprite icon = null;
            Registry?.TryGetItemSprite(stack.ItemId, out icon);

            UiInventoryEntryRow row = _rowPool.Spawn();
            row.Bind(
                ClientContentRegistry.GetItemDisplayName(stack.ItemId),
                "Carried " + stack.BackpackQuantity + "   -   Stashed " + stack.StashQuantity + "   -   Total " + stack.Total,
                quantity + " " + whereLabel,
                false,
                icon);
            row.transform.SetAsLastSibling();
            _activeRows.Add(row);
        }

        private void AddPlainRow(string title, string detail, string quantity)
        {
            UiInventoryEntryRow row = _rowPool.Spawn();
            row.Bind(title, detail, quantity, false, null);
            row.transform.SetAsLastSibling();
            _activeRows.Add(row);
        }

        // Modul: Affix System Unification. Rarity, the affix count that rarity
        // grants, and the actual rolled affixes - so the row answers "what does
        // this item do" rather than just naming it.
        private string BuildEquipmentDetail(InventoryEquipmentData item)
        {
            _detailBuilder.Clear();
            _detailBuilder.Append(ClientAffixRegistry.GetRarityName(item.QualityTier));
            _detailBuilder.Append(" (").Append(ClientAffixRegistry.GetAffixCount(item.QualityTier)).Append(" affixes)");

            if (item.IsAffixLocked)
            {
                _detailBuilder.Append("   -   LOCKED");
            }

            if (item.Affixes != null && item.Affixes.Count > 0)
            {
                _detailBuilder.Append("   -   ");
                bool first = true;
                foreach (var affix in item.Affixes)
                {
                    if (!first) _detailBuilder.Append(",  ");
                    _detailBuilder.Append(ClientAffixRegistry.Describe(affix.Key, affix.Value));
                    first = false;
                }
            }

            return _detailBuilder.ToString();
        }
    }
}
