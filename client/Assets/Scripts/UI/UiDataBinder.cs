using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;
using System.Runtime.CompilerServices;
using System;

namespace FolkIdle.Client.UI
{
    public class UiDataBinder : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Player")]
        public Slider PlayerHpSlider;
        public TMP_Text PlayerHpText;

        [Header("Monster")]
        public Slider MonsterHpSlider;
        public TMP_Text MonsterHpText;

        [Header("Progression")]
        public Slider ProgressSlider;
        public TMP_Text ProgressText;

        public float MaxPlayerHp = 100000f;
        public float MaxMonsterHp = 1000f;
        public float MaxProgressTicks = 100f;

        [Header("Gathering - Woodcutting")]
        public Slider WoodcuttingXpSlider;
        public TMP_Text WoodcuttingXpText;
        public float MaxWoodcuttingXp = 1000f;

        [Header("Gathering - Mining")]
        public Slider MiningXpSlider;
        public TMP_Text MiningXpText;
        public float MaxMiningXp = 1000f;

        [Header("Gathering Progress")]
        public Slider GatheringProgressSlider;
        public TMP_Text GatheringProgressText;
        public float MaxGatheringTicks = 100f;

        [Header("Market")]
        public TMP_Text MarketItemPriceText;
        public float CurrentMarketItemPrice = 0f;

        [Header("Mailbox & Bank")]
        public TMP_Text BankSlotsText;
        public TMP_Text MailboxAttachmentText;
        public float CurrentBankSlots = 0f;
        public float CurrentMailAttachments = 0f;

        [Header("Chrono Bank")]
        public TMP_Text AccumulatedTimeBankText;
        private readonly char[] _timeBankBuffer = new char[9];

        [Header("Sprint 44 - Chrono Buffer")]
        public TMP_Text BankedChronoText;
        public TMP_Text ChronoMultiplierText;
        private readonly char[] _chronoGlobalUiBuffer = new char[128];
        private readonly char[] _chronoMultiplierBuffer = new char[16];

        [Header("Guild War")]
        public TMP_Text GuildWarStatusText;
        public TMP_Text GuildCombatPointsText;
        public TMP_Text GuildLogisticsPointsText;
        public TMP_Text GuildSupplyPointsText;
        public TMP_Text EnemyCombatPointsText;
        public TMP_Text EnemyLogisticsPointsText;
        public TMP_Text EnemySupplyPointsText;
        private readonly char[] _guildWarBuffer = new char[32];
        private readonly char[] _guildWarUiBuffer = new char[128];
        private readonly char[] _guildWarStatusBuffer = new char[48];
        private readonly char[] _guildCombatBuffer = new char[48];
        private readonly char[] _guildWarLogisticsBuffer = new char[48];
        private readonly char[] _guildSupplyBuffer = new char[48];
        private readonly char[] _enemyCombatBuffer = new char[48];
        private readonly char[] _enemyLogisticsBuffer = new char[48];
        private readonly char[] _enemySupplyBuffer = new char[48];

        [Header("Guild Logistics Depot")]
        public TMP_Text GuildLogisticsDepotText;
        private readonly char[] _guildLogisticsBuffer = new char[64];

        [Header("Guild Combat Simulation")]
        public TMP_Text CombatSimulationText;
        private readonly char[] _combatSimulationBuffer = new char[128];

        [Header("Reroll")]
        public TMP_Text RerollCostText;
        public float CurrentRerollCost = 0f;
        private readonly char[] _rerollBuffer = new char[32];

        [Header("Village")]
        public TMP_Text VillagePopulationText;
        public TMP_Text VillageInfrastructureText;
        private readonly char[] _populationBuffer = new char[32];
        private readonly char[] _villageBuffer = new char[64];

        [Header("Consumable UI Context")]
        public TMP_Text ActiveModifiersText;
        public TMP_Text BuffDurationText;
        private readonly char[] _productionUiBuffer = new char[64];

        [Header("Chronicle Pass")]
        public TMP_Text ChroniclePassText;
        private readonly char[] _seasonalUiBuffer = new char[128];

        [Header("Analytics")]
        public TMP_Text AnalyticsSummaryText;
        private readonly char[] _analyticsUiBuffer = new char[128];

        [Header("Migration Epoch")]
        public TMP_Text LogicalEpochFrameText;
        private readonly char[] _migrationUiBuffer = new char[64];

        [Header("Legacy Store")]
        public TMP_Text LegacyShardBalanceText;
        private readonly char[] _legacyBuffer = new char[32];

        [Header("Forge Affix State")]
        public TMP_Text ForgeAffixStateText;
        private readonly char[] _forgeBuffer = new char[32];

        [Header("Guild")]
        public TMP_Text MiningMonolithLevelText;
        public TMP_Text WoodcuttingMonolithLevelText;
        private readonly char[] _miningMonolithBuffer = new char[32];
        private readonly char[] _woodcuttingMonolithBuffer = new char[32];

        [Header("World Boss")]
        public Slider WorldBossHpSlider;
        public TMP_Text WorldBossHpText;
        private readonly char[] _worldBossHpBuffer = new char[32];

        [Header("LiveOps")]
        public TMP_Text GlobalEventText;
        private readonly char[] _liveOpsBuffer = new char[64];

        [Header("Push Notifications")]
        public TMP_Text NotificationQueueText;
        private readonly char[] _notificationBuffer = new char[32];

        [Header("Sprint 37 - Mentorship & Aging")]
        public TMP_Text ActiveAgePhaseText;
        public TMP_Text MentorCountText;
        private readonly char[] _agePhaseBuffer = new char[32];
        private readonly char[] _mentorBuffer = new char[32];

        [Header("Mentorship Contract")]
        public TMP_Text MentorshipContractText;
        private readonly char[] _mentorshipBuffer = new char[32];

        [Header("Sprint 38 - Codex & Achievements")]
        public TMP_Text CompletedAreaFlagsText;
        private readonly char[] _completedAreaBuffer = new char[48];

        [Header("Sprint 42 - OTA Handshake")]
        public TMP_Text OtaStatusText;
        private readonly char[] _otaBuffer = new char[32];
        public TMP_Text ClaimedAchievementFlagsText;
        public TMP_Text HumanMasteryLevelText;
        public TMP_Text VilaMasteryLevelText;
        public TMP_Text DraugrMasteryLevelText;
        private readonly char[] _claimedAchievementBuffer = new char[48];
        private readonly char[] _humanMasteryBuffer = new char[48];
        private readonly char[] _vilaMasteryBuffer = new char[48];
        private readonly char[] _draugrMasteryBuffer = new char[48];

