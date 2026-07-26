using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 16/21: active equipment slot display + unequip dispatch.
    // Event-driven only - refreshes from VisualSyncProxy.OnCharacterStateUpdated
    // (equipped item id changes) and EquipmentInventoryCache.OnSnapshotUpdated
    // (item name/tier metadata), never from an Update() loop.
    //
    // Modul: 6-slot equipment. Was two hardcoded slots, Weapon and a single
    // "Armor" that stood in for helmet, chest, gloves and boots together. All
    // six are separate now, so the panel is array-driven over SlotCount rather
    // than two copies of every field - adding a seventh slot later (the GDD's
    // helper/offhand) is then one array entry rather than another six members.
    public class UiEquipmentSlotsPanel : MonoBehaviour
    {
        // Index order matches EquipmentSlotEngine's server-side constants
        // exactly: 0 Weapon, 1 Helmet, 2 Chest, 3 Gloves, 4 Leggings, 5 Boots.
        // The unequip command sends this index straight through, so the two
        // orderings must never drift.
        public const int SlotWeapon = 0;
        public const int SlotHelmet = 1;
        public const int SlotChest = 2;
        public const int SlotGloves = 3;
        public const int SlotLeggings = 4;
        public const int SlotBoots = 5;
        public const int SlotCount = 6;

        public static readonly string[] SlotDisplayNames = { "Weapon", "Helmet", "Chest", "Gloves", "Leggings", "Boots" };

        public VisualSyncProxy SyncProxy;
        public EquipmentInventoryCache InventoryCache;
        public WebSocketClient NetworkClient;

        [Header("Slots (index order: Weapon, Helmet, Chest, Gloves, Leggings, Boots)")]
        public TextMeshProUGUI[] SlotTexts = new TextMeshProUGUI[SlotCount];
        public Button[] UnequipButtons = new Button[SlotCount];
        public GameObject[] EmptyIndicators = new GameObject[SlotCount];

        // One reusable char buffer per slot. TMP_Text.SetCharArray takes the
        // buffer directly, so nothing here allocates on a refresh.
        private readonly char[][] _slotBuffers = new char[SlotCount][];

        private void Awake()
        {
            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                _slotBuffers[slotIndex] = new char[128];

                if (UnequipButtons != null && slotIndex < UnequipButtons.Length && UnequipButtons[slotIndex] != null)
                {
                    int capturedSlot = slotIndex;
                    UnequipButtons[slotIndex].onClick.AddListener(() => HandleUnequipClicked(capturedSlot));
                }
            }
        }

        private void OnEnable()
        {
            VisualSyncProxy.OnCharacterStateUpdated += HandleStateUpdated;

            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated += HandleStateUpdated;
            }

            // Modul: caught via a live Play Mode run - requesting the
            // snapshot immediately on enable fired before UiLoginWindow's
            // async login flow had set WebSocketClient.AuthenticatorToken,
            // so the very first request went out with an empty Bearer
            // token and 401'd. OnStateConfirmed only fires once auth has
            // actually succeeded (see WebSocketClient), so wait for that
            // instead - the same signal UiLoginWindow itself waits on.
            if (NetworkClient != null)
            {
                NetworkClient.OnStateConfirmed += HandleAuthenticatedReady;
            }

            RefreshDisplay();
        }

        private void OnDisable()
        {
            VisualSyncProxy.OnCharacterStateUpdated -= HandleStateUpdated;

            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated -= HandleStateUpdated;
            }

            if (NetworkClient != null)
            {
                NetworkClient.OnStateConfirmed -= HandleAuthenticatedReady;
            }
        }

        private void HandleAuthenticatedReady()
        {
            InventoryCache?.RequestSnapshot();
        }

        private void HandleStateUpdated()
        {
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (SyncProxy == null) return;

            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                RefreshSlot(
                    GetEquippedId(slotIndex),
                    SlotTexts != null && slotIndex < SlotTexts.Length ? SlotTexts[slotIndex] : null,
                    UnequipButtons != null && slotIndex < UnequipButtons.Length ? UnequipButtons[slotIndex] : null,
                    EmptyIndicators != null && slotIndex < EmptyIndicators.Length ? EmptyIndicators[slotIndex] : null,
                    _slotBuffers[slotIndex]);
            }
        }

        private long GetEquippedId(int slotIndex)
        {
            switch (slotIndex)
            {
                case SlotWeapon: return SyncProxy.VisualEquippedWeaponId;
                case SlotHelmet: return SyncProxy.VisualEquippedHelmetId;
                case SlotChest: return SyncProxy.VisualEquippedArmorId;
                case SlotGloves: return SyncProxy.VisualEquippedGlovesId;
                case SlotLeggings: return SyncProxy.VisualEquippedLeggingsId;
                case SlotBoots: return SyncProxy.VisualEquippedBootsId;
                default: return 0L;
            }
        }

        private void RefreshSlot(long equippedId, TextMeshProUGUI slotText, Button unequipButton, GameObject emptyIndicator, char[] buffer)
        {
            bool isEmpty = equippedId <= 0L;

            if (emptyIndicator != null)
            {
                emptyIndicator.SetActive(isEmpty);
            }

            if (unequipButton != null)
            {
                unequipButton.gameObject.SetActive(!isEmpty);
                unequipButton.interactable = !isEmpty;
            }

            if (slotText == null) return;

            if (isEmpty)
            {
                slotText.SetCharArray(System.Array.Empty<char>(), 0, 0);
                return;
            }

            ForgeEquipmentInstanceData item = FindOwnedItem(equippedId);
            int offset;
            if (item != null)
            {
                offset = WriteTextToBuffer(buffer, 0, "T");
                offset = WriteIntToBuffer(buffer, offset, item.QualityTier);
                offset = WriteTextToBuffer(buffer, offset, " - ");
                offset = WriteTextToBuffer(buffer, offset, item.BaseItemId);
            }
            else
            {
                offset = WriteLongToBuffer(buffer, 0, equippedId);
            }

            slotText.SetCharArray(buffer, 0, offset);
        }

        private ForgeEquipmentInstanceData FindOwnedItem(long itemId)
        {
            if (InventoryCache == null) return null;

            IReadOnlyList<ForgeEquipmentInstanceData> owned = InventoryCache.OwnedEquipment;
            for (int i = 0; i < owned.Count; i++)
            {
                if (owned[i].Id == itemId) return owned[i];
            }
            return null;
        }

        // Disables the button immediately so a double-click cannot dispatch the
        // unequip command twice before the next state packet settles the slot.
        private void HandleUnequipClicked(int slotIndex)
        {
            if (NetworkClient == null) return;

            // Disabled immediately so a double-click cannot dispatch the
            // unequip twice before the next state packet settles the slot.
            if (UnequipButtons != null && slotIndex < UnequipButtons.Length && UnequipButtons[slotIndex] != null)
            {
                UnequipButtons[slotIndex].interactable = false;
            }

            NetworkClient.SendUnequipItemCommandZeroAlloc(slotIndex);
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            return (int)WriteLongToBuffer(buffer, offset, value);
        }

        private static int WriteLongToBuffer(char[] buffer, int offset, long value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            if (value < 0)
            {
                buffer[offset++] = '-';
                value = -value;
            }

            long temp = value;
            int length = 0;
            while (temp > 0)
            {
                temp /= 10;
                length++;
            }

            int endOffset = offset + length;
            temp = value;
            for (int i = endOffset - 1; i >= offset; i--)
            {
                buffer[i] = (char)('0' + (temp % 10));
                temp /= 10;
            }
            return endOffset;
        }
    }
}
