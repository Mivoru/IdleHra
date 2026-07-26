using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: UI rework. One monster in the selected location's roster.
    // Pooled by UiCombatLocationPanel via UIComponentPool, the same pattern
    // every other list in this codebase uses. Bind() only runs when the
    // panel rebuilds its list (location changed, codex data arrived), never
    // from Update().
    public class UiCombatMonsterRow : MonoBehaviour
    {
        public Button SelectButton;
        public Image IconImage;
        public Image SelectedHighlight;
        public Image BossBadge;
        public TMP_Text NameText;
        public TMP_Text StatsText;
        public TMP_Text KillsText;

        private static readonly Color NormalTint = new Color(0.17f, 0.17f, 0.22f, 1f);
        private static readonly Color BossTint = new Color(0.34f, 0.14f, 0.16f, 1f);

        private int _monsterId;
        private Action<int> _onSelected;

        private void Awake()
        {
            if (SelectButton != null)
            {
                SelectButton.onClick.AddListener(HandleSelectClicked);
            }
        }

        public void Bind(int monsterId, string monsterName, int maxHp, int attackPower, long killCount, bool isBoss, bool isSelected, Sprite icon, Action<int> onSelected)
        {
            _monsterId = monsterId;
            _onSelected = onSelected;

            if (NameText != null)
            {
                NameText.text = isBoss ? monsterName + "  (BOSS)" : monsterName;
            }

            if (StatsText != null)
            {
                StatsText.text = FormatCompact(maxHp) + " HP   -   " + FormatCompact(attackPower) + " ATK";
            }

            if (KillsText != null)
            {
                KillsText.text = killCount > 0 ? killCount + " slain" : "Undiscovered";
                KillsText.color = killCount > 0 ? new Color(0.7f, 0.9f, 0.7f, 1f) : new Color(1f, 1f, 1f, 0.35f);
            }

            if (IconImage != null)
            {
                IconImage.sprite = icon;
                // A missing sprite would otherwise render as an opaque white
                // box, which reads as broken art rather than as "no art yet".
                IconImage.enabled = icon != null;
            }

            if (BossBadge != null)
            {
                BossBadge.gameObject.SetActive(isBoss);
            }

            if (SelectButton != null && SelectButton.targetGraphic is Image background)
            {
                background.color = isBoss ? BossTint : NormalTint;
            }

            if (SelectedHighlight != null)
            {
                SelectedHighlight.gameObject.SetActive(isSelected);
            }
        }

        private void HandleSelectClicked()
        {
            _onSelected?.Invoke(_monsterId);
        }

        // Monster HP spans 69 to 1.5 billion across the content table, so a
        // raw number is unreadable at the high end and the column would jump
        // around in width. Same convention the World Boss panel uses.
        public static string FormatCompact(long value)
        {
            if (value >= 1_000_000_000L) return (value / 1_000_000_000d).ToString("0.##") + "B";
            if (value >= 1_000_000L) return (value / 1_000_000d).ToString("0.##") + "M";
            if (value >= 1_000L) return (value / 1_000d).ToString("0.##") + "K";
            return value.ToString();
        }
    }
}