        [Header("Sprint 59 - Leaderboard")]
        private readonly char[] _leaderboardUiBuffer = new char[128];

        [Header("Sprint 60 - Premium Store")]
        public TMP_Text PremiumCurrencyText;
        private readonly char[] _premiumUiBuffer = new char[64];

        [Header("Sprint 62 - Crafting State")]
        public TMP_Text CraftingStateText;
        private readonly char[] _craftingUiBuffer = new char[64];

        [Header("Sprint 63 - Achievements & Mastery")]
        public TMP_Text AchievementsCountText;
        public TMP_Text MasteryBitmaskText;

        [Header("Sprint 70 - Network Diagnostics")]
        public TMP_Text NetworkDiagnosticsText;
        private readonly char[] _networkUiBuffer = new char[128];
        private readonly char[] _achievementsUiBuffer = new char[64];
        private readonly char[] _masteryUiBuffer = new char[128];

        // Sprint 43: Hierarchical canvas layer roots.
        // StaticBackgroundCanvas: static labels, borders, layout roots — updated once.
        // PeriodicIntermittentCanvas: inventories, age texts, factory lines — event-driven.
        // HighFrequencySimulationCanvas: 10 Hz metrics — NO Unity Layout Groups.
        [Header("Sprint 43 - Canvas Isolation")]
        public UnityEngine.Canvas StaticBackgroundCanvas;
        public UnityEngine.Canvas PeriodicIntermittentCanvas;
        public UnityEngine.Canvas HighFrequencySimulationCanvas;

        // High-frequency canvas element RectTransforms — positioned via explicit matrix anchoring.
        public UnityEngine.RectTransform HfPlayerHpBarRect;
        public UnityEngine.RectTransform HfMonsterHpBarRect;
        public UnityEngine.RectTransform HfWorldBossBarRect;

        // Zero-allocation sync buffer for cluster epoch number rendering.
        private readonly char[] _syncBuffer = new char[32];

        [Header("Sprint 43 - Epoch Sync")]
        public TMP_Text EpochSyncText;

        private readonly char[] _playerHpBuffer = new char[32];
        private readonly char[] _monsterHpBuffer = new char[32];
        private readonly char[] _progressBuffer = new char[48];
        private readonly char[] _woodcuttingXpBuffer = new char[48];
        private readonly char[] _miningXpBuffer = new char[48];
        private readonly char[] _gatheringBuffer = new char[48];
        private readonly char[] _marketBuffer = new char[32];
        private readonly char[] _bankSlotsBuffer = new char[32];
        private readonly char[] _mailboxAttachmentBuffer = new char[48];

