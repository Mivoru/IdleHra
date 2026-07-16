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
    public class UiCommandResultToast : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public TextMeshProUGUI ToastText;
        public GameObject ToastRoot;
        public float DisplaySeconds = 3f;

        private readonly char[] _lineBuffer = new char[64];
        private float _remainingVisibleSeconds;

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
            if (_remainingVisibleSeconds <= 0f) return;

            _remainingVisibleSeconds -= Time.unscaledDeltaTime;
            if (_remainingVisibleSeconds <= 0f && ToastRoot != null)
            {
                ToastRoot.SetActive(false);
            }
        }

        private void HandleCommandResult(byte resultCode)
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
