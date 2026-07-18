using System;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class LoginBonusStateData
    {
        public int CurrentStreakDay { get; set; }
        public bool CreditedToday { get; set; }
        public long[] WeeklyGoldSchedule { get; set; } = Array.Empty<long>();
        public int Day7DiamondBonus { get; set; }
    }

    // Modul: UI audit follow-up. On-demand snapshot cache for the player's
    // real daily-login streak state, backed by
    // NetworkBroadcastSystem.HandleLoginBonusState - mirrors
    // AchievementsStateCache's exact "state" (not list) pattern. The reward
    // itself is granted server-side at login time by DailyLoginRewardEngine,
    // not by anything this cache or its UI triggers - this is read-only.
    public static class LoginBonusCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static event Action<LoginBonusStateData> OnLoginBonusStateUpdated;

        private static bool _requestInFlight;

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = FetchAsync();
        }

        private static async Task FetchAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/login-bonus/state");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Login bonus state request failed: {request.error}");
                    return;
                }

                LoginBonusStateData data = JsonSerializer.Deserialize<LoginBonusStateData>(request.downloadHandler.text);
                if (data == null) return;

                OnLoginBonusStateUpdated?.Invoke(data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Login bonus state parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}
