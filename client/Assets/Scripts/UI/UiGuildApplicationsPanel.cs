using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit fix. JoinGuildAsync has always filed a
    // GuildApplication row for Application-Required guilds, but nothing
    // anywhere - server or client - ever reviewed one, so any guild gated
    // behind "application required" was a black hole for new members.
    // Rows are pooled via UIComponentPool, mirroring UiGuildRosterPanel;
    // Approve/Reject dispatch authenticated JSON POSTs, mirroring
    // UiGuildCreatePanel's BuildAuthorizedJsonPostRequest pattern (this is
    // a leader-administrative action, not a per-tick command, matching how
    // guild create/join are already HTTP rather than wire commands).
    public class UiGuildApplicationsPanel : MonoBehaviour
    {
        // Modul: server config. Reads the one configured server address rather
        // than owning a copy - see ClientServerConfig.
        public string ServerBaseUrl => FolkIdle.Client.Network.ClientServerConfig.BaseUrl;

        [Header("Guild Applications HUD")]
        public Transform RowContainer;
        public UiGuildApplicationEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 10;
        public TextMeshProUGUI HeaderText;
        public TextMeshProUGUI StatusText;

        private sealed class ApplicationActionRequestBody
        {
            [JsonPropertyName("applicationId")] public long ApplicationId { get; set; }
        }

        private sealed class ApplicationActionResponseBody
        {
            public bool Success { get; set; }
        }

        private UIComponentPool<UiGuildApplicationEntryRow> _rowPool;
        private readonly List<UiGuildApplicationEntryRow> _activeRows = new List<UiGuildApplicationEntryRow>();
        private bool _isDirty;
        private bool _actionInFlight;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiGuildApplicationEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            GuildApplicationsCache.OnGuildApplicationsCacheUpdated += HandleCacheUpdated;
            GuildApplicationsCache.Refresh();
        }

        private void OnDisable()
        {
            GuildApplicationsCache.OnGuildApplicationsCacheUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty) return;

            RefreshRows();
            _isDirty = false;
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void RefreshRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<GuildApplicationEntryData> entries = GuildApplicationsCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                GuildApplicationEntryData entry = entries[i];
                UiGuildApplicationEntryRow row = _rowPool.Spawn();
                row.Bind(entry.Id, entry.Username, entry.ApplicantLevel, HandleApproveClicked, HandleRejectClicked);
                _activeRows.Add(row);
            }
        }

        private async void HandleApproveClicked(long applicationId)
        {
            await SubmitActionAsync("/api/v1/guild/applications/approve", applicationId, "Approved.", "Approval failed.");
        }

        private async void HandleRejectClicked(long applicationId)
        {
            await SubmitActionAsync("/api/v1/guild/applications/reject", applicationId, "Rejected.", "Rejection failed.");
        }

        private async Task SubmitActionAsync(string path, long applicationId, string successMessage, string failureMessage)
        {
            if (_actionInFlight) return;

            _actionInFlight = true;
            SetStatus($"{(path.EndsWith("approve") ? "Approving" : "Rejecting")}...");

            try
            {
                string json = JsonSerializer.Serialize(new ApplicationActionRequestBody { ApplicationId = applicationId });
                using UnityWebRequest request = BuildAuthorizedJsonPostRequest(path, json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"{failureMessage} {request.error}");
                    return;
                }

                ApplicationActionResponseBody response = JsonSerializer.Deserialize<ApplicationActionResponseBody>(request.downloadHandler.text);
                SetStatus(response != null && response.Success ? successMessage : failureMessage);
                GuildApplicationsCache.Refresh();
            }
            catch (Exception ex)
            {
                SetStatus($"{failureMessage} {ex.Message}");
            }
            finally
            {
                _actionInFlight = false;
            }
        }

        private UnityWebRequest BuildAuthorizedJsonPostRequest(string path, string json)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            UnityWebRequest request = new UnityWebRequest($"{ServerBaseUrl}{path}", "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");
            return request;
        }

        private void SetStatus(string message)
        {
            if (StatusText != null) StatusText.text = message;
        }
    }
}