        private int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            return WriteLongToBuffer(buffer, offset, value);
        }

        private int WriteLongToBuffer(char[] buffer, int offset, long value)
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

            long temp = value;
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

        private int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private int WriteTwoDigits(char[] buffer, int offset, int value)
        {
            if (value < 0) value = 0;
            buffer[offset++] = (char)('0' + ((value / 10) % 10));
            buffer[offset++] = (char)('0' + (value % 10));
            return offset;
        }

        private void WriteMetric(TMP_Text target, char[] buffer, string label, int value)
        {
            int index = WriteTextToBuffer(buffer, 0, label);
            index = WriteIntToBuffer(buffer, index, value);
            target.SetCharArray(buffer, 0, index);
        }

        private void WriteMetricWithMax(TMP_Text target, char[] buffer, string label, int value, int maximum)
        {
            int index = WriteTextToBuffer(buffer, 0, label);
            index = WriteIntToBuffer(buffer, index, value);
            buffer[index++] = ' ';
            buffer[index++] = '/';
            buffer[index++] = ' ';
            index = WriteIntToBuffer(buffer, index, maximum);
            target.SetCharArray(buffer, 0, index);
        }

        private LocalizationKey ResolveEventLocalizationKey(int eventId)
        {
            switch (eventId)
            {
                case 1:
                    return LocalizationKey.EventGoldenHarvest;
                case 2:
                    return LocalizationKey.EventBloodMoon;
                case 3:
                    return LocalizationKey.EventMasterArtisan;
                case 4:
                    return LocalizationKey.EventDiamondStar;
                default:
                    return LocalizationKey.EventNone;
            }
        }

        private int WriteWarMultiplier(char[] buffer, int offset, float multiplier)
        {
            int scaled = (int)(multiplier * 100f);
            if (scaled < 0) scaled = 0;
            offset = WriteIntToBuffer(buffer, offset, scaled / 100);
            buffer[offset++] = '.';
            offset = WriteTwoDigits(buffer, offset, scaled % 100);
            return offset;
        }

        private void Awake()
        {
            LocalizationMatrix.Boot();
            // Assign explicit vector displacement anchors for all high-frequency elements.
            // No Layout Group components exist on HighFrequencySimulationCanvas prefab.
            // O(1) RectTransform matrix assignments replace automated layout CPU traversal.
            if (HfPlayerHpBarRect != null)
            {
                HfPlayerHpBarRect.anchorMin = new UnityEngine.Vector2(0f, 1f);
                HfPlayerHpBarRect.anchorMax = new UnityEngine.Vector2(1f, 1f);
                HfPlayerHpBarRect.pivot = new UnityEngine.Vector2(0.5f, 1f);
                HfPlayerHpBarRect.localPosition = new UnityEngine.Vector3(0f, -20f, 0f);
            }
            if (HfMonsterHpBarRect != null)
            {
                HfMonsterHpBarRect.anchorMin = new UnityEngine.Vector2(0f, 1f);
                HfMonsterHpBarRect.anchorMax = new UnityEngine.Vector2(1f, 1f);
                HfMonsterHpBarRect.pivot = new UnityEngine.Vector2(0.5f, 1f);
                HfMonsterHpBarRect.localPosition = new UnityEngine.Vector3(0f, -80f, 0f);
            }
            if (HfWorldBossBarRect != null)
            {
                HfWorldBossBarRect.anchorMin = new UnityEngine.Vector2(0f, 1f);
                HfWorldBossBarRect.anchorMax = new UnityEngine.Vector2(1f, 1f);
                HfWorldBossBarRect.pivot = new UnityEngine.Vector2(0.5f, 1f);
                HfWorldBossBarRect.localPosition = new UnityEngine.Vector3(0f, -140f, 0f);
            }
        }

        // Zero-allocation epoch counter projection for cluster synchronization display.
        // Writes 'Epoch: <N>' into the isolated _syncBuffer and binds via SetCharArray.
        public void UpdateEpochSync(long epochCounter)
        {
            if (EpochSyncText == null) return;

            _syncBuffer[0] = 'E';
            _syncBuffer[1] = 'p';
            _syncBuffer[2] = 'o';
            _syncBuffer[3] = 'c';
            _syncBuffer[4] = 'h';
            _syncBuffer[5] = ':';
            _syncBuffer[6] = ' ';

            int offset = 7;

            if (epochCounter == 0)
            {
                _syncBuffer[offset++] = '0';
            }
            else
            {
                long e = epochCounter;
                long divisor = 1000000000000000L;
                bool started = false;
                while (divisor > 0)
                {
                    int digit = (int)(e / divisor);
                    if (digit > 0 || started || divisor == 1)
                    {
                        _syncBuffer[offset++] = (char)('0' + digit);
                        started = true;
                    }
                    e %= divisor;
                    divisor /= 10;
                }
            }

            EpochSyncText.SetCharArray(_syncBuffer, 0, offset);
        }

        public void UpdateMigrationEpoch(ulong frameIndex)
        {
            if (LogicalEpochFrameText == null) return;
            _migrationUiBuffer[0] = 'F';
            _migrationUiBuffer[1] = 'r';
            _migrationUiBuffer[2] = 'a';
            _migrationUiBuffer[3] = 'm';
            _migrationUiBuffer[4] = 'e';
            _migrationUiBuffer[5] = ':';
            _migrationUiBuffer[6] = ' ';
            int offset = 7;
            if (frameIndex == 0)
            {
                _migrationUiBuffer[offset++] = '0';
            }
            else
            {
                ulong e = frameIndex;
                ulong divisor = 10000000000000000000UL;
                bool started = false;
                while (divisor > 0)
                {
                    int digit = (int)(e / divisor);
                    if (digit > 0 || started || divisor == 1)
                    {
                        _migrationUiBuffer[offset++] = (char)('0' + digit);
                        e %= divisor;
                        started = true;
                    }
                    divisor /= 10;
                }
            }
            LogicalEpochFrameText.SetCharArray(_migrationUiBuffer, 0, offset);
        }
        public void Update()
        {
            if (SyncProxy == null) return;
            UpdateEpochSync(SyncProxy.VisualLogicEpochCounter);
            UpdateMigrationEpoch(SyncProxy.VisualLogicalEpochFrameIndex);

            float playerHp = SyncProxy.VisualPlayerHp;
            if (PlayerHpSlider != null)
            {
                PlayerHpSlider.value = MaxPlayerHp <= 0f ? 0f : (playerHp / MaxPlayerHp);
            }
            if (PlayerHpText != null)
            {
                int hp = (int)playerHp;
                int maxHp = (int)MaxPlayerHp;
                _playerHpBuffer[0] = 'H'; _playerHpBuffer[1] = 'P'; _playerHpBuffer[2] = ':'; _playerHpBuffer[3] = ' ';
                int offset = WriteIntToBuffer(_playerHpBuffer, 4, hp);
                _playerHpBuffer[offset++] = ' '; _playerHpBuffer[offset++] = '/'; _playerHpBuffer[offset++] = ' ';
                offset = WriteIntToBuffer(_playerHpBuffer, offset, maxHp);
                PlayerHpText.SetCharArray(_playerHpBuffer, 0, offset);
            }

            float monsterHp = SyncProxy.VisualMonsterHp;
            if (MonsterHpSlider != null)
            {
                MonsterHpSlider.value = MaxMonsterHp <= 0f ? 0f : (monsterHp / MaxMonsterHp);
            }
            if (MonsterHpText != null)
            {
                int mHp = (int)monsterHp;
                int maxMHp = (int)MaxMonsterHp;
                _monsterHpBuffer[0] = 'E'; _monsterHpBuffer[1] = 'n'; _monsterHpBuffer[2] = 'e'; _monsterHpBuffer[3] = 'm'; _monsterHpBuffer[4] = 'y'; _monsterHpBuffer[5] = ' '; _monsterHpBuffer[6] = 'H'; _monsterHpBuffer[7] = 'P'; _monsterHpBuffer[8] = ':'; _monsterHpBuffer[9] = ' ';
                int offset = WriteIntToBuffer(_monsterHpBuffer, 10, mHp);
                _monsterHpBuffer[offset++] = ' '; _monsterHpBuffer[offset++] = '/'; _monsterHpBuffer[offset++] = ' ';
                offset = WriteIntToBuffer(_monsterHpBuffer, offset, maxMHp);
                MonsterHpText.SetCharArray(_monsterHpBuffer, 0, offset);
            }

            float progress = SyncProxy.VisualProgressTicks;
            if (ProgressSlider != null)
            {
                ProgressSlider.value = MaxProgressTicks <= 0f ? 0f : (progress / MaxProgressTicks);
            }
            if (ProgressText != null)
            {
                WriteMetricWithMax(ProgressText, _progressBuffer, "Progress: ", (int)progress, (int)MaxProgressTicks);
            }

            float woodcuttingXp = SyncProxy.VisualWoodcuttingXp;
            if (WoodcuttingXpSlider != null)
            {
                WoodcuttingXpSlider.value = MaxWoodcuttingXp <= 0f ? 0f : (woodcuttingXp / MaxWoodcuttingXp);
            }
            if (WoodcuttingXpText != null)
            {
                WriteMetricWithMax(WoodcuttingXpText, _woodcuttingXpBuffer, "Woodcutting XP: ", (int)woodcuttingXp, (int)MaxWoodcuttingXp);
            }

            float miningXp = SyncProxy.VisualMiningXp;
            if (MiningXpSlider != null)
            {
                MiningXpSlider.value = MaxMiningXp <= 0f ? 0f : (miningXp / MaxMiningXp);
            }
            if (MiningXpText != null)
            {
                WriteMetricWithMax(MiningXpText, _miningXpBuffer, "Mining XP: ", (int)miningXp, (int)MaxMiningXp);
            }

            float gatheringProgress = SyncProxy.VisualGatheringProgress;
            if (GatheringProgressSlider != null)
            {
                GatheringProgressSlider.value = MaxGatheringTicks <= 0f ? 0f : (gatheringProgress / MaxGatheringTicks);
            }
            if (GatheringProgressText != null)
            {
                WriteMetricWithMax(GatheringProgressText, _gatheringBuffer, "Gathering: ", (int)gatheringProgress, (int)MaxGatheringTicks);
            }

            if (MarketItemPriceText != null)
            {
                int index = WriteTextToBuffer(_marketBuffer, 0, "Price: ");
                index = WriteIntToBuffer(_marketBuffer, index, (int)CurrentMarketItemPrice);
                _marketBuffer[index++] = 'g';
                MarketItemPriceText.SetCharArray(_marketBuffer, 0, index);
            }

            if (BankSlotsText != null)
            {
                WriteMetricWithMax(BankSlotsText, _bankSlotsBuffer, "Bank Slots: ", (int)CurrentBankSlots, 100);
            }

            if (MailboxAttachmentText != null)
            {
                int index = WriteTextToBuffer(_mailboxAttachmentBuffer, 0, "Attached Gold: ");
                index = WriteIntToBuffer(_mailboxAttachmentBuffer, index, (int)CurrentMailAttachments);
                _mailboxAttachmentBuffer[index++] = 'g';
                MailboxAttachmentText.SetCharArray(_mailboxAttachmentBuffer, 0, index);
            }

            if (AccumulatedTimeBankText != null)
            {
                long totalSeconds = (long)(SyncProxy.VisualAccumulatedTimeBankMs / 1000);
                long hours = totalSeconds / 3600;
                long minutes = (totalSeconds % 3600) / 60;
                long seconds = totalSeconds % 60;
                
                int index = 0;
                if (hours > 99)
                {
                    _timeBankBuffer[index++] = (char)('0' + (hours / 100));
                    _timeBankBuffer[index++] = (char)('0' + ((hours / 10) % 10));
                    _timeBankBuffer[index++] = (char)('0' + (hours % 10));
                }
                else
                {
                    _timeBankBuffer[index++] = (char)('0' + (hours / 10));
                    _timeBankBuffer[index++] = (char)('0' + (hours % 10));
                }
                _timeBankBuffer[index++] = ':';
                _timeBankBuffer[index++] = (char)('0' + (minutes / 10));
                _timeBankBuffer[index++] = (char)('0' + (minutes % 10));
                _timeBankBuffer[index++] = ':';
                _timeBankBuffer[index++] = (char)('0' + (seconds / 10));
                _timeBankBuffer[index++] = (char)('0' + (seconds % 10));

                AccumulatedTimeBankText.SetCharArray(_timeBankBuffer, 0, index);
            }

            if (BankedChronoText != null)
            {
                long totalSeconds = (long)SyncProxy.VisualBankedChronoSeconds;
                if (totalSeconds < 0) totalSeconds = 0;
                long hours = totalSeconds / 3600;
                long minutes = (totalSeconds % 3600) / 60;

                int index = WriteTextToBuffer(_chronoGlobalUiBuffer, 0, "Chrono ");
                index = WriteLongToBuffer(_chronoGlobalUiBuffer, index, hours);
                _chronoGlobalUiBuffer[index++] = 'h';
                _chronoGlobalUiBuffer[index++] = ' ';
                index = WriteLongToBuffer(_chronoGlobalUiBuffer, index, minutes);
                _chronoGlobalUiBuffer[index++] = 'm';
                _chronoGlobalUiBuffer[index++] = ' ';
                if (SyncProxy.VisualChronoEngineStatus == 2)
                {
                    index = WriteTextToBuffer(_chronoGlobalUiBuffer, index, "Active");
                }
                else if (SyncProxy.VisualChronoEngineStatus == 1)
                {
                    index = WriteTextToBuffer(_chronoGlobalUiBuffer, index, "Banked");
                }
                else
                {
                    index = WriteTextToBuffer(_chronoGlobalUiBuffer, index, "Idle");
                }
                BankedChronoText.SetCharArray(_chronoGlobalUiBuffer, 0, index);
            }

            if (ChronoMultiplierText != null)
            {
                _chronoMultiplierBuffer[0] = 'x';
                if (SyncProxy.VisualIsChronoAccelerating)
                {
                    byte multiplier = SyncProxy.VisualCurrentSimulationSpeedMultiplier;
                    if (multiplier != 2 && multiplier != 4) multiplier = 2;
                    _chronoMultiplierBuffer[1] = (char)('0' + multiplier);
                    _chronoMultiplierBuffer[2] = ' ';
                    _chronoMultiplierBuffer[3] = 'A';
                    _chronoMultiplierBuffer[4] = 'c';
                    _chronoMultiplierBuffer[5] = 't';
                    _chronoMultiplierBuffer[6] = 'i';
                    _chronoMultiplierBuffer[7] = 'v';
                    _chronoMultiplierBuffer[8] = 'e';
                    ChronoMultiplierText.SetCharArray(_chronoMultiplierBuffer, 0, 9);
                }
                else
                {
                    _chronoMultiplierBuffer[1] = '1';
                    _chronoMultiplierBuffer[2] = ' ';
                    _chronoMultiplierBuffer[3] = 'I';
                    _chronoMultiplierBuffer[4] = 'd';
                    _chronoMultiplierBuffer[5] = 'l';
                    _chronoMultiplierBuffer[6] = 'e';
                    ChronoMultiplierText.SetCharArray(_chronoMultiplierBuffer, 0, 7);
                }
            }

            if (VillagePopulationText != null)
            {
                _populationBuffer[0] = 'P'; _populationBuffer[1] = 'o'; _populationBuffer[2] = 'p'; _populationBuffer[3] = 'u'; _populationBuffer[4] = 'l'; _populationBuffer[5] = 'a'; _populationBuffer[6] = 't'; _populationBuffer[7] = 'i'; _populationBuffer[8] = 'o'; _populationBuffer[9] = 'n'; _populationBuffer[10] = ':'; _populationBuffer[11] = ' ';
                int index = WriteIntToBuffer(_populationBuffer, 12, SyncProxy.VisualCurrentPopulationCount);
                _populationBuffer[index++] = ' '; _populationBuffer[index++] = '/'; _populationBuffer[index++] = ' ';
                index = WriteIntToBuffer(_populationBuffer, index, SyncProxy.VisualMaxVillagePopulation);
                VillagePopulationText.SetCharArray(_populationBuffer, 0, index);
            }

            if (VillageInfrastructureText != null)
            {
                int index = WriteTextToBuffer(_villageBuffer, 0, "Forge ");
                index = WriteIntToBuffer(_villageBuffer, index, SyncProxy.VisualForgeLevel);
                index = WriteTextToBuffer(_villageBuffer, index, " Inn ");
                index = WriteIntToBuffer(_villageBuffer, index, SyncProxy.VisualInnLevel);
                index = WriteTextToBuffer(_villageBuffer, index, " Breed ");
                index = WriteIntToBuffer(_villageBuffer, index, SyncProxy.VisualBreedingLevel);
                index = WriteTextToBuffer(_villageBuffer, index, " Academy ");
                index = WriteIntToBuffer(_villageBuffer, index, SyncProxy.VisualAcademyLevel);
                index = WriteTextToBuffer(_villageBuffer, index, " Pop ");
                index = WriteIntToBuffer(_villageBuffer, index, SyncProxy.VisualCurrentPopulationCount);
                _villageBuffer[index++] = '/';
                index = WriteIntToBuffer(_villageBuffer, index, SyncProxy.VisualMaxVillagePopulation);
                VillageInfrastructureText.SetCharArray(_villageBuffer, 0, index);
            }

            if (LegacyShardBalanceText != null)
            {
                int index = WriteTextToBuffer(_legacyBuffer, 0, "Legacy: ");
                index = WriteIntToBuffer(_legacyBuffer, index, SyncProxy.GetLegacyShardBalance());
                LegacyShardBalanceText.SetCharArray(_legacyBuffer, 0, index);
            }

            if (ChroniclePassText != null)
            {
                int index = WriteTextToBuffer(_seasonalUiBuffer, 0, "Pass ");
                index = WriteLongToBuffer(_seasonalUiBuffer, index, SyncProxy.VisualActiveChroniclePassLevel);
                index = WriteTextToBuffer(_seasonalUiBuffer, index, " XP ");
                index = WriteLongToBuffer(_seasonalUiBuffer, index, SyncProxy.VisualAccumulatedSeasonalXp);
                index = WriteTextToBuffer(_seasonalUiBuffer, index, " / ");
                long nextRequirement = ((long)SyncProxy.VisualActiveChroniclePassLevel + 1L) * 1000L;
                index = WriteLongToBuffer(_seasonalUiBuffer, index, nextRequirement);
                ChroniclePassText.SetCharArray(_seasonalUiBuffer, 0, index);
            }

            if (AnalyticsSummaryText != null)
            {
                ulong analyticsCount = SyncProxy.VisualTotalAnalyticsEventsLoggedCount;
                long displayCount = analyticsCount > long.MaxValue ? long.MaxValue : (long)analyticsCount;
                int index = WriteTextToBuffer(_analyticsUiBuffer, 0, "Analytics Events: ");
                index = WriteLongToBuffer(_analyticsUiBuffer, index, displayCount);
                AnalyticsSummaryText.SetCharArray(_analyticsUiBuffer, 0, index);
            }

            if (GuildLogisticsDepotText != null)
            {
                int index = WriteTextToBuffer(_guildLogisticsBuffer, 0, "Depot: ");
                index = WriteLongToBuffer(_guildLogisticsBuffer, index, SyncProxy.VisualGuildLogisticsCurrentStock);
                _guildLogisticsBuffer[index++] = ' ';
                _guildLogisticsBuffer[index++] = '/';
                _guildLogisticsBuffer[index++] = ' ';
                index = WriteLongToBuffer(_guildLogisticsBuffer, index, SyncProxy.VisualGuildLogisticsTargetRequirement);
                GuildLogisticsDepotText.SetCharArray(_guildLogisticsBuffer, 0, index);
            }

            if (CombatSimulationText != null)
            {
                int index = WriteTextToBuffer(_combatSimulationBuffer, 0, "Combat M");
                index = WriteLongToBuffer(_combatSimulationBuffer, index, SyncProxy.VisualCombatSimulationMatchId);
                index = WriteTextToBuffer(_combatSimulationBuffer, index, " T");
                index = WriteIntToBuffer(_combatSimulationBuffer, index, SyncProxy.VisualCombatSimulationTurnCounter);
                index = WriteTextToBuffer(_combatSimulationBuffer, index, " D");
                index = WriteIntToBuffer(_combatSimulationBuffer, index, SyncProxy.VisualCombatSimulationDamageDelta);
                CombatSimulationText.SetCharArray(_combatSimulationBuffer, 0, index);
            }

            if (RerollCostText != null)
            {
                WriteMetric(RerollCostText, _rerollBuffer, "Cost: ", (int)CurrentRerollCost);
            }

            if (ForgeAffixStateText != null)
            {
                _forgeBuffer[0] = 'A';
                _forgeBuffer[1] = 'f';
                _forgeBuffer[2] = 'f';
                _forgeBuffer[3] = 'i';
                _forgeBuffer[4] = 'x';
                _forgeBuffer[5] = ':';
                _forgeBuffer[6] = ' ';

                bool locked = SyncProxy.VisualWeaponAffixLocked || SyncProxy.VisualArmorAffixLocked;
                if (locked)
                {
                    _forgeBuffer[7] = 'L';
                    _forgeBuffer[8] = 'o';
                    _forgeBuffer[9] = 'c';
                    _forgeBuffer[10] = 'k';
                    _forgeBuffer[11] = 'e';
                    _forgeBuffer[12] = 'd';
                    ForgeAffixStateText.SetCharArray(_forgeBuffer, 0, 13);
                }
                else
                {
                    _forgeBuffer[7] = 'O';
                    _forgeBuffer[8] = 'p';
                    _forgeBuffer[9] = 'e';
                    _forgeBuffer[10] = 'n';
                    ForgeAffixStateText.SetCharArray(_forgeBuffer, 0, 11);
                }
            }

            if (MiningMonolithLevelText != null)
            {
                _miningMonolithBuffer[0] = 'M'; _miningMonolithBuffer[1] = 'i'; _miningMonolithBuffer[2] = 'n'; _miningMonolithBuffer[3] = 'e'; _miningMonolithBuffer[4] = ' '; _miningMonolithBuffer[5] = 'L'; _miningMonolithBuffer[6] = 'V'; _miningMonolithBuffer[7] = 'L'; _miningMonolithBuffer[8] = ':'; _miningMonolithBuffer[9] = ' ';
                int index = WriteIntToBuffer(_miningMonolithBuffer, 10, SyncProxy.VisualMiningMonolithLevel);
                MiningMonolithLevelText.SetCharArray(_miningMonolithBuffer, 0, index);
            }

            if (WoodcuttingMonolithLevelText != null)
            {
                _woodcuttingMonolithBuffer[0] = 'W'; _woodcuttingMonolithBuffer[1] = 'o'; _woodcuttingMonolithBuffer[2] = 'o'; _woodcuttingMonolithBuffer[3] = 'd'; _woodcuttingMonolithBuffer[4] = ' '; _woodcuttingMonolithBuffer[5] = 'L'; _woodcuttingMonolithBuffer[6] = 'V'; _woodcuttingMonolithBuffer[7] = 'L'; _woodcuttingMonolithBuffer[8] = ':'; _woodcuttingMonolithBuffer[9] = ' ';
                int index = WriteIntToBuffer(_woodcuttingMonolithBuffer, 10, SyncProxy.VisualWoodcuttingMonolithLevel);
                WoodcuttingMonolithLevelText.SetCharArray(_woodcuttingMonolithBuffer, 0, index);
            }

            float worldBossHp = SyncProxy.VisualWorldBossHp;
            float worldBossMaxHp = SyncProxy.VisualWorldBossMaxHp;
            byte activeLanguage = SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
            if (WorldBossHpSlider != null)
            {
                WorldBossHpSlider.value = worldBossMaxHp <= 0f ? 0f : (worldBossHp / worldBossMaxHp);
            }
            if (WorldBossHpText != null && worldBossMaxHp > 0)
            {
                int index = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.BossHpPrefix, _worldBossHpBuffer, 0);
                index = WriteLongToBuffer(_worldBossHpBuffer, index, (long)worldBossHp);
                _worldBossHpBuffer[index++] = ' '; _worldBossHpBuffer[index++] = '/'; _worldBossHpBuffer[index++] = ' ';
                index = WriteLongToBuffer(_worldBossHpBuffer, index, (long)worldBossMaxHp);
                WorldBossHpText.SetCharArray(_worldBossHpBuffer, 0, index);
            }

            if (GlobalEventText != null)
            {
                int index = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.ActiveEventPrefix, _liveOpsBuffer, 0);
                index = LocalizationMatrix.WriteToCharBuffer(activeLanguage, ResolveEventLocalizationKey(SyncProxy.VisualGlobalEventId), _liveOpsBuffer, index);
                GlobalEventText.SetCharArray(_liveOpsBuffer, 0, index);
            }

            if (NotificationQueueText != null)
            {
                int index = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.PushQueuePrefix, _notificationBuffer, 0);
                index = WriteIntToBuffer(_notificationBuffer, index, SyncProxy.VisualNotificationQueueStateLength);
                NotificationQueueText.SetCharArray(_notificationBuffer, 0, index);
            }

            if (ActiveAgePhaseText != null)
            {
                int index = WriteTextToBuffer(_agePhaseBuffer, 0, "Age Phase: ");
                switch (SyncProxy.VisualSlot1AgePhase)
                {
                    case 1:
                        index = WriteTextToBuffer(_agePhaseBuffer, index, "Adult");
                        break;
                    case 2:
                        index = WriteTextToBuffer(_agePhaseBuffer, index, "Senior");
                        break;
                    case 3:
                        index = WriteTextToBuffer(_agePhaseBuffer, index, "Old");
                        break;
                    default:
                        index = WriteTextToBuffer(_agePhaseBuffer, index, "Child");
                        break;
                }
                ActiveAgePhaseText.SetCharArray(_agePhaseBuffer, 0, index);
            }

            if (MentorCountText != null)
            {
                WriteMetricWithMax(MentorCountText, _mentorBuffer, "Mentors: ", SyncProxy.VisualMentorCount, 5);
            }

            if (MentorshipContractText != null)
            {
                int scaled = (int)(SyncProxy.VisualMentorshipExpBonusMultiplier * 100.0 + 0.5);
                if (scaled < 100) scaled = 100;
                int index = WriteTextToBuffer(_mentorshipBuffer, 0, "Mentor x");
                index = WriteIntToBuffer(_mentorshipBuffer, index, scaled / 100);
                _mentorshipBuffer[index++] = '.';
                index = WriteTwoDigits(_mentorshipBuffer, index, scaled % 100);
                MentorshipContractText.SetCharArray(_mentorshipBuffer, 0, index);
            }

            if (CompletedAreaFlagsText != null)
            {
                WriteMetric(CompletedAreaFlagsText, _completedAreaBuffer, "Areas Completed: ", SyncProxy.VisualCompletedAreaFlags);
            }

            if (ClaimedAchievementFlagsText != null)
            {
                WriteMetric(ClaimedAchievementFlagsText, _claimedAchievementBuffer, "Achievements: ", SyncProxy.VisualClaimedAchievementFlags);
            }

            if (HumanMasteryLevelText != null)
            {
                WriteMetric(HumanMasteryLevelText, _humanMasteryBuffer, "Human Mastery: ", SyncProxy.VisualHumanMasteryLevel);
            }

            if (VilaMasteryLevelText != null)
            {
                WriteMetric(VilaMasteryLevelText, _vilaMasteryBuffer, "Vila Mastery: ", SyncProxy.VisualVilaMasteryLevel);
            }

            if (DraugrMasteryLevelText != null)
            {
                WriteMetric(DraugrMasteryLevelText, _draugrMasteryBuffer, "Draugr Mastery: ", SyncProxy.VisualDraugrMasteryLevel);
            }

            bool hasLegacyGuildWar = SyncProxy.VisualActiveGuildWarId > 0;
            if (SyncProxy.VisualActiveMatchId != System.Guid.Empty)
            {
                if (GuildWarStatusText != null)
                {
                    int index = WriteTextToBuffer(_guildWarUiBuffer, 0, "Cross Match HP: ");
                    index = WriteLongToBuffer(_guildWarUiBuffer, index, SyncProxy.VisualGlobalNodeRemainingHp);
                    index = WriteTextToBuffer(_guildWarUiBuffer, index, " MMR: ");
                    index = WriteLongToBuffer(_guildWarUiBuffer, index, SyncProxy.VisualActiveMatchMmr);
                    GuildWarStatusText.SetCharArray(_guildWarUiBuffer, 0, index);
                }
            }
            else if (hasLegacyGuildWar)
            {
                if (GuildWarStatusText != null)
                {
                    int index = WriteTextToBuffer(_guildWarStatusBuffer, 0, "Active War! Multiplier: x");
                    index = WriteWarMultiplier(_guildWarStatusBuffer, index, SyncProxy.VisualWarMultiplier);
                    GuildWarStatusText.SetCharArray(_guildWarStatusBuffer, 0, index);
                }
            }
            else
            {
                if (GuildWarStatusText != null)
                {
                    int index = WriteTextToBuffer(_guildWarStatusBuffer, 0, "No active war. Multiplier: x1.00");
                    GuildWarStatusText.SetCharArray(_guildWarStatusBuffer, 0, index);
                }
            }

            if (hasLegacyGuildWar)
            {
                if (GuildCombatPointsText != null) WriteMetric(GuildCombatPointsText, _guildCombatBuffer, "Our Combat WP: ", SyncProxy.VisualGuildCombatPoints);
                if (GuildLogisticsPointsText != null) WriteMetric(GuildLogisticsPointsText, _guildWarLogisticsBuffer, "Our Logistics WP: ", SyncProxy.VisualGuildLogisticsPoints);
                if (GuildSupplyPointsText != null) WriteMetric(GuildSupplyPointsText, _guildSupplyBuffer, "Our Supply WP: ", SyncProxy.VisualGuildSupplyPoints);
                if (EnemyCombatPointsText != null) WriteMetric(EnemyCombatPointsText, _enemyCombatBuffer, "Enemy Combat WP: ", SyncProxy.VisualEnemyCombatPoints);
                if (EnemyLogisticsPointsText != null) WriteMetric(EnemyLogisticsPointsText, _enemyLogisticsBuffer, "Enemy Logistics WP: ", SyncProxy.VisualEnemyLogisticsPoints);
                if (EnemySupplyPointsText != null) WriteMetric(EnemySupplyPointsText, _enemySupplyBuffer, "Enemy Supply WP: ", SyncProxy.VisualEnemySupplyPoints);
            }
            else
            {
                if (GuildCombatPointsText != null) WriteMetric(GuildCombatPointsText, _guildCombatBuffer, "Our Combat WP: ", 0);
                if (GuildLogisticsPointsText != null) WriteMetric(GuildLogisticsPointsText, _guildWarLogisticsBuffer, "Our Logistics WP: ", 0);
                if (GuildSupplyPointsText != null) WriteMetric(GuildSupplyPointsText, _guildSupplyBuffer, "Our Supply WP: ", 0);
                if (EnemyCombatPointsText != null) WriteMetric(EnemyCombatPointsText, _enemyCombatBuffer, "Enemy Combat WP: ", 0);
                if (EnemyLogisticsPointsText != null) WriteMetric(EnemyLogisticsPointsText, _enemyLogisticsBuffer, "Enemy Logistics WP: ", 0);
                if (EnemySupplyPointsText != null) WriteMetric(EnemySupplyPointsText, _enemySupplyBuffer, "Enemy Supply WP: ", 0);
            }

            if (PremiumCurrencyText != null)
            {
                int index = WriteTextToBuffer(_premiumUiBuffer, 0, "Premium: ");
                index = WriteLongToBuffer(_premiumUiBuffer, index, SyncProxy.VisualPremiumCurrencyBalance);
                _premiumUiBuffer[index++] = ' ';
                _premiumUiBuffer[index++] = '|';
                _premiumUiBuffer[index++] = ' ';
                _premiumUiBuffer[index++] = 'T';
                _premiumUiBuffer[index++] = 'X';
                _premiumUiBuffer[index++] = 's';
                _premiumUiBuffer[index++] = ':';
                _premiumUiBuffer[index++] = ' ';
                index = WriteLongToBuffer(_premiumUiBuffer, index, SyncProxy.VisualEventHorizonTransactionCount);
                PremiumCurrencyText.SetCharArray(_premiumUiBuffer, 0, index);
            }

            if (CraftingStateText != null)
            {
                int index = WriteTextToBuffer(_craftingUiBuffer, 0, "Crafted: ");
                index = WriteLongToBuffer(_craftingUiBuffer, index, SyncProxy.VisualTotalItemsCraftedCount);
                _craftingUiBuffer[index++] = ' ';
                _craftingUiBuffer[index++] = '|';
                _craftingUiBuffer[index++] = ' ';
                _craftingUiBuffer[index++] = 'S';
                _craftingUiBuffer[index++] = 't';
                _craftingUiBuffer[index++] = 'a';
                _craftingUiBuffer[index++] = 't';
                _craftingUiBuffer[index++] = 'e';
                _craftingUiBuffer[index++] = ':';
                _craftingUiBuffer[index++] = ' ';
                index = WriteIntToBuffer(_craftingUiBuffer, index, SyncProxy.VisualCraftingEngineStatus);
                CraftingStateText.SetCharArray(_craftingUiBuffer, 0, index);
            }

            if (AchievementsCountText != null)
            {
                int index = WriteTextToBuffer(_achievementsUiBuffer, 0, "Achievements: ");
                index = WriteLongToBuffer(_achievementsUiBuffer, index, SyncProxy.VisualTotalAchievementsClaimedCount);
                AchievementsCountText.SetCharArray(_achievementsUiBuffer, 0, index);
            }

            if (MasteryBitmaskText != null)
            {
                int index = WriteTextToBuffer(_masteryUiBuffer, 0, "Mastery Mask: 0x");
                // simple hex write
                uint mask = SyncProxy.VisualActiveMasteryBitmask;
                for (int i = 7; i >= 0; i--)
                {
                    uint nibble = (mask >> (i * 4)) & 0xF;
                    _masteryUiBuffer[index++] = (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
                }
                MasteryBitmaskText.SetCharArray(_masteryUiBuffer, 0, index);
            }

            UpdateNetworkDiagnostics();
        }
        public void UpdateOtaStatus(int percent, long bytesRemaining)
        {
            if (OtaStatusText == null) return;
            
            _otaBuffer[0] = 'O';
            _otaBuffer[1] = 'T';
            _otaBuffer[2] = 'A';
            _otaBuffer[3] = ':';
            _otaBuffer[4] = ' ';

            int offset = 5;
            
            if (percent == 0)
            {
                _otaBuffer[offset++] = '0';
            }
            else
            {
                int p = percent;
                int divisor = 100;
                bool started = false;
                while (divisor > 0)
                {
                    int digit = p / divisor;
                    if (digit > 0 || started || divisor == 1)
                    {
                        _otaBuffer[offset++] = (char)('0' + digit);
                        started = true;
                    }
                    p %= divisor;
                    divisor /= 10;
                }
            }
            
            _otaBuffer[offset++] = '%';
            _otaBuffer[offset++] = ' ';
            _otaBuffer[offset++] = '|';
            _otaBuffer[offset++] = ' ';

            if (bytesRemaining == 0)
            {
                _otaBuffer[offset++] = '0';
            }
            else
            {
                long b = bytesRemaining;
                long divisor = 1000000000000L; 
                bool started = false;
                while (divisor > 0)
                {
                    int digit = (int)(b / divisor);
                    if (digit > 0 || started || divisor == 1)
                    {
                        _otaBuffer[offset++] = (char)('0' + digit);
                        started = true;
                    }
                    b %= divisor;
                    divisor /= 10;
                }
            }

            _otaBuffer[offset++] = ' ';
            _otaBuffer[offset++] = 'B';
            
            OtaStatusText.SetCharArray(_otaBuffer, 0, offset);
        }

        private void UpdateNetworkDiagnostics()
        {
            if (NetworkDiagnosticsText == null) return;
            
            int offset = 0;
            _networkUiBuffer[offset++] = 'N';
            _networkUiBuffer[offset++] = 'e';
            _networkUiBuffer[offset++] = 't';
            _networkUiBuffer[offset++] = ' ';
            _networkUiBuffer[offset++] = 'I';
            _networkUiBuffer[offset++] = 'O';
            _networkUiBuffer[offset++] = ':';
            _networkUiBuffer[offset++] = ' ';
            offset = WriteLongToBuffer(_networkUiBuffer, offset, VisualProxy.VisualActiveConnectionThroughput);
            _networkUiBuffer[offset++] = 'B';
            _networkUiBuffer[offset++] = '/';
            _networkUiBuffer[offset++] = 's';
            _networkUiBuffer[offset++] = ' ';
            _networkUiBuffer[offset++] = '|';
            _networkUiBuffer[offset++] = ' ';
            _networkUiBuffer[offset++] = 'M';
            _networkUiBuffer[offset++] = 'e';
            _networkUiBuffer[offset++] = 'm';
            _networkUiBuffer[offset++] = ':';
            _networkUiBuffer[offset++] = ' ';
            offset = WriteLongToBuffer(_networkUiBuffer, offset, VisualProxy.VisualCurrentNodeMemoryLoadMetrics);
            _networkUiBuffer[offset++] = 'M';
            _networkUiBuffer[offset++] = 'B';
            
            NetworkDiagnosticsText.SetCharArray(_networkUiBuffer, 0, offset);
        }

        public unsafe void UpdateLeaderboardRow(TMP_Text targetText, int rank, char* unmanagedNamePtr, int nameLength, int level, long xp)
        {
            if (targetText == null) return;

            // 1. Write Rank
            int offset = 0;
            _leaderboardUiBuffer[offset++] = '#';
            offset = WriteIntToBuffer(_leaderboardUiBuffer, offset, rank);
            _leaderboardUiBuffer[offset++] = ' ';
            _leaderboardUiBuffer[offset++] = '-';
            _leaderboardUiBuffer[offset++] = ' ';

            // 2. Write Name with clamped Unsafe.CopyBlock
            int safeCopyCount = Math.Min(nameLength, 32);
            int remainingSpace = _leaderboardUiBuffer.Length - offset;
            safeCopyCount = Math.Min(safeCopyCount, remainingSpace);

            if (safeCopyCount > 0 && unmanagedNamePtr != null)
            {
                fixed (char* destPtr = &_leaderboardUiBuffer[offset])
                {
                    Unsafe.CopyBlock(destPtr, unmanagedNamePtr, (uint)(safeCopyCount * sizeof(char)));
                }
                offset += safeCopyCount;
            }

            // 3. Write Level and XP
            string lvlSuffix = " Lvl: ";
            for (int i = 0; i < lvlSuffix.Length; i++) _leaderboardUiBuffer[offset++] = lvlSuffix[i];
            
            offset = WriteIntToBuffer(_leaderboardUiBuffer, offset, level);

            string xpSuffix = " XP: ";
            for (int i = 0; i < xpSuffix.Length; i++) _leaderboardUiBuffer[offset++] = xpSuffix[i];

            offset = WriteLongToBuffer(_leaderboardUiBuffer, offset, xp);

            targetText.SetCharArray(_leaderboardUiBuffer, 0, offset);
            if (SyncProxy.VisualActiveStatusEffectModifierBitmask > 0)
            {
                if (ActiveModifiersText != null)
                {
                    uint mask = SyncProxy.VisualActiveStatusEffectModifierBitmask;
                    int length = 0;
                    _productionUiBuffer[length++] = 'B';
                    _productionUiBuffer[length++] = 'U';
                    _productionUiBuffer[length++] = 'F';
                    _productionUiBuffer[length++] = 'F';
                    _productionUiBuffer[length++] = 'S';
                    _productionUiBuffer[length++] = ':';
                    _productionUiBuffer[length++] = ' ';
                    
                    if ((mask & 1) != 0) { _productionUiBuffer[length++] = 'H'; _productionUiBuffer[length++] = 'P'; _productionUiBuffer[length++] = ' '; }
                    if ((mask & 2) != 0) { _productionUiBuffer[length++] = 'P'; _productionUiBuffer[length++] = 'O'; _productionUiBuffer[length++] = 'T'; _productionUiBuffer[length++] = ' '; }
                    
                    ActiveModifiersText.SetText(_productionUiBuffer, 0, length);
                }
                
                if (BuffDurationText != null)
                {
                    uint ticks = SyncProxy.VisualRemainingBuffDurationTicks;
                    int length = 0;
                    _productionUiBuffer[length++] = 'T';
                    _productionUiBuffer[length++] = 'I';
                    _productionUiBuffer[length++] = 'M';
                    _productionUiBuffer[length++] = 'E';
                    _productionUiBuffer[length++] = ':';
                    _productionUiBuffer[length++] = ' ';
                    
                    // Simple uint to char array for zero-alloc
                    uint temp = ticks;
                    int digits = 0;
                    if (temp == 0) digits = 1;
                    else while (temp > 0) { digits++; temp /= 10; }
                    
                    temp = ticks;
                    for (int i = length + digits - 1; i >= length; i--)
                    {
                        _productionUiBuffer[i] = (char)('0' + (temp % 10));
                        temp /= 10;
                    }
                    length += digits;
                    
                    BuffDurationText.SetText(_productionUiBuffer, 0, length);
                }
            }
            else
            {
                if (ActiveModifiersText != null) ActiveModifiersText.SetText("NO BUFFS");
                if (BuffDurationText != null) BuffDurationText.SetText("TIME: 0");
            }
        }
    }
}
