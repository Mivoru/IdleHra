using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Crafting Tree screen. One recipe. Pooled by
    // UiCraftingTreePanel; Bind() runs only on a rebuild, never per frame.
    public class UiCraftingRecipeRow : MonoBehaviour
    {
        public Image IconImage;
        public TMP_Text NameText;
        public TMP_Text RequirementText;
        public TMP_Text GateText;
        public Button CraftButton;
        public TMP_Text CraftButtonLabel;

        private static readonly Color AffordableTint = new Color(0.28f, 0.52f, 0.34f, 1f);
        private static readonly Color BlockedTint = new Color(0.30f, 0.30f, 0.34f, 1f);
        private static readonly Color RequirementMetColor = new Color(0.72f, 0.90f, 0.72f, 1f);
        private static readonly Color RequirementShortColor = new Color(0.95f, 0.60f, 0.55f, 1f);

        private int _resultItemId;
        private Action<int> _onCraftClicked;

        private void Awake()
        {
            if (CraftButton != null)
            {
                CraftButton.onClick.AddListener(HandleCraftClicked);
            }
        }

        public void Bind(int resultItemId, string displayName, string requirementText, bool hasMaterials, bool levelMet, int requiredLevel, Sprite icon, Action<int> onCraftClicked)
        {
            _resultItemId = resultItemId;
            _onCraftClicked = onCraftClicked;

            if (NameText != null) NameText.text = displayName;

            if (RequirementText != null)
            {
                RequirementText.text = requirementText;
                RequirementText.color = hasMaterials ? RequirementMetColor : RequirementShortColor;
            }

            if (GateText != null)
            {
                // The two blocking reasons are reported separately and
                // explicitly - "you are level 8, this needs 10" is actionable
                // in a way that a greyed-out button alone is not.
                GateText.text = levelMet ? string.Empty : "Requires level " + requiredLevel;
                GateText.gameObject.SetActive(!levelMet);
            }

            bool craftable = hasMaterials && levelMet;

            if (CraftButton != null)
            {
                CraftButton.interactable = craftable;
                if (CraftButton.targetGraphic is Image background)
                {
                    background.color = craftable ? AffordableTint : BlockedTint;
                }
            }

            if (CraftButtonLabel != null)
            {
                CraftButtonLabel.text = craftable ? "Craft" : (levelMet ? "Short" : "Locked");
            }

            if (IconImage != null)
            {
                IconImage.sprite = icon;
                IconImage.enabled = icon != null;
            }
        }

        private void HandleCraftClicked()
        {
            _onCraftClicked?.Invoke(_resultItemId);
        }
    }
}
