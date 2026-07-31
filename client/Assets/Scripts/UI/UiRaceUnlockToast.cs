using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: race unlock feedback. Announces a newly unlocked playable race.
    //
    // Killing a region boss for the first time grants a male/female breeding
    // pair of a new race - a genuinely significant, once-per-region event that
    // the player was never told about. The only evidence was two extra
    // characters quietly appearing in the roster, with nothing naming the race
    // or explaining where they came from.
    //
    // Works off the server's monotonic ownership MASK rather than an event
    // packet, so it is robust in the two ways an event is not: a grant that
    // lands while the socket is down is still announced at the next login, and
    // a mask that arrives twice (reconnect, duplicate packet) announces once.
    // The first mask seen in a session is a baseline, never an announcement -
    // otherwise every login would replay every race the account has ever
    // unlocked.
    public class UiRaceUnlockToast : MonoBehaviour
    {
        // Index is (raceId - 1). Human is index 0 and is never announced: every
        // account starts with it, so its bit is set from the first packet.
        private static readonly string[] RaceNames =
        {
            "Human",
            "Vila",
            "Draugr",
            "Kobold",
            "Vodnik",
            "Moosleute"
        };

        public VisualSyncProxy SyncProxy;
        public TextMeshProUGUI ToastText;
        public GameObject ToastRoot;
        public float DisplaySeconds = 6f;

        // "Unlocked the Moosleute! A breeding pair has joined your roster." is
        // the longest line this can produce.
        private readonly char[] _lineBuffer = new char[96];

        private byte _lastSeenMask;
        private bool _hasBaseline;
        private float _remainingVisibleSeconds;

        private void Awake()
        {
            if (ToastRoot != null) ToastRoot.SetActive(false);
        }

        private void Update()
        {
            if (_remainingVisibleSeconds > 0f)
            {
                _remainingVisibleSeconds -= Time.deltaTime;
                if (_remainingVisibleSeconds <= 0f && ToastRoot != null)
                {
                    ToastRoot.SetActive(false);
                }
            }

            if (SyncProxy == null) return;

            byte mask = SyncProxy.VisualUnlockedRaceBitmask;

            // A zero mask means no packet has landed yet. The server always
            // sets at least the Human bit, so zero is never a real value and
            // must not be taken as a baseline - doing so would make the first
            // real packet look like five simultaneous unlocks.
            if (mask == 0) return;

            if (!_hasBaseline)
            {
                _lastSeenMask = mask;
                _hasBaseline = true;
                return;
            }

            if (mask == _lastSeenMask) return;

            int newlySetBits = mask & ~_lastSeenMask;
            _lastSeenMask = mask;
            if (newlySetBits == 0) return;

            // Announce the highest new race rather than looping: two races
            // cannot be unlocked in the same tick (one boss, one region), and
            // if a desync ever delivered several at once, stacking toasts on
            // top of each other would show only the last one anyway.
            for (int raceIndex = RaceNames.Length - 1; raceIndex >= 1; raceIndex--)
            {
                if ((newlySetBits & (1 << raceIndex)) == 0) continue;

                ShowToast(RaceNames[raceIndex]);
                return;
            }
        }

        private void ShowToast(string raceName)
        {
            if (ToastText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "Unlocked the ");
                offset = WriteTextToBuffer(_lineBuffer, offset, raceName);
                offset = WriteTextToBuffer(_lineBuffer, offset, "! A breeding pair has joined your roster.");
                ToastText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (ToastRoot != null) ToastRoot.SetActive(true);
            _remainingVisibleSeconds = DisplaySeconds;

            GameAudioDirector.Play(GameSfx.RaceUnlocked);
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }
    }
}
