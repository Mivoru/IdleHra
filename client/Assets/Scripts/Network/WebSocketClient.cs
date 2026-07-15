using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FolkIdle.Client.Network
{
    public class WebSocketClient : MonoBehaviour
    {
        // JWT issued by POST /api/v1/auth/login (see UiLoginWindow) - sent as
        // the WS AuthHandshakePacket's payload and as the Authorization: Bearer
        // header on authenticated HTTP endpoints (e.g. EquipmentInventoryCache).
        // Stays empty until UiLoginWindow completes a successful login; Connect()
        // refuses to open a socket while empty.
        public static string AuthenticatorToken = string.Empty;

        // Fires exactly once per connection, the first time a StateUpdatePacket
        // arrives after a successful handshake - this is the "state confirmation"
        // UiLoginWindow waits on before hiding its blocking panel, since it is
        // the first proof the server accepted the JWT and resolved a playerId.
        public event Action OnStateConfirmed;
        public bool IsAuthenticated { get; private set; }

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private bool _isConnecting;

        // Single reusable buffer to prevent GC allocations in unity Update
        private byte[] _receiveBuffer = new byte[1024];

        // Reused across connection attempts/retries so the handshake send never
        // allocates on the hot path.
        private readonly byte[] _authBuffer = new byte[Marshal.SizeOf<AuthHandshakePacket>()];

        // Modul: backpressure caps. An idle game is defined by long
        // backgrounded/AFK sessions - the receive loop keeps enqueuing at
        // the server's 10Hz cadence regardless of whether VisualSyncProxy's
        // Update() is even running (a disabled/backgrounded scene still
        // owns the socket), so an uncapped queue grows without bound over a
        // multi-hour background period. Drop-oldest keeps memory bounded
        // and is the semantically correct choice for both queues: a
        // StateUpdatePacket fully supersedes every earlier one for the same
        // player (there is nothing to lose by dropping a stale snapshot),
        // and a chat backlog beyond this depth is already far past what
        // ChatEngine's own server-side rate limiting and UiChatWindow's
        // HistoryCapacity circular buffer are designed around. Combined
        // with VisualSyncProxy's per-frame dequeue budget, the worst case
        // after a long background period is a small, bounded catch-up
        // instead of an unbounded one.
        private const int MaxQueuedStatePackets = 8;
        private const int MaxQueuedChatMessages = 128;

        // Thread-safe queue for main-thread consumption
        public ConcurrentQueue<StateUpdatePacket> PacketQueue { get; } = new ConcurrentQueue<StateUpdatePacket>();

        // Chat messages are a dedicated, exact-size binary WS message
        // (see ResponseChatMessagePacket) distinguished from StateUpdatePacket
        // by byte count in the receive loop below, not by riding on the
        // recurring per-tick state channel - routed into their own queue so
        // UiChatWindow never has to filter them out of PacketQueue.
        public ConcurrentQueue<ResponseChatMessagePacket> ChatMessageQueue { get; } = new ConcurrentQueue<ResponseChatMessagePacket>();

        // Modul: enqueues then trims from the front until back under the
        // cap - safe under the single-producer (this receive loop is the
        // only enqueuer) / single-consumer shape both queues actually have,
        // and ConcurrentQueue<T>.TryDequeue is itself thread-safe against
        // whichever thread is consuming, so no external lock is needed.
        private static void EnqueueWithCap<T>(ConcurrentQueue<T> queue, T item, int maxDepth)
        {
            queue.Enqueue(item);
            while (queue.Count > maxDepth && queue.TryDequeue(out _))
            {
            }
        }

        private readonly byte[] _chatSendBuffer = new byte[Marshal.SizeOf<RequestChatMessagePacket>()];

        private long _lastPlayerId;
        private long _lastLogicEpochCounter;
        private uint _lastChallengeSeed;

        public void Start()
        {
            NetworkPacketLayoutGuard.Validate();
            FlightRecorder.Initialize();
            ClientContentRegistry.Initialize();
        }

        // Called by UiLoginWindow only after a successful /api/v1/auth/login
        // response has populated AuthenticatorToken - the socket is never
        // opened before a JWT exists, so there is no window where a gameplay
        // command could race the handshake.
        public void Connect()
        {
            if (_isConnecting) return;
            _isConnecting = true;
            IsAuthenticated = false;
            _webSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            _ = ConnectAndReceiveLoopAsync();
        }

        private async Task ConnectAndReceiveLoopAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(AuthenticatorToken))
                {
                    Debug.LogError("WebSocketClient: Connect() called with an empty AuthenticatorToken; aborting before opening a socket.");
                    return;
                }

                await _webSocket.ConnectAsync(new Uri("ws://localhost:8080/"), _cts.Token);
                FlightRecorder.RecordNetworkState(1);
                Debug.Log("WebSocket Connected.");

                await SendAuthHandshakeAsync();

                var segment = new ArraySegment<byte>(_receiveBuffer);

                while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(segment, _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FlightRecorder.RecordNetworkState(2);
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        break;
                    }

                    if (result.Count == Marshal.SizeOf<ResponseChatMessagePacket>())
                    {
                        ParseAndEnqueueChatMessage(result.Count);
                    }
                    else
                    {
                        ParseAndEnqueuePacket(result.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                FlightRecorder.RecordNetworkState(3);
                Debug.LogError($"WebSocket Error: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
            }
        }

        // Must be the very first message on the socket - NetworkBroadcastSystem
        // gives the connection 5 seconds to send a binary AuthHandshakePacket
        // before dropping it, and rejects any gameplay CommandType packet sent
        // before this succeeds. AssetHash/PlatformSignature are sent as 0 (the
        // server only enforces them when ExpectedCatalogHashLong/
        // ExpectedPlatformSignatureLong are configured).
        private async Task SendAuthHandshakeAsync()
        {
            byte[] jwtBytes = System.Text.Encoding.UTF8.GetBytes(AuthenticatorToken);
            if (jwtBytes.Length > AuthHandshakePacket.JwtTokenCapacity)
            {
                Debug.LogError($"WebSocketClient: JWT length {jwtBytes.Length} exceeds AuthHandshakePacket capacity {AuthHandshakePacket.JwtTokenCapacity}; aborting handshake.");
                await _webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Token too large", CancellationToken.None);
                return;
            }

            AuthHandshakePacket authPacket = new AuthHandshakePacket
            {
                JwtTokenLength = (ushort)jwtBytes.Length,
                AssetHash = 0,
                PlatformSignature = 0
            };

            unsafe
            {
                byte* target = authPacket.JwtToken;
                for (int i = 0; i < AuthHandshakePacket.JwtTokenCapacity; i++)
                {
                    target[i] = i < jwtBytes.Length ? jwtBytes[i] : (byte)0;
                }
            }

            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref _authBuffer[0], authPacket);
            var segment = new ArraySegment<byte>(_authBuffer);
            await _webSocket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private void ParseAndEnqueuePacket(int length)
        {
            if (!UnsafePacketParser.TryParseState(_receiveBuffer, length, out StateUpdatePacket packet))
            {
                Debug.LogWarning($"WebSocketClient: rejected malformed inbound packet (length {length}).");
                return;
            }

            if (!IsAuthenticated)
            {
                IsAuthenticated = true;
                OnStateConfirmed?.Invoke();
            }

            _lastPlayerId = packet.PlayerId;
            _lastLogicEpochCounter = packet.LogicEpochCounter;
            FlightRecorder.RecordInbound(0, length, packet.LogicEpochCounter);
            if (packet.ActiveChallengeSeed != 0 && packet.ActiveChallengeSeed != _lastChallengeSeed)
            {
                _lastChallengeSeed = packet.ActiveChallengeSeed;
                uint verificationHash = ComputeChallengeHash(packet.ActiveChallengeSeed, packet.PlayerId, packet.LogicEpochCounter);
                SendAntiCheatChallengeResponseZeroAlloc(packet.ActiveChallengeSeed, verificationHash);
            }
            EnqueueWithCap(PacketQueue, packet, MaxQueuedStatePackets);
        }

        private void ParseAndEnqueueChatMessage(int length)
        {
            if (!UnsafePacketParser.TryParseChatMessage(_receiveBuffer, length, out ResponseChatMessagePacket packet))
            {
                Debug.LogWarning($"WebSocketClient: rejected malformed inbound chat packet (length {length}).");
                return;
            }

            EnqueueWithCap(ChatMessageQueue, packet, MaxQueuedChatMessages);
        }

        // Zero-allocation-on-the-hot-path chat send: the packet struct and
        // its UTF8 encode are stack/short-lived allocations from a discrete
        // user action (pressing Enter in the chat box), not the 10Hz tick -
        // _chatSendBuffer itself is the one reused buffer that matters, same
        // as _outboundBuffer/_authBuffer below.
        public unsafe void SendChatMessageZeroAlloc(string messageText)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open || string.IsNullOrEmpty(messageText)) return;

            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(messageText);
            int length = textBytes.Length > RequestChatMessagePacket.MessageCapacity ? RequestChatMessagePacket.MessageCapacity : textBytes.Length;

            RequestChatMessagePacket packet = new RequestChatMessagePacket
            {
                MessageLength = (ushort)length
            };

            byte* target = packet.MessageText;
            for (int i = 0; i < RequestChatMessagePacket.MessageCapacity; i++)
            {
                target[i] = i < length ? textBytes[i] : (byte)0;
            }

            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref _chatSendBuffer[0], packet);
            var segment = new ArraySegment<byte>(_chatSendBuffer);
            _ = _webSocket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private readonly byte[] _outboundBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];

        private void SendPacket(ref ClientCommandPacket packet, bool useChronoTimestamp = false)
        {
            packet.LogicEpochCounter = useChronoTimestamp ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : _lastLogicEpochCounter;
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref _outboundBuffer[0], packet);
            FlightRecorder.RecordOutbound((byte)packet.Command, _outboundBuffer.Length, packet.LogicEpochCounter);

            var segment = new ArraySegment<byte>(_outboundBuffer);
            _ = _webSocket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public void SendCommandZeroAlloc(byte commandType, int argumentValue)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandType,
                    TargetId = argumentValue,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };
                
                SendPacket(ref packet);
            }
        }
        public void SendPingCommandZeroAlloc(uint token)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)52,
                    NetworkDiagnosticsToken = token
                };
                
                SendPacket(ref packet);
            }
        }

        public void SendFusionCommandZeroAlloc(long targetId, long sacId1, long sacId2)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)2,
                    TargetId = targetId,
                    SecondaryId = sacId1,
                    TertiaryId = sacId2,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendAchievementClaimCommandZeroAlloc(uint achievementId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)43,
                    TargetAchievementId = achievementId
                };

                SendPacket(ref packet);
            }
        }

        public void SendBattlePassClaimCommandZeroAlloc(uint milestoneIndex)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ClaimBattlePassReward,
                    TargetMilestoneIndex = milestoneIndex
                };

                SendPacket(ref packet);
            }
        }

        public void SendMarketCommandZeroAlloc(byte commandType, long instanceId, int price)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandType,
                    TargetId = instanceId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = price,
                    IsBuy = commandType == 10 ? (byte)1 : (byte)0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendMailCommandZeroAlloc(byte commandType, long instanceId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandType,
                    TargetId = instanceId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendWarSupplyCommandZeroAlloc(long targetPlayerId, long commodityId, long quantityToBurn)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ContributeToWarSupply,
                    TargetId = targetPlayerId,
                    SecondaryId = commodityId,
                    TertiaryId = quantityToBurn,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendBreedingCommandZeroAlloc(byte commandType, System.Guid targetGuid, System.Guid secondaryGuid)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandType,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetGuid = targetGuid,
                    SecondaryGuid = secondaryGuid
                };

                SendPacket(ref packet);
            }
        }

        public void SendCraftingCommandZeroAlloc(byte commandType, long resultItemId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandType,
                    TargetId = resultItemId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }
        public void SendEquipmentCraftingCommandZeroAlloc(uint targetRecipeId, uint craftingSlotIndex)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.CraftItem,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetRecipeId = targetRecipeId,
                    CraftingSlotIndex = craftingSlotIndex
                };

                SendPacket(ref packet);
            }
        }
        public void SendRerollCommandZeroAlloc(long instanceId, int affixIndex)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)14,
                    TargetId = instanceId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = affixIndex,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendGuildContributionCommandZeroAlloc(int itemDefinitionId, int quantity)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)5,
                    TargetId = itemDefinitionId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = quantity,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendWorldBossDamageCommandZeroAlloc(long damage)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)19,
                    TargetId = damage,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendWorldBossAttackCommandZeroAlloc(uint targetedBossId, uint clientPredictedDamage)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.AttackWorldBoss,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetedBossId = targetedBossId,
                    ClientPredictedDamage = clientPredictedDamage
                };

                SendPacket(ref packet);
            }
        }

        public unsafe void SendPushTokenCommandZeroAlloc(byte[] deviceTokenBytes, byte platformFamily)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.RegisterPushToken,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetPlatformFamily = platformFamily
                };

                byte* target = packet.DeviceTokenBytes;
                for (int i = 0; i < 64; i++)
                {
                    target[i] = i < deviceTokenBytes.Length ? deviceTokenBytes[i] : (byte)0;
                }

                SendPacket(ref packet);
            }
        }

        public void SendGdprPurgeCommandZeroAlloc()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.TriggerGdprPurge,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    ConfirmationHash = ComputeGdprConfirmationHash(_lastPlayerId, _lastLogicEpochCounter)
                };

                SendPacket(ref packet);
            }
        }

        public void SendConsumableCommandZeroAlloc(byte commandId, uint consumableItemId, uint slotTarget)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandId,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    ConsumableItemId = consumableItemId,
                    ConsumableSlotTarget = slotTarget
                };

                SendPacket(ref packet);
            }
        }

        public void SendLanguageSwitchCommandZeroAlloc(byte languageId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.SwitchLanguage,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetLanguageId = languageId
                };

                SendPacket(ref packet);
            }
        }

        public void SendUpgradeCommandZeroAlloc(byte commandType, int targetId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)commandType,
                    TargetId = targetId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendCombatConfigZeroAlloc(int thresholdValue)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)16,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = thresholdValue,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendMigrationCommandZeroAlloc(uint migrationToken)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.InitiateNodeMigration,
                    MigrationToken = migrationToken
                };

                SendPacket(ref packet);
            }
        }

        public void SendMentorshipCommandZeroAlloc(System.Guid characterId, int slotIndex)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = (CommandType)22,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = slotIndex,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetGuid = characterId
                };

                SendPacket(ref packet);
            }
        }

        public void SendChronoCoreCommandZeroAlloc(long chronoCoreItemId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ConsumeChronoCore,
                    TargetId = chronoCoreItemId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendLegacyUnlockCommandZeroAlloc(uint targetUnlockId, uint requestedSlotIndex)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.PurchaseLegacyUnlocks,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetUnlockId = targetUnlockId,
                    RequestedSlotIndex = requestedSlotIndex
                };

                SendPacket(ref packet);
            }
        }

        public void SendGuildMaterialDepositCommandZeroAlloc(uint materialId, uint depositQuantity)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.DepositGuildMaterial,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    MaterialId = materialId,
                    DepositQuantity = depositQuantity
                };

                SendPacket(ref packet);
            }
        }

        public void SendLaunchGuildRaidCommandZeroAlloc()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.LaunchGuildRaid,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendEquipItemCommandZeroAlloc(long itemInstanceId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.EquipItem,
                    TargetId = itemInstanceId,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        // isArmorSlot: false clears the weapon slot, true clears the armor slot.
        public void SendUnequipItemCommandZeroAlloc(bool isArmorSlot)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.UnequipItem,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = isArmorSlot ? (byte)1 : (byte)0,
                    QualityTier = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendCombatTurnCommandZeroAlloc(uint matchId, uint predictedTurnCounter)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ExecuteCombatTurn,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    MatchId = matchId,
                    ClientPredictedTurnCounter = predictedTurnCounter
                };

                SendPacket(ref packet);
            }
        }

        public void SendEstablishMentorshipCommandZeroAlloc(uint targetPlayerId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.EstablishMentorship,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetPlayerId = targetPlayerId,
                    MentorshipRole = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendTerminateMentorshipCommandZeroAlloc(uint counterpartyPlayerId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.TerminateMentorship,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetPlayerId = counterpartyPlayerId,
                    MentorshipRole = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendVillageUpgradeCommandZeroAlloc(uint targetBuildingId)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.UpgradeBuilding,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetBuildingId = targetBuildingId,
                    TargetVillagerSlot = 0
                };

                SendPacket(ref packet);
            }
        }

        public void SendVillagerEvictionCommandZeroAlloc(uint targetVillagerSlot)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.EvictVillager,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetBuildingId = 0,
                    TargetVillagerSlot = targetVillagerSlot
                };

                SendPacket(ref packet);
            }
        }

        public void SendRegisterGuildDefenseCommandZeroAlloc()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.RegisterGuildDefense,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetMatchUuid = Guid.Empty
                };

                SendPacket(ref packet);
            }
        }

        public void SendSubmitShardAttackCommandZeroAlloc(Guid matchUuid, uint damage, bool isFinalBlow)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.SubmitShardAttack,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = isFinalBlow ? (byte)1 : (byte)0,
                    QualityTier = 0,
                    TargetMatchUuid = matchUuid,
                    ClientPredictedDamage = damage
                };

                SendPacket(ref packet);
            }
        }

        public unsafe void SendTelemetryBurstCommandZeroAlloc(uint eventCount, uint eventTypeHash, uint payloadMetric)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ReportTelemetryBurst,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    TelemetryEventCount = eventCount,
                    TelemetryBurstPadding = eventTypeHash
                };

                long packedMetric = ((long)eventTypeHash << 32) | (payloadMetric & 0xFFFFFFFFL);
                byte* target = packet.RawTransactionReceipt;
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(target, packedMetric);

                SendPacket(ref packet);
            }
        }

        private void SendAntiCheatChallengeResponseZeroAlloc(uint challengeId, uint verificationHash)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.AntiCheatChallengeResponse,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    ChallengeId = challengeId,
                    ChallengeVerificationHash = verificationHash
                };

                SendPacket(ref packet);
            }
        }

        private static uint ComputeChallengeHash(uint challengeSeed, long playerId, long logicEpochCounter)
        {
            uint value = challengeSeed;
            value ^= unchecked((uint)playerId);
            value = XorShift32(value);
            value ^= unchecked((uint)(playerId >> 32));
            value = XorShift32(value + unchecked((uint)logicEpochCounter));
            value ^= 0xC2B2AE35u;
            return XorShift32(value);
        }

        private static uint ComputeGdprConfirmationHash(long playerId, long logicEpochCounter)
        {
            uint value = 0x47D99513u;
            value ^= unchecked((uint)playerId);
            value = XorShift32(value);
            value ^= unchecked((uint)(playerId >> 32));
            value = XorShift32(value + unchecked((uint)logicEpochCounter * 0x9E3779B9u));
            value ^= 0xA5C3F19Bu;
            return XorShift32(value);
        }

        private static uint XorShift32(uint value)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value == 0u ? 0x6D2B79F5u : value;
        }

        public void SendActivateChronoBoostCommandZeroAlloc(double multiplier)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ActivateChronoBoost,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    RequestedSpeedMultiplier = multiplier
                };

                SendPacket(ref packet, true);
            }
        }

        public void SendConsumeTimeWarpCoreCommandZeroAlloc(uint chronoSecondsRequested, uint chronoTargetSlot = 0)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.ConsumeTimeWarpCore,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = 0,
                    IsBuy = 0,
                    QualityTier = 0,
                    ChronoSecondsRequested = chronoSecondsRequested,
                    ChronoWarpDurationSeconds = chronoSecondsRequested,
                    ChronoTargetSlot = chronoTargetSlot
                };

                SendPacket(ref packet, true);
            }
        }

        public unsafe void SendPurchaseReceiptCommandZeroAlloc(byte[] receiptBytes, uint productIdHash, int premiumAmount)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                ClientCommandPacket packet = new ClientCommandPacket
                {
                    Command = CommandType.SubmitPurchaseReceipt,
                    TargetId = 0,
                    SecondaryId = 0,
                    TertiaryId = 0,
                    LimitPrice = premiumAmount,
                    IsBuy = 0,
                    QualityTier = 0,
                    TargetProductIdHash = productIdHash
                };

                byte* target = packet.RawTransactionReceipt;
                fixed (byte* source = receiptBytes)
                {
                    // Blueprint specifies using Unsafe for copying
                    System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(target, source, 64);
                }

                SendPacket(ref packet);
            }
        }

        public void OnDestroy()
        {
            _cts?.Cancel();
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait();
            }
            _webSocket?.Dispose();
            _cts?.Dispose();
            FlightRecorder.Shutdown();
        }
    }
}
