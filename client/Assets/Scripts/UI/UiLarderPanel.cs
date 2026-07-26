using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: larder. The auto-eat larder - three food slots the character eats
    // from automatically when its health drops below the threshold.
    //
    // This screen exists because the larder had no write side at all. Four
    // server systems read TickStatePayload.Food{1,2,3}_ItemId/_Count - the
    // combat auto-eat step, both World Boss depletion checks, and the Chrono
    // warp catch-up - and nothing anywhere assigned them: no command, no
    // storage, no UI. So every player's larder was permanently empty, and any
    // combat activity stopped the first time health crossed the auto-eat
    // threshold, silently, with the character left standing at full HP looking
    // exactly like one that had never been deployed. That was the single
    // largest barrier to the game being playable for more than a minute.
    //
    // The threshold slider is here too rather than buried in Settings, because
    // it is meaningless without stocked food and vice versa.
    public class UiLarderPanel : MonoBehaviour
    {
        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;

        [Header("Slots")]
        public TMP_Dropdown[] SlotFoodDropdowns = new TMP_Dropdown[3];
        public TMP_InputField[] SlotQuantityInputs = new TMP_InputField[3];
        public Button[] SlotLoadButtons = new Button[3];
        public Button[] SlotUnloadButtons = new Button[3];
        public TMP_Text[] SlotContentsTexts = new TMP_Text[3];

        [Header("Auto-Eat")]
        public Slider ThresholdSlider;
        public TMP_Text ThresholdValueText;

        [Header("Status")]
        public TMP_Text StatusText;
        public TMP_Text SustainEstimateText;

        private const int SlotCount = 3;
        private const int SlotCapacity = 999;

        // Index-aligned with each dropdown's option list, so a selection maps
        // back to a real item id. Rebuilt only when the backpack changes.
        private readonly List<int> _offeredFoodItemIds = new List<int>();
        private readonly StringBuilder _labelBuilder = new StringBuilder(64);

        // The threshold is only sent when the player lets go of the slider.
        // Sending on every value-changed callback would put one command per
        // frame on the wire for a single drag.
        private int _lastSentThresholdPct = -1;
        private bool _suppressThresholdCallback;

        private void Awake()
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                int capturedSlot = slot;

                if (SlotLoadButtons != null && slot < SlotLoadButtons.Length && SlotLoadButtons[slot] != null)
                {
                    SlotLoadButtons[slot].onClick.AddListener(() => HandleLoadClicked(capturedSlot));
                }

                if (SlotUnloadButtons != null && slot < SlotUnloadButtons.Length && SlotUnloadButtons[slot] != null)
                {
                    SlotUnloadButtons[slot].onClick.AddListener(() => HandleUnloadClicked(capturedSlot));
                }
            }

            if (ThresholdSlider != null)
            {
                ThresholdSlider.minValue = 0f;
                ThresholdSlider.maxValue = 100f;
                ThresholdSlider.wholeNumbers = true;
                ThresholdSlider.onValueChanged.AddListener(HandleThresholdChanged);
            }
        }

        private void OnEnable()
        {
            PlayerInventoryCache.OnInventoryUpdated += HandleInventoryUpdated;
            PlayerInventoryCache.RequestSnapshot();

            RebuildFoodOptions();
            RefreshFromProxy();
        }

        private void OnDisable()
        {
            PlayerInventoryCache.OnInventoryUpdated -= HandleInventoryUpdated;

            // Committing on close covers the case where the player drags the
            // slider and leaves the screen without releasing over it.
            SendThresholdIfChanged();
        }

        private void Update()
        {
            RefreshFromProxy();
        }

        private void HandleInventoryUpdated()
        {
            RebuildFoodOptions();
        }

        // Only foods the player actually holds are offered. Listing the full
        // catalogue would let a player pick something they cannot stock and get
        // an "insufficient materials" rejection for no reason they can see.
        private void RebuildFoodOptions()
        {
            _offeredFoodItemIds.Clear();

            var options = new List<string>();
            IReadOnlyList<InventoryStackData> stacks = PlayerInventoryCache.Stacks;

            for (int i = 0; i < stacks.Count; i++)
            {
                InventoryStackData stack = stacks[i];
                if (stack.Total <= 0) continue;
                if (!ClientContentRegistry.IsFood(stack.ItemId)) continue;
                if (!ClientContentRegistry.TryGetItemByBaseId(stack.ItemId, out ItemEntry item)) continue;

                _offeredFoodItemIds.Add(item.Id);

                _labelBuilder.Clear();
                _labelBuilder.Append(ClientContentRegistry.GetItemDisplayName(stack.ItemId));
                _labelBuilder.Append(" (x");
                _labelBuilder.Append(stack.Total);
                _labelBuilder.Append(", heals ");
                _labelBuilder.Append(ClientContentRegistry.GetFoodHealFlatHp(item.Id));
                _labelBuilder.Append(" HP)");
                options.Add(_labelBuilder.ToString());
            }

            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (SlotFoodDropdowns == null || slot >= SlotFoodDropdowns.Length) break;
                TMP_Dropdown dropdown = SlotFoodDropdowns[slot];
                if (dropdown == null) continue;

                int previousIndex = dropdown.value;
                dropdown.ClearOptions();
                dropdown.AddOptions(options.Count > 0 ? options : new List<string> { "No cooked food in backpack" });
                dropdown.interactable = options.Count > 0;
                dropdown.value = previousIndex < dropdown.options.Count ? previousIndex : 0;
                dropdown.RefreshShownValue();
            }

            if (options.Count == 0 && StatusText != null)
            {
                StatusText.text = "You have no cooked food. Cook some at the Crafting bench first - without it your character stops fighting the first time it gets hurt.";
            }
        }

        private void RefreshFromProxy()
        {
            if (SyncProxy == null) return;

            WriteSlotContents(0, SyncProxy.VisualFood1ItemId, SyncProxy.VisualFood1Count);
            WriteSlotContents(1, SyncProxy.VisualFood2ItemId, SyncProxy.VisualFood2Count);
            WriteSlotContents(2, SyncProxy.VisualFood3ItemId, SyncProxy.VisualFood3Count);

            if (ThresholdSlider != null && _lastSentThresholdPct < 0)
            {
                // First sync only: adopt the server's value without echoing it
                // straight back as a command.
                _suppressThresholdCallback = true;
                ThresholdSlider.value = SyncProxy.VisualAutoEatThreshold;
                _suppressThresholdCallback = false;
                _lastSentThresholdPct = SyncProxy.VisualAutoEatThreshold;
                UpdateThresholdLabel(SyncProxy.VisualAutoEatThreshold);
            }

            UpdateSustainEstimate();
        }

        private void WriteSlotContents(int slot, int itemId, int count)
        {
            if (SlotContentsTexts == null || slot >= SlotContentsTexts.Length) return;
            TMP_Text label = SlotContentsTexts[slot];
            if (label == null) return;

            if (itemId <= 0 || count <= 0)
            {
                label.text = "Empty";
                return;
            }

            _labelBuilder.Clear();
            if (ClientContentRegistry.TryGetItemById(itemId, out ItemEntry item))
            {
                _labelBuilder.Append(ClientContentRegistry.GetItemDisplayName(item.BaseId));
            }
            else
            {
                _labelBuilder.Append("Item ");
                _labelBuilder.Append(itemId);
            }
            _labelBuilder.Append(" x");
            _labelBuilder.Append(count);
            label.text = _labelBuilder.ToString();
        }

        // Total healing stocked, which is the number that actually tells a
        // player whether they can leave the game running.
        private void UpdateSustainEstimate()
        {
            if (SustainEstimateText == null || SyncProxy == null) return;

            long totalHeal =
                (long)ClientContentRegistry.GetFoodHealFlatHp(SyncProxy.VisualFood1ItemId) * SyncProxy.VisualFood1Count
                + (long)ClientContentRegistry.GetFoodHealFlatHp(SyncProxy.VisualFood2ItemId) * SyncProxy.VisualFood2Count
                + (long)ClientContentRegistry.GetFoodHealFlatHp(SyncProxy.VisualFood3ItemId) * SyncProxy.VisualFood3Count;

            int totalUnits = SyncProxy.VisualFood1Count + SyncProxy.VisualFood2Count + SyncProxy.VisualFood3Count;

            _labelBuilder.Clear();
            if (totalUnits <= 0)
            {
                _labelBuilder.Append("Larder empty. Your character will stop fighting as soon as it is hurt.");
            }
            else
            {
                _labelBuilder.Append("Stocked: ");
                _labelBuilder.Append(totalUnits);
                _labelBuilder.Append(" meals, ");
                _labelBuilder.Append(totalHeal);
                _labelBuilder.Append(" total HP of healing.");
            }
            SustainEstimateText.text = _labelBuilder.ToString();
        }

        private void HandleThresholdChanged(float value)
        {
            if (_suppressThresholdCallback) return;
            UpdateThresholdLabel((int)value);
        }

        private void UpdateThresholdLabel(int thresholdPct)
        {
            if (ThresholdValueText == null) return;

            _labelBuilder.Clear();
            _labelBuilder.Append("Eat when health drops below ");
            _labelBuilder.Append(thresholdPct);
            _labelBuilder.Append('%');
            ThresholdValueText.text = _labelBuilder.ToString();
        }

        private void SendThresholdIfChanged()
        {
            if (NetworkClient == null || ThresholdSlider == null) return;

            int thresholdPct = (int)ThresholdSlider.value;
            if (thresholdPct == _lastSentThresholdPct) return;

            NetworkClient.SendAutoEatThresholdCommandZeroAlloc(thresholdPct);
            _lastSentThresholdPct = thresholdPct;
        }

        private void HandleLoadClicked(int slot)
        {
            if (NetworkClient == null) return;
            if (SlotFoodDropdowns == null || slot >= SlotFoodDropdowns.Length) return;

            TMP_Dropdown dropdown = SlotFoodDropdowns[slot];
            if (dropdown == null || _offeredFoodItemIds.Count == 0)
            {
                SetStatus("Cook some food first - there is nothing in your backpack to load.");
                return;
            }

            int selectionIndex = dropdown.value;
            if (selectionIndex < 0 || selectionIndex >= _offeredFoodItemIds.Count)
            {
                SetStatus("Select a food to load.");
                return;
            }

            int quantity = ReadQuantity(slot);
            if (quantity <= 0)
            {
                SetStatus("Enter how many meals to load.");
                return;
            }

            NetworkClient.SendStockFoodSlotCommandZeroAlloc((uint)slot, (uint)_offeredFoodItemIds[selectionIndex], (uint)quantity);
            SetStatus("Loading slot " + (slot + 1) + "...");

            // The server's response arrives as a state broadcast, but the
            // backpack totals behind the dropdown come from the REST snapshot,
            // so that has to be re-pulled explicitly.
            PlayerInventoryCache.RequestSnapshot();
        }

        private void HandleUnloadClicked(int slot)
        {
            if (NetworkClient == null) return;

            // Quantity 0 is the unload signal; the food goes back to the
            // backpack rather than being destroyed.
            NetworkClient.SendStockFoodSlotCommandZeroAlloc((uint)slot, 0u, 0u);
            SetStatus("Emptying slot " + (slot + 1) + " back into your backpack...");
            PlayerInventoryCache.RequestSnapshot();
        }

        private int ReadQuantity(int slot)
        {
            if (SlotQuantityInputs == null || slot >= SlotQuantityInputs.Length) return 0;
            TMP_InputField input = SlotQuantityInputs[slot];
            if (input == null) return 0;

            if (!int.TryParse(input.text, out int quantity)) return 0;
            return Mathf.Clamp(quantity, 0, SlotCapacity);
        }

        private void SetStatus(string message)
        {
            if (StatusText != null)
            {
                StatusText.text = message;
            }
        }
    }
}
