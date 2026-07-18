using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 16/21: active weapon/armor slot display + unequip dispatch.
    // Event-driven only - refreshes from VisualSyncProxy.OnCharacterStateUpdated
    // (equipped item id changes) and EquipmentInventoryCache.OnSnapshotUpdated
    // (item name/tier metadata), never from an Update() loop.
    public class UiEquipmentSlotsPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public EquipmentInventoryCache InventoryCache;
        public WebSocketClient NetworkClient;

        [Header("Weapon Slot")]
        public TextMeshProUGUI WeaponSlotText;
        public Button UnequipWeaponButton;
        public GameObject WeaponEmptyIndicator;

        [Header("Armor Slot")]
        public TextMeshProUGUI ArmorSlotText;
        public Button UnequipArmorButton;
        public GameObject ArmorEmptyIndicator;

        private readonly char[] _weaponBuffer = new char[128];
        private readonly char[] _armorBuffer = new char[128];

        private void Awake()
        {
            if (UnequipWeaponButton != null)
            {
                UnequipWeaponButton.onClick.AddListener(HandleUnequipWeaponClicked);
            }

            if (UnequipArmorButton != null)
            {
                UnequipArmorButton.onClick.AddListener(HandleUnequipArmorClicked);
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

            long weaponId = SyncProxy.VisualEquippedWeaponId;
            long armorId = SyncProxy.VisualEquippedArmorId;

            RefreshSlot(weaponId, WeaponSlotText, UnequipWeaponButton, WeaponEmptyIndicator, _weaponBuffer);
            RefreshSlot(armorId, ArmorSlotText, UnequipArmorButton, ArmorEmptyIndicator, _armorBuffer);
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
        private void HandleUnequipWeaponClicked()
        {
            if (NetworkClient == null) return;

            if (UnequipWeaponButton != null)
            {
                UnequipWeaponButton.interactable = false;
            }

            NetworkClient.SendUnequipItemCommandZeroAlloc(false);
        }

        private void HandleUnequipArmorClicked()
        {
            if (NetworkClient == null) return;

            if (UnequipArmorButton != null)
            {
                UnequipArmorButton.interactable = false;
            }

            NetworkClient.SendUnequipItemCommandZeroAlloc(true);
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
