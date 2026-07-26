using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Inventory screen. One pooled row, used for both halves of the
    // list (equipment instances and material stacks) - they differ only in
    // what the three text fields say, so one row type avoids a second pool
    // and a second prefab for no benefit. Bind() runs only when the panel
    // rebuilds, never from Update().
    public class UiInventoryEntryRow : MonoBehaviour
    {
        public Image IconImage;
        public Image EquippedMarker;
        public TMP_Text NameText;
        public TMP_Text DetailText;
        public TMP_Text QuantityText;

        // Modul: interactive inventory. Equip action for backpack gear. Only
        // equipment rows show it - a material stack has nothing to equip -
        // so it is hidden per-bind rather than existing on every row.
        public Button ActionButton;
        public TMP_Text ActionButtonLabel;

        private static readonly Color EquippedTint = new Color(0.22f, 0.30f, 0.24f, 1f);
        private static readonly Color NormalTint = new Color(0.17f, 0.17f, 0.22f, 1f);

        private long _instanceId;
        private Action<long> _onActionClicked;

        private void Awake()
        {
            if (ActionButton != null)
            {
                ActionButton.onClick.AddListener(HandleActionClicked);
            }
        }

        public void Bind(string displayName, string detail, string quantity, bool isEquipped, Sprite icon)
        {
            BindWithAction(displayName, detail, quantity, isEquipped, icon, 0L, null, null);
        }

        public void BindWithAction(string displayName, string detail, string quantity, bool isEquipped, Sprite icon, long instanceId, string actionLabel, Action<long> onActionClicked)
        {
            _instanceId = instanceId;
            _onActionClicked = onActionClicked;

            if (ActionButton != null)
            {
                bool showAction = onActionClicked != null && !string.IsNullOrEmpty(actionLabel);
                ActionButton.gameObject.SetActive(showAction);
                if (showAction && ActionButtonLabel != null)
                {
                    ActionButtonLabel.text = actionLabel;
                }
            }

            if (NameText != null) NameText.text = displayName;
            if (DetailText != null) DetailText.text = detail;
            if (QuantityText != null) QuantityText.text = quantity;

            if (EquippedMarker != null)
            {
                EquippedMarker.gameObject.SetActive(isEquipped);
            }

            Image background = GetComponent<Image>();
            if (background != null)
            {
                background.color = isEquipped ? EquippedTint : NormalTint;
            }

            if (IconImage != null)
            {
                IconImage.sprite = icon;
                // A null sprite on an enabled Image draws an opaque white
                // box, which reads as broken art rather than as "no art".
                IconImage.enabled = icon != null;
            }
        }

        private void HandleActionClicked()
        {
            _onActionClicked?.Invoke(_instanceId);
        }
    }
}
