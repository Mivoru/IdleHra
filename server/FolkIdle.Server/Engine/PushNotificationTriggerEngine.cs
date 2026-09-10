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

            // Modul: SAID ONCE, LOUDLY, AT START-UP - because the alternative
            // is a feature that fails silently forever.
            //
            // SendFcmV1Async returns without sending when FCM_PROJECT_ID or the
            // service-account credentials are missing. That is the correct
            // behaviour and it is invisible: triggers are scheduled, the queue
            // drains, nothing is logged, and no phone rings. A deployment can
            // run for months believing push works. This is the one line that
            // makes "there is no Firebase project" a fact somebody can read.
            ReportSendConfiguration();

            _ = Task.Run(RunAsync);
        }

        /// <summary>Whether this deployment can actually send anything.</summary>
        public static bool IsSendConfigured()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FCM_PROJECT_ID"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FCM_CLIENT_EMAIL"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FCM_PRIVATE_KEY"));
        }

        private static void ReportSendConfiguration()
        {
            if (IsSendConfigured())
            {
                Console.WriteLine("Push: FCM configured; notifications will be delivered.");
                return;
            }

            // Named individually: "something is missing" sends somebody looking
            // through three variables one at a time.
            var missing = new List<string>();
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FCM_PROJECT_ID"))) missing.Add("FCM_PROJECT_ID");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FCM_CLIENT_EMAIL"))) missing.Add("FCM_CLIENT_EMAIL");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FCM_PRIVATE_KEY"))) missing.Add("FCM_PRIVATE_KEY");

            Console.WriteLine(
                $"Push: NOT CONFIGURED - missing {string.Join(", ", missing)}. " +
                "Device tokens will be stored and triggers scheduled, but NOTHING WILL BE SENT. " +
                "See TASK_BOARD C5.");
        }

        public void QueueDeviceRegistration(long playerId, byte[] deviceTokenRaw, byte platformFamily)
        {
            _ = Task.Run(async () => await RegisterDeviceAsync(playerId, deviceTokenRaw, platformFamily));
        }

        /// <summary>
        /// The shape the stores actually hand out: a STRING.
        ///
        /// Modul: 64 BYTES WAS NEVER GOING TO BE ENOUGH, AND NOTHING HAD EVER
        /// TESTED IT.
        ///
        /// The wire field behind opcode 33 is `fixed byte DeviceTokenBytes[64]`
        /// and RegisterDeviceAsync refused anything that was not exactly 64
        /// long. Capacitor's Token.value is an APNS token on iOS and an FCM
        /// token on Android - and an FCM registration token is roughly 160
        /// ASCII characters, so Android push could never have worked through
        /// that path. An APNS token as hex is 64 characters, which fits the
        /// field exactly and leaves no room for anything else.
        ///
        /// No client has ever called opcode 33, so this was never observed. It
        /// is the same problem the purchase receipt hit - see billing.ts, which
        /// says outright that the receipt goes over REST "because it is far too
        /// large for the fixed-layout command packet" - and it takes the same
        /// answer, for the same reason.
        ///
        /// The token is stored as its UTF-8 bytes rather than decoded, because
        /// the string is what FCM and APNS are addressed WITH. Decoding hex
        /// here would mean re-encoding it at every send.
        /// </summary>
        /// <remarks>
        /// AWAITABLE, AND IT ANSWERS. The byte[] overload above is
        /// fire-and-forget because opcode 33 has no reply channel; this one is
        /// reached from a REST route that does, and a route that answered 200
        /// to a token it silently dropped would be this server's favourite
        /// lie - the Settings screen would say "this device is registered"
        /// about a device that will never be sent anything.
        /// </remarks>
        public Task<bool> RegisterDeviceTokenAsync(long playerId, string deviceToken, byte platformFamily)
        {
            if (string.IsNullOrWhiteSpace(deviceToken)) return Task.FromResult(false);
            return RegisterDeviceAsync(playerId, System.Text.Encoding.UTF8.GetBytes(deviceToken.Trim()), platformFamily);
        }

        public void QueueDeviceRegistration(long playerId, string deviceToken, byte platformFamily)
        {
            if (string.IsNullOrWhiteSpace(deviceToken)) return;
            QueueDeviceRegistration(playerId, System.Text.Encoding.UTF8.GetBytes(deviceToken.Trim()), platformFamily);
        }

        /// <summary>The bounds a device token has to fall inside to be stored at all.</summary>
        public const int MinDeviceTokenBytes = 16;

        /// <summary>
        /// Generous on purpose: an FCM token is around 160 bytes today and
        /// Google has lengthened it before without warning. The point of the
        /// ceiling is to refuse a body that is obviously not a token, not to
        /// predict a format.
        /// </summary>
        public const int MaxDeviceTokenBytes = 512;

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

        private async Task<bool> RegisterDeviceAsync(long playerId, byte[] deviceTokenRaw, byte platformFamily)
        {
            // Modul: a RANGE, not an equality. This read `!= 64`, which is the
            // width of the fixed wire field rather than the width of a device
            // token - see the string overload above for why that made Android
            // push impossible and iOS push exactly one byte from impossible.
            if (playerId <= 0
                || deviceTokenRaw == null
                || deviceTokenRaw.Length < MinDeviceTokenBytes
                || deviceTokenRaw.Length > MaxDeviceTokenBytes
                || platformFamily == 0
                || platformFamily > 2)
            {
                return false;
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

                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Push device registration failed for player {playerId}: {ex.Message}");
                return false;
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

        internal readonly record struct TriggerCopy(string Title, string Body, string Screen);

        /// <summary>
        /// What a trigger says on a lock screen, and where tapping it goes.
        ///
        /// Modul: ONE TABLE, ON THE SERVER. The alternative is a switch in the
        /// client keyed on the same payload codes - the ordered-list-across-
        /// the-wire trap that KNOWN_AFFIX_IDS fell into, where ten of twelve
        /// entries had quietly drifted. The screen key is the client's own
        /// (`App.svelte`'s nav keys); an unknown code lands on the map rather
        /// than nowhere, because a notification that opens a blank screen is
        /// worse than one that opens the wrong one.
        /// </summary>
        internal static TriggerCopy DescribeTrigger(string payloadCode) => payloadCode switch
        {
            "world_boss_window_open" => new TriggerCopy(
                "A world boss has surfaced",
                "The event window is open. Your attempts reset with it.",
                "worldboss"),
            "daily_quest_reset" => new TriggerCopy(
                "New daily quests",
                "Today's quests and your daily login reward are waiting.",
                "progression"),
            _ => new TriggerCopy(
                "FolkIdle",
                "Something is waiting for you.",
                "hub")
        };

        private async Task SendFcmV1Async(OutboundPushRequest request)
        {
            string? accessToken = await GetAccessTokenAsync();
            string projectId = Environment.GetEnvironmentVariable("FCM_PROJECT_ID") ?? string.Empty;
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(projectId))
            {
                return;
            }

            string endpoint = $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = new StringContent(BuildFcmMessageJson(request), Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(httpRequest);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"FCM send failed for player {request.PlayerId}: {(int)response.StatusCode}");
            }
        }

        /// <summary>
        /// The exact JSON one trigger becomes.
        ///
        /// Modul: SPLIT OUT SO IT CAN BE ASSERTED ON. What this used to send was
        /// `data` and nothing else, and a data-only FCM message is delivered to
        /// a RUNNING app and dropped on the floor of a backgrounded one - so the
        /// one moment the feature exists for, a player who is not looking at the
        /// game, arrived as silence. That defect is invisible from inside the
        /// server: the send succeeds, FCM answers 200, and nothing anywhere is
        /// wrong except that no phone rang. A pure function is the only way to
        /// put a test between the trigger and the wire.
        /// </summary>
        internal static string BuildFcmMessageJson(OutboundPushRequest request)
        {
            var copy = DescribeTrigger(request.PayloadCode);
            var body = new
            {
                message = new
                {
                    token = request.DeviceToken,

                    // Modul: A DATA-ONLY MESSAGE DISPLAYS NOTHING AND CANNOT BE
                    // TAPPED. This block was `data` alone, which FCM delivers to
                    // a RUNNING app and drops on the floor of a backgrounded
                    // one - so the single moment this feature exists for, the
                    // player who is not looking at the game, saw nothing at all.
                    // The whole argument for having an app rather than a
                    // bookmark was arriving as silence.
                    notification = new { title = copy.Title, body = copy.Body },

                    // The data rides ALONGSIDE it, and carries the destination.
                    // The client does not map a payload code to a screen -
                    // that would be this ordering written down twice, in two
                    // languages, and that is this codebase's dominant bug
                    // class. The server says where the tap goes.
                    data = new Dictionary<string, string>
                    {
                        ["trigger_type"] = request.TriggerType.ToString(),
                        ["payload"] = request.PayloadCode,
                        ["player_id"] = request.PlayerId.ToString(),
                        ["screen"] = copy.Screen
                    },

                    // Modul: "high" so a doze-mode phone is woken. An idle
                    // game's notification is worth nothing an hour late - the
                    // boss window it announces may have closed.
                    android = new { priority = "high" },

                    // The APNS half is declared for completeness and does not
                    // work yet: FCM v1 addresses iOS through an FCM
                    // registration token issued by the Firebase iOS SDK, and
                    // the client currently hands over the RAW APNS token
                    // Capacitor returns. See MOBILE.md - that is a C-phase
                    // job, alongside the Firebase project itself.
                    apns = new
                    {
                        headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
                        payload = new { aps = new { sound = "default" } }
                    }
                }
            };

            return JsonSerializer.Serialize(body);
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

        internal sealed class OutboundPushRequest
        {
            public long PlayerId;
            public string DeviceToken = string.Empty;
            public byte PlatformFamily;
            public byte TriggerType;
            public string PayloadCode = string.Empty;
        }
    }
}
