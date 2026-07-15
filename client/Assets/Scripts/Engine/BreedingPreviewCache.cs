using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class GenePreviewLocusData
    {
        public string LocusName { get; set; } = string.Empty;
        public int ParentPaternalDominant { get; set; }
        public int ParentMaternalDominant { get; set; }
        public int PredictedMinDominant { get; set; }
        public int PredictedMaxDominant { get; set; }
        public double MutationChancePct { get; set; }
    }

    public class BreedingPreviewData
    {
        public bool IsEligible { get; set; }
        public string IneligibleReason { get; set; } = string.Empty;
        public bool IsInbredRisk { get; set; }
        public long BreedingCostGold { get; set; }
        public bool HasSufficientGold { get; set; }
        public List<GenePreviewLocusData> Loci { get; set; } = new List<GenePreviewLocusData>();
    }

    // Modul 13.4.3: on-demand, read-only preview of a candidate breeding pair
    // - see NetworkBroadcastSystem.HandleBreedingPreview on the server, which
    // computes the exact achievable gene range via GeneticSplicingEngine.
    // PreviewLocus without writing anything to the DB. Never polls on its
    // own; the UI must call RequestPreview explicitly whenever both parent
    // slots are filled or changed.
    public static class BreedingPreviewCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static BreedingPreviewData Preview { get; private set; }

        public static event Action OnPreviewCacheUpdated;

        private static bool _requestInFlight;
        private static string _pendingRequestKey = string.Empty;

        public static void ClearPreview()
        {
            Preview = null;
            OnPreviewCacheUpdated?.Invoke();
        }

        public static void RequestPreview(string paternalId, string maternalId)
        {
            if (_requestInFlight) return;
            _ = FetchPreviewAsync(paternalId, maternalId);
        }

        public static async Task FetchPreviewAsync(string paternalId, string maternalId)
        {
            if (_requestInFlight) return;

            string requestKey = paternalId + ":" + maternalId;
            _pendingRequestKey = requestKey;
            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/breeding/preview?paternalId={paternalId}&maternalId={maternalId}");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                // A newer request superseded this one while it was in flight -
                // discard this stale response rather than overwriting a more
                // recent selection's preview.
                if (_pendingRequestKey != requestKey) return;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Breeding preview request failed: {request.error}");
                    Preview = null;
                    OnPreviewCacheUpdated?.Invoke();
                    return;
                }

                BreedingPreviewData snapshot = JsonSerializer.Deserialize<BreedingPreviewData>(request.downloadHandler.text);
                Preview = snapshot;
                OnPreviewCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Breeding preview parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}
