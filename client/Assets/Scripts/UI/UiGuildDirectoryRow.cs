using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: guild discovery, 2026-08-01. One guild in the browse list.
    //
    // Pooled and rebound like every other list row here, so it must fully
    // reset its own state on every Bind - a row that kept a stale guild name
    // would send a join request for whatever it previously displayed.
    public class UiGuildDirectoryRow : MonoBehaviour
    {
        public TMP_Text NameText;
        public TMP_Text DetailText;
        public Button JoinButton;

        private string _boundGuildName = string.Empty;
        public string BoundGuildName => _boundGuildName;

        // The panel owns the actual request; the row only reports which guild
        // was clicked, exactly as UiChatMessageRow reports its sender rather
        // than acting on it.
        public event System.Action<string> OnJoinClicked;

        public void HandleJoinClicked()
        {
            if (!string.IsNullOrEmpty(_boundGuildName))
            {
                OnJoinClicked?.Invoke(_boundGuildName);
            }
        }

        public void Bind(FolkIdle.Client.Engine.GuildDirectoryEntryData entry, int viewerLevel)
        {
            if (entry == null) return;

            _boundGuildName = entry.Name ?? string.Empty;

            if (NameText != null)
            {
                NameText.text = _boundGuildName;

                // Tier drives the colour through the shared rarity palette, so
                // a guild's standing reads the same way item and affix rarity
                // does everywhere else in the game.
                NameText.color = UiRarityPalette.GetItemRarityColor(entry.CurrentTier);
            }

            if (DetailText != null)
            {
                DetailText.text =
                    $"Members {entry.ActiveMembers}/{entry.MaxMembers}   Tier {entry.CurrentTier}   " +
                    $"MMR {entry.GuildMMR}   Tax {entry.TaxRatePct}%   Min level {entry.MinApplicationLevel}";
            }

            // Disabled rather than hidden when the player cannot join: a
            // greyed-out row still tells them the guild exists and why they are
            // not eligible, where hiding it would look like the search was
            // broken.
            bool isFull = entry.MaxMembers > 0 && entry.ActiveMembers >= entry.MaxMembers;
            bool levelLocked = viewerLevel > 0 && viewerLevel < entry.MinApplicationLevel;

            if (JoinButton != null)
            {
                JoinButton.interactable = !isFull && !levelLocked;
            }
        }
    }
}
