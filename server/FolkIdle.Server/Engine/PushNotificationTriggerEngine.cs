using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public sealed class PushNotificationTriggerEngine
    {
        public const string DelayedTriggerZSetKey = "global:delayed_push_triggers";
        private const int MaxTriggersPerPoll = 128;
        private const string PopExpiredScript = "local items = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, ARGV[2]); for i = 1, #items do redis.call('ZREM', KEYS[1], items[i]); end; return items;";

        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer _redis;
        private readonly ConcurrentQueue<OutboundPushRequest> _outboundQueue = new();
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _oauthLock = new(1, 1);

        private volatile bool _isRunning;
        private string _cachedAccessToken = string.Empty;
        private long _cachedAccessTokenExpiresAt;

        public PushNotificationTriggerEngine(IServiceProvider serviceProvider, IConnectionMultiplexer redis)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        public static RedisKey PushTokenCacheKey(long playerId) => $"player:{playerId}:push_tokens";

        public void StartCron()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _ = Task.Run(RunAsync);
        }

        public void QueueDeviceRegistration(long playerId, byte[] deviceTokenRaw, byte platformFamily)
        {
            _ = Task.Run(async () => await RegisterDeviceAsync(playerId, deviceTokenRaw, platformFamily));
        }

        public async Task ScheduleTriggerAsync(long playerId, long targetEpochTimestamp, byte triggerType, string payloadCode)
        {
            if (!_redis.IsConnected || playerId <= 0)
            {
                return;
            }

            string payload = $"{playerId}|{triggerType}|{payloadCode}";
            await _redis.GetDatabase().SortedSetAddAsync(DelayedTriggerZSetKey, payload, targetEpochTimestamp);
        }

        private async Task RunAsync()
        {
            while (_isRunning)
            {
                try
                {
                    await PollExpiredTriggersAsync();
                    await DrainOutboundQueueAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Push notification trigger loop failed: {ex.Message}");
                }

                GlobalEngineState.NotificationQueueStateLength = Math.Min(255, _outboundQueue.Count);
                await Task.Delay(1000);
            }
        }

        private async Task RegisterDeviceAsync(long playerId, byte[] deviceTokenRaw, byte platformFamily)
        {
            if (playerId <= 0 || deviceTokenRaw.Length != 64 || platformFamily == 0 || platformFamily > 2)
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var existing = await db.PlayerDeviceRegistrations
                    .FromSqlRaw("SELECT * FROM \"PlayerDeviceRegistrations\" WHERE \"PlayerId\" = {0} AND \"DeviceTokenRaw\" = {1} FOR UPDATE", playerId, deviceTokenRaw)
                    .SingleOrDefaultAsync();

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (existing == null)
                {
                    existing = new PlayerDeviceRegistration
                    {
                        PlayerId = playerId,
                        DeviceTokenRaw = deviceTokenRaw,
                        PlatformFamily = platformFamily,
                        TimestampRegistered = now
                    };
                    db.PlayerDeviceRegistrations.Add(existing);
                }
                else
                {
                    existing.PlatformFamily = platformFamily;
                    existing.TimestampRegistered = now;
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                if (_redis.IsConnected)
                {
                    string tokenKey = Convert.ToHexString(deviceTokenRaw);
                    await _redis.GetDatabase().HashSetAsync(PushTokenCacheKey(playerId), tokenKey, (int)platformFamily);
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Push device registration failed for player {playerId}: {ex.Message}");
            }
        }

        private async Task PollExpiredTriggersAsync()
        {
            if (!_redis.IsConnected)
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            RedisResult result = await _redis.GetDatabase().ScriptEvaluateAsync(
                PopExpiredScript,
                new RedisKey[] { DelayedTriggerZSetKey },
                new RedisValue[] { now, MaxTriggersPerPoll });

            RedisResult[] items = (RedisResult[]?)result ?? Array.Empty<RedisResult>();
            for (int i = 0; i < items.Length; i++)
            {
                string payload = items[i].ToString();
                if (TryParseTrigger(payload, out long playerId, out byte triggerType, out string payloadCode))
                {
                    await EnqueueOutboundRequestsAsync(playerId, triggerType, payloadCode);
                }
            }
        }

        private async Task EnqueueOutboundRequestsAsync(long playerId, byte triggerType, string payloadCode)
        {
            var tokens = await LoadDeviceTokensAsync(playerId);
            for (int i = 0; i < tokens.Count; i++)
            {
                _outboundQueue.Enqueue(new OutboundPushRequest
                {
                    PlayerId = playerId,
                    DeviceToken = tokens[i].Token,
                    PlatformFamily = tokens[i].PlatformFamily,
                    TriggerType = triggerType,
                    PayloadCode = payloadCode
                });
            }
        }

        private async Task<List<DeviceTokenDescriptor>> LoadDeviceTokensAsync(long playerId)
        {
            if (_redis.IsConnected)
            {
                HashEntry[] cached = await _redis.GetDatabase().HashGetAllAsync(PushTokenCacheKey(playerId));
                if (cached.Length > 0)
                {
                    var result = new List<DeviceTokenDescriptor>(cached.Length);
                    for (int i = 0; i < cached.Length; i++)
                    {
                        byte[] raw = Convert.FromHexString(cached[i].Name.ToString());
                        string token = DecodeToken(raw);
                        if (!string.IsNullOrEmpty(token) && byte.TryParse(cached[i].Value.ToString(), out byte platformFamily))
                        {
                            result.Add(new DeviceTokenDescriptor(token, platformFamily));
                        }
                    }
                    return result;
                }
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var rows = await db.PlayerDeviceRegistrations
                .AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .ToListAsync();

            var descriptors = new List<DeviceTokenDescriptor>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                string token = DecodeToken(rows[i].DeviceTokenRaw);
                if (!string.IsNullOrEmpty(token))
                {
                    descriptors.Add(new DeviceTokenDescriptor(token, rows[i].PlatformFamily));
                }
            }

            if (_redis.IsConnected && rows.Count > 0)
            {
                HashEntry[] cacheEntries = new HashEntry[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    cacheEntries[i] = new HashEntry(Convert.ToHexString(rows[i].DeviceTokenRaw), (int)rows[i].PlatformFamily);
                }
                await _redis.GetDatabase().HashSetAsync(PushTokenCacheKey(playerId), cacheEntries);
            }

            return descriptors;
        }

        private async Task DrainOutboundQueueAsync()
        {
            int drained = 0;
            while (drained < MaxTriggersPerPoll && _outboundQueue.TryDequeue(out var request))
            {
                drained++;
                await SendFcmV1Async(request);
            }
        }

        private async Task SendFcmV1Async(OutboundPushRequest request)
        {
            string? accessToken = await GetAccessTokenAsync();
            string projectId = Environment.GetEnvironmentVariable("FCM_PROJECT_ID") ?? string.Empty;
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(projectId))
            {
                return;
            }

            string endpoint = $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send";
            var body = new
            {
                message = new
                {
                    token = request.DeviceToken,
                    data = new Dictionary<string, string>
                    {
                        ["trigger_type"] = request.TriggerType.ToString(),
                        ["payload"] = request.PayloadCode,
                        ["player_id"] = request.PlayerId.ToString()
                    }
                }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(httpRequest);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"FCM send failed for player {request.PlayerId}: {(int)response.StatusCode}");
            }
        }

        private async Task<string?> GetAccessTokenAsync()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!string.IsNullOrEmpty(_cachedAccessToken) && _cachedAccessTokenExpiresAt - now > 60)
            {
                return _cachedAccessToken;
            }

            await _oauthLock.WaitAsync();
            try
            {
                now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!string.IsNullOrEmpty(_cachedAccessToken) && _cachedAccessTokenExpiresAt - now > 60)
                {
                    return _cachedAccessToken;
                }

                string clientEmail = Environment.GetEnvironmentVariable("FCM_CLIENT_EMAIL") ?? string.Empty;
                string privateKey = Environment.GetEnvironmentVariable("FCM_PRIVATE_KEY")?.Replace("\\n", "\n") ?? string.Empty;
                if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey))
                {
                    return null;
                }

                string assertion = CreateJwtAssertion(clientEmail, privateKey, now);
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                });

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                    Content = form
                };

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync();
                using JsonDocument document = JsonDocument.Parse(json);
                _cachedAccessToken = document.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
                int expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
                _cachedAccessTokenExpiresAt = now + expiresIn;
                return _cachedAccessToken;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FCM OAuth helper failed: {ex.Message}");
                return null;
            }
            finally
            {
                _oauthLock.Release();
            }
        }

        private static string CreateJwtAssertion(string clientEmail, string privateKey, long now)
        {
            string header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iss"] = clientEmail,
                ["scope"] = "https://www.googleapis.com/auth/firebase.messaging",
                ["aud"] = "https://oauth2.googleapis.com/token",
                ["iat"] = now,
                ["exp"] = now + 3600
            })));

            string signingInput = $"{header}.{payload}";
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{signingInput}.{Base64UrlEncode(signature)}";
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool TryParseTrigger(string payload, out long playerId, out byte triggerType, out string payloadCode)
        {
            playerId = 0;
            triggerType = 0;
            payloadCode = string.Empty;

            int first = payload.IndexOf('|');
            if (first <= 0) return false;
            int second = payload.IndexOf('|', first + 1);
            if (second <= first) return false;

            if (!long.TryParse(payload.AsSpan(0, first), out playerId)) return false;
            if (!byte.TryParse(payload.AsSpan(first + 1, second - first - 1), out triggerType)) return false;
            payloadCode = payload[(second + 1)..];
            return playerId > 0;
        }

        private static string DecodeToken(byte[] raw)
        {
            int length = 0;
            while (length < raw.Length && raw[length] != 0)
            {
                length++;
            }

            return length == 0 ? string.Empty : Encoding.UTF8.GetString(raw, 0, length);
        }

        private readonly record struct DeviceTokenDescriptor(string Token, byte PlatformFamily);

        private sealed class OutboundPushRequest
        {
            public long PlayerId;
            public string DeviceToken = string.Empty;
            public byte PlatformFamily;
            public byte TriggerType;
            public string PayloadCode = string.Empty;
        }
    }
}
