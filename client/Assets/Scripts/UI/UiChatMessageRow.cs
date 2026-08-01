using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Single fixed chat row, owned and repeatedly rebound by UiChatWindow's
    // virtualization - never instantiated or destroyed outside that window's
    // one-time row setup. Bind() is called every time this row's slot is
    // remapped to a different history entry (new live message arriving, or
    // the user scrolling), so it uses the same zero-allocation char-buffer
    // text-write convention as other frequently-rebound pooled rows in this
    // codebase (see UiMarketListingRow.Bind) rather than string
    // concatenation.
    public class UiChatMessageRow : MonoBehaviour
    {
        public TMP_Text RowText;

        // Modul: bounded so a hostile/overlong username can never push the
        // message itself out of the fixed row buffer entirely.
        private const int MaxDisplayNameLength = 24;

        private static readonly Color OwnMessageColor = new Color(0.72f, 0.90f, 1f, 1f);
        private static readonly Color OtherMessageColor = Color.white;

        private readonly char[] _rowUiBuffer = new char[200];

        // Modul: Full-Stack Social Layer, Part 5. Click-to-action hook -
        // the sender id currently bound to this row, and an event
        // UiChatWindow subscribes to once at row-creation time so a click
        // on the row (wired to HandleNameClicked via a Button component in
        // the row prefab's Inspector, standard Unity practice) can open
        // the player context window (Profile Inspection / Add Friend /
        // Block User) for whoever is actually displayed in this slot right
        // now, not whoever it was bound to when the click handler was
        // wired up.
        private long _boundSenderPlayerId;
        public long BoundSenderPlayerId => _boundSenderPlayerId;
        public event System.Action<long> OnNameClicked;

        // Modul: high-rarity announcements, 2026-08-01. Shown only on
        // announcement rows; hidden on every ordinary message so the log does
        // not sprout a button per line.
        public UnityEngine.UI.Button CongratulateButton;
        public event System.Action OnCongratulateClicked;

        public void HandleCongratulateClicked()
        {
            OnCongratulateClicked?.Invoke();
        }

        public void HandleNameClicked()
        {
            if (_boundSenderPlayerId != 0)
            {
                OnNameClicked?.Invoke(_boundSenderPlayerId);
            }
        }

        // Modul: UI rework. senderDisplayName may be null - PlayerNameCache
        // resolves ids to usernames asynchronously, so a brand-new sender
        // renders as "Player #1042" for the one frame before the batched
        // lookup lands and the window rebinds. isOwnMessage tints the row so
        // a player can pick their own lines out of the log at a glance,
        // which matters most in the whisper view where both sides of the
        // conversation share one column.
        public void Bind(long senderPlayerId, string senderDisplayName, bool isOwnMessage, string messageText)
        {
            _boundSenderPlayerId = senderPlayerId;

            // Rows are pooled and rebound, so an ordinary message reusing a slot
            // that last held an announcement must actively clear both the button
            // and any glow the rarity styling left behind.
            if (CongratulateButton != null) CongratulateButton.gameObject.SetActive(false);
            ClearRarityStyling();

            if (RowText == null) return;

            RowText.color = isOwnMessage ? OwnMessageColor : OtherMessageColor;

            int offset;
            if (string.IsNullOrEmpty(senderDisplayName))
            {
                offset = WriteTextToBuffer(_rowUiBuffer, 0, "Player #");
                offset = WriteLongToBuffer(_rowUiBuffer, offset, senderPlayerId);
            }
            else
            {
                int nameLength = senderDisplayName.Length;
                if (nameLength > MaxDisplayNameLength) nameLength = MaxDisplayNameLength;

                offset = 0;
                for (int i = 0; i < nameLength; i++)
                {
                    _rowUiBuffer[offset++] = senderDisplayName[i];
                }
            }

            offset = WriteTextToBuffer(_rowUiBuffer, offset, ": ");

            int remaining = _rowUiBuffer.Length - offset;
            int messageLength = messageText.Length;
            if (messageLength > remaining)
            {
                messageLength = remaining;
            }

            for (int i = 0; i < messageLength; i++)
            {
                _rowUiBuffer[offset++] = messageText[i];
            }

            RowText.SetCharArray(_rowUiBuffer, 0, offset);
        }

        public void Clear()
        {
            _boundSenderPlayerId = 0;
            if (RowText == null) return;
            RowText.SetCharArray(_rowUiBuffer, 0, 0);
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
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
        // Modul: high-rarity announcements, 2026-08-01.
        //
        // Payload is "playerId|rarity|affixId|magnitude" as built by
        // AffixRerollEngine.FormatRarityAnnouncement. Parsed rather than
        // displayed raw so the client can colour by rarity and localise the
        // wording later without a server change.
        //
        // A malformed payload falls back to a plain message rather than
        // throwing - a chat row is not worth crashing the window over, and the
        // server could be a version ahead during a rollout.
        public void BindAnnouncement(string payload, string senderDisplayName)
        {
            if (RowText == null) return;

            if (!TryParseAnnouncement(payload, out long playerId, out int rarity, out string affixId, out int magnitude))
            {
                Bind(0L, senderDisplayName, false, payload);
                return;
            }

            _boundSenderPlayerId = playerId;

            string who = string.IsNullOrEmpty(senderDisplayName) ? ("Player #" + playerId) : senderDisplayName;
            string rarityName = UiRarityPalette.GetAffixRarityName(rarity);
            string statLabel = FolkIdle.Client.Network.ClientAffixRegistry.Describe(affixId, magnitude);

            RowText.text = who + " rerolled " + statLabel + " to " + rarityName + "!";
            UiRarityPalette.ApplyAffixRarity(RowText, rarity);

            if (CongratulateButton != null) CongratulateButton.gameObject.SetActive(true);
        }

        private void ClearRarityStyling()
        {
            if (RowText == null) return;

            UiRarityGlow glow = RowText.GetComponent<UiRarityGlow>();
            if (glow != null)
            {
                glow.enabled = false;
                glow.ResetToPlain();
            }
        }

        private static bool TryParseAnnouncement(string payload, out long playerId, out int rarity, out string affixId, out int magnitude)
        {
            playerId = 0L;
            rarity = 1;
            affixId = string.Empty;
            magnitude = 0;

            if (string.IsNullOrEmpty(payload)) return false;

            string[] parts = payload.Split('|');
            if (parts.Length != 4) return false;

            if (!long.TryParse(parts[0], out playerId)) return false;
            if (!int.TryParse(parts[1], out rarity)) return false;
            affixId = parts[2];
            if (!int.TryParse(parts[3], out magnitude)) return false;

            return true;
        }

    }
}
