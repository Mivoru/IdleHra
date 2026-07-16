using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Final Production Polish, Part 1. Generic client error-feedback
    // toast - the one UI subscriber to VisualSyncProxy.OnCommandResultReceived
    // (previously nothing displayed CommandResultCode at all; a rejected
    // forge fusion, market order, bank withdraw, or mail claim was silent
    // from the player's perspective). Maps the subset of wire
    // CommandResultCode values that have a dedicated player-facing message
    // to a localized string via LocalizationMatrix and auto-hides after a
    // fixed duration. Zero-allocation: char-buffer writes only, Update()'s
    // auto-hide timer is plain float subtraction, no string concatenation/
    // interpolation/coroutines.
    //
    // Modul: Full-Stack Production Hardening Phase 3, Part 5.
    // VisualSyncProxy.ApplyCommandResultState now drains a 4-slot wire ring
    // buffer and can fire OnCommandResultReceived more than once per
    // packet - a small fixed-size (4 slot) FIFO queue here ensures each
    // rejection is actually shown to the player in turn rather than a
    // later call immediately overwriting the toast text before an earlier
    // rejection was ever rendered on screen. 4 explicit fields (not a
    // managed array/Queue<T>) to stay zero-allocation, matching the wire
    // buffer's own capacity.
    public class UiCommandResultToast : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public TextMeshProUGUI ToastText;
        public GameObject ToastRoot;
        public float DisplaySeconds = 3f;

        private readonly char[] _lineBuffer = new char[64];
        private float _remainingVisibleSeconds;

        private byte _pendingCode0;
        private byte _pendingCode1;
        private byte _pendingCode2;
        private byte _pendingCode3;
        private byte _pendingCount;

        private void OnEnable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnCommandResultReceived += HandleCommandResult;
            if (ToastRoot != null) ToastRoot.SetActive(false);
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnCommandResultReceived -= HandleCommandResult;
        }

        private void Update()
        {
            if (_remainingVisibleSeconds > 0f)
            {
                _remainingVisibleSeconds -= Time.unscaledDeltaTime;
                if (_remainingVisibleSeconds > 0f) return;

                if (_pendingCount == 0)
                {
                    if (ToastRoot != null) ToastRoot.SetActive(false);
                    return;
                }
            }
            else if (_pendingCount == 0)
            {
                return;
            }

            // Either nothing was showing and a rejection just got queued,
            // or the current toast's display window just elapsed and
            // another rejection is waiting - advance to it now so multiple
            // concurrent rejections are shown one after another instead of
            // one silently replacing another before ever reaching the
            // screen.
            byte nextCode = _pendingCode0;
            _pendingCode0 = _pendingCode1;
            _pendingCode1 = _pendingCode2;
            _pendingCode2 = _pendingCode3;
            _pendingCount--;
            DisplayToast(nextCode);
        }

        private void HandleCommandResult(byte resultCode)
        {
            if (!TryResolveLocalizationKey((CommandResultCode)resultCode, out _)) return;
            if (_pendingCount >= 4) return;

            switch (_pendingCount)
            {
                case 0: _pendingCode0 = resultCode; break;
                case 1: _pendingCode1 = resultCode; break;
                case 2: _pendingCode2 = resultCode; break;
                default: _pendingCode3 = resultCode; break;
            }
            _pendingCount++;
        }

        private void DisplayToast(byte resultCode)
        {
            if (ToastText == null) return;
            if (!TryResolveLocalizationKey((CommandResultCode)resultCode, out LocalizationKey key)) return;

            byte activeLanguage = SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
            int offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, key, _lineBuffer, 0);
            ToastText.SetCharArray(_lineBuffer, 0, offset);

            if (ToastRoot != null) ToastRoot.SetActive(true);
            _remainingVisibleSeconds = DisplaySeconds;
        }

        private static bool TryResolveLocalizationKey(CommandResultCode code, out LocalizationKey key)
        {
            switch (code)
            {
                case CommandResultCode.TransactionPending:
                    key = LocalizationKey.ErrorTransactionPending;
                    return true;
                case CommandResultCode.MaxTierReached:
                    key = LocalizationKey.ErrorMaxTierReached;
                    return true;
                case CommandResultCode.InsufficientGold:
                    key = LocalizationKey.ErrorInsufficientFunds;
                    return true;
                case CommandResultCode.InventoryFull:
                    key = LocalizationKey.ErrorInventoryFull;
                    return true;
                default:
                    key = default;
                    return false;
            }
        }
    }
}
