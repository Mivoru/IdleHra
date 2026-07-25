using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Single pooled row for UiGuildApplicationsPanel. Bind() is only called
    // when the owning panel rebuilds its visible list, never from an
    // Update() loop - mirrors UiMarketListingRow's exact shape.
    public class UiGuildApplicationEntryRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button ApproveButton;
        public Button RejectButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private long _applicationId;
        private Action<long> _onApproveClicked;
        private Action<long> _onRejectClicked;

        private void Awake()
        {
            if (ApproveButton != null)
            {
                ApproveButton.onClick.AddListener(HandleApproveClicked);
            }

            if (RejectButton != null)
            {
                RejectButton.onClick.AddListener(HandleRejectClicked);
            }
        }

        public void Bind(long applicationId, string username, int applicantLevel, Action<long> onApproveClicked, Action<long> onRejectClicked)
        {
            _applicationId = applicationId;
            _onApproveClicked = onApproveClicked;
            _onRejectClicked = onRejectClicked;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, username);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  Lv ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, applicantLevel);
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandleApproveClicked()
        {
            _onApproveClicked?.Invoke(_applicationId);
        }

        private void HandleRejectClicked()
        {
            _onRejectClicked?.Invoke(_applicationId);
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

            int temp = value;
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
