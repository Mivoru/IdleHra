using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: UI rework. The Combat screen, rebuilt.
    //
    // What it replaces: five identical "Region N (0 / 1000)" rows, each with
    // a dropdown listing the player's ENTIRE discovered monster codex
    // regardless of region (the client mirror of monsters.json did not carry
    // RegionTier, so it genuinely could not tell which monsters lived
    // where), and no art, no stats, no feedback of any kind.
    //
    // What it is now, top to bottom:
    //   < arrow >   big location art        - page through locations
    //               location name + progress
    //   monster roster (weakest first, boss last, real HP/ATK/kill counts)
    //   -> selecting a monster swaps the big art to that monster and shows
    //      its name and a live HP bar while you are fighting it
    //   your character stats
    //   food + potion slots
    //   this session's tally
    //   [ Fight ]
    //
    // Everything is driven by real data: RegionTier/MaxHp/AttackPower from
    // GameData/monsters.json, kill counts and region progress from the Codex
    // caches, live monster HP and player stats from the state packet, and
    // the session tally from CombatSessionTracker.
    public class UiCombatLocationPanel : MonoBehaviour
    {
        private const int CharacterSlotCount = 4;
        private const float SessionRefreshIntervalSeconds = 5f;

        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;
        public AssetRegistry Registry;
        public UiTabGroup ScreenTabGroup;
        public int CharacterScreenIndex;

        [Header("Location header")]
        public Button PreviousLocationButton;
        public Button NextLocationButton;
        public Image FeatureImage;
        public TMP_Text FeatureCaptionText;
        public TMP_Text LocationNameText;
        public TMP_Text LocationProgressText;
        public RectTransform LocationProgressFill;

        [Header("Selected target")]
        public GameObject TargetHealthRoot;
        public TMP_Text TargetNameText;
        public TMP_Text TargetHealthText;
        public RectTransform TargetHealthFill;

        [Header("Monster roster - pooled")]
        public Transform MonsterRowContainer;
        public UiCombatMonsterRow MonsterRowPrefab;
        public int InitialRowPoolCapacity = 8;

        [Header("Character stats")]
        public TMP_Text CharacterStatsText;
        public TMP_Text CharacterHealthText;

        [Header("Character slots")]
        public TMP_Text[] CharacterSlotTexts = new TMP_Text[CharacterSlotCount];
        public Button[] CharacterSlotButtons = new Button[CharacterSlotCount];
        public GameObject[] CharacterSlotSelectedHighlights = new GameObject[CharacterSlotCount];

        [Header("Consumables")]
        public TMP_Dropdown FoodDropdown;
        public Button UseFoodButton;
        public TMP_Dropdown PotionDropdown;
        public Button UsePotionButton;
        public TMP_Text ActiveBuffText;

        [Header("Session tally")]
        public TMP_Text SessionSummaryText;
        public TMP_Text SessionKillListText;

        [Header("Deploy")]
        public Button DeployButton;
        public TMP_Text DeployButtonLabel;
        public TMP_Text StatusText;

        private const int MaxSessionDropLines = 6;
        private const int MaxSessionKillLines = 8;

        private readonly System.Text.StringBuilder _sessionTextBuilder = new System.Text.StringBuilder(512);
        private readonly List<string> _dropdownOptionBuffer = new List<string>(8);
        private readonly List<UiCombatMonsterRow> _activeRows = new List<UiCombatMonsterRow>();
        private UIComponentPool<UiCombatMonsterRow> _rowPool;

        private int _regionIndex;
        private int _selectedMonsterId;
        private int _selectedCharacterSlotIndex = -1;
        private float _sessionRefreshTimer;

        private void Awake()
        {
            if (MonsterRowPrefab != null && MonsterRowContainer != null)
            {
                _rowPool = new UIComponentPool<UiCombatMonsterRow>(MonsterRowPrefab, MonsterRowContainer, InitialRowPoolCapacity);
            }

            if (PreviousLocationButton != null) PreviousLocationButton.onClick.AddListener(() => StepLocation(-1));
            if (NextLocationButton != null) NextLocationButton.onClick.AddListener(() => StepLocation(1));
            if (DeployButton != null) DeployButton.onClick.AddListener(HandleDeployClicked);
            if (UseFoodButton != null) UseFoodButton.onClick.AddListener(HandleUseFoodClicked);
            if (UsePotionButton != null) UsePotionButton.onClick.AddListener(HandleUsePotionClicked);

            for (int i = 0; i < CharacterSlotButtons.Length; i++)
            {
                int index = i;
                if (CharacterSlotButtons[i] != null)
                {
                    CharacterSlotButtons[i].onClick.AddListener(() => HandleCharacterSlotClicked(index));
                }
            }

            PopulateConsumableDropdowns();
        }

        private void OnEnable()
        {
            CodexRegionsCache.OnCodexRegionsCacheUpdated += RefreshLocationHeader;
            CodexInventoryCache.OnCodexCacheUpdated += RefreshMonsterRoster;
            BreedingRosterCache.OnRosterCacheUpdated += RefreshCharacterSlots;
            CombatSessionTracker.OnSessionProgressUpdated += RefreshSessionTally;

            CodexRegionsCache.RequestSnapshot();
            CodexInventoryCache.RequestSnapshot();
            BreedingRosterCache.RequestSnapshot();

            RefreshEverything();
        }

        private void OnDisable()
        {
            CodexRegionsCache.OnCodexRegionsCacheUpdated -= RefreshLocationHeader;
            CodexInventoryCache.OnCodexCacheUpdated -= RefreshMonsterRoster;
            BreedingRosterCache.OnRosterCacheUpdated -= RefreshCharacterSlots;
            CombatSessionTracker.OnSessionProgressUpdated -= RefreshSessionTally;
        }

        // Live values only - everything structural is event-driven. This
        // runs solely while the Combat screen is the active screen, so an
        // idle player parked on the map costs nothing.
        private void Update()
        {
            RefreshLiveTargetHealth();
            RefreshCharacterStats();
            RefreshActiveBuffs();

            _sessionRefreshTimer += Time.deltaTime;
            if (_sessionRefreshTimer >= SessionRefreshIntervalSeconds)
            {
                _sessionRefreshTimer = 0f;
                CombatSessionTracker.Instance?.RequestRefresh();
            }
        }

        private void RefreshEverything()
        {
            RefreshLocationHeader();
            RefreshMonsterRoster();
            RefreshCharacterSlots();
            RefreshSessionTally();
            RefreshFeatureImage();
        }

        // ------------------------------------------------------------
        // Location paging
        // ------------------------------------------------------------
        private int CurrentRegionId
        {
            get
            {
                IReadOnlyList<int> ids = ClientContentRegistry.RegionIds;
                if (ids.Count == 0) return 1;
                return ids[Mathf.Clamp(_regionIndex, 0, ids.Count - 1)];
            }
        }

        private void StepLocation(int direction)
        {
            IReadOnlyList<int> ids = ClientContentRegistry.RegionIds;
            if (ids.Count == 0) return;

            // Wraps, so the arrows are never dead ends at either extreme.
            _regionIndex = (_regionIndex + direction + ids.Count) % ids.Count;

            // The previous location's monster is not in this one - clearing
            // it stops the header showing a target you can no longer pick.
            _selectedMonsterId = 0;

            RefreshLocationHeader();
            RefreshMonsterRoster();
            RefreshFeatureImage();
        }

        private void RefreshLocationHeader()
        {
            int regionId = CurrentRegionId;
            IReadOnlyList<int> ids = ClientContentRegistry.RegionIds;

            if (LocationNameText != null)
            {
                LocationNameText.text = ClientContentRegistry.GetRegionName(regionId);
            }

            long currentKills = 0;
            long requiredKills = 0;
            bool completed = false;

            IReadOnlyList<RegionProgressData> regions = CodexRegionsCache.Regions;
            for (int i = 0; i < regions.Count; i++)
            {
                if (regions[i].RegionId != regionId) continue;
                currentKills = regions[i].CurrentKills;
                requiredKills = regions[i].RequiredKills;
                completed = regions[i].IsCompleted;
                break;
            }

            if (LocationProgressText != null)
            {
                int position = Mathf.Clamp(_regionIndex, 0, Mathf.Max(0, ids.Count - 1)) + 1;
                string progress = requiredKills > 0
                    ? currentKills + " / " + requiredKills + " clears"
                    : "no progress recorded yet";
                LocationProgressText.text = "Location " + position + " of " + ids.Count + "   -   " + progress + (completed ? "   -   CLEARED" : string.Empty);
            }

            if (LocationProgressFill != null)
            {
                float fraction = requiredKills > 0 ? Mathf.Clamp01((float)currentKills / requiredKills) : 0f;
                Vector2 anchorMax = LocationProgressFill.anchorMax;
                anchorMax.x = fraction;
                LocationProgressFill.anchorMax = anchorMax;
            }
        }

        // ------------------------------------------------------------
        // Monster roster
        // ------------------------------------------------------------
        private void RefreshMonsterRoster()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<MonsterEntry> monsters = ClientContentRegistry.GetMonstersInRegion(CurrentRegionId);
            IReadOnlyList<CodexSnapshotEntryData> codex = CodexInventoryCache.Entries;

            for (int i = 0; i < monsters.Count; i++)
            {
                MonsterEntry monster = monsters[i];

                long kills = 0;
                for (int c = 0; c < codex.Count; c++)
                {
                    if (codex[c].MonsterId != monster.Id) continue;
                    kills = codex[c].Kills;
                    break;
                }

                Sprite icon = null;
                Registry?.TryGetMonsterSprite(monster.Id, out icon);

                bool isBoss = i == monsters.Count - 1;

                UiCombatMonsterRow row = _rowPool.Spawn();
                row.Bind(monster.Id, monster.Name, monster.MaxHp, monster.AttackPower, kills, isBoss, monster.Id == _selectedMonsterId, icon, HandleMonsterSelected);
                _activeRows.Add(row);
            }

            RefreshTargetHeader();
        }

        private void HandleMonsterSelected(int monsterId)
        {
            _selectedMonsterId = monsterId;
            RefreshMonsterRoster();
            RefreshFeatureImage();
            RefreshTargetHeader();
            SetStatus(string.Empty);
        }

        // The big image is the location by default and the selected monster
        // once you pick one - the "click a monster and its picture replaces
        // the location picture" behaviour.
        private void RefreshFeatureImage()
        {
            if (FeatureImage == null) return;

            Sprite sprite = null;
            string caption;

            if (_selectedMonsterId > 0 && Registry != null && Registry.TryGetMonsterSprite(_selectedMonsterId, out Sprite monsterSprite))
            {
                sprite = monsterSprite;
                caption = string.Empty;
            }
            else if (_selectedMonsterId > 0)
            {
                caption = ClientContentRegistry.TryGetMonster(_selectedMonsterId, out MonsterEntry monster) ? monster.Name : string.Empty;
            }
            else if (Registry != null && Registry.TryGetRegionBackdrop(CurrentRegionId, out Sprite backdrop))
            {
                sprite = backdrop;
                caption = string.Empty;
            }
            else
            {
                caption = ClientContentRegistry.GetRegionName(CurrentRegionId);
            }

            FeatureImage.sprite = sprite;
            FeatureImage.color = sprite != null ? Color.white : new Color(0.15f, 0.18f, 0.15f, 1f);

            if (FeatureCaptionText != null)
            {
                // Only shown when there is no art for what is being
                // displayed - most locations and every region above 2 have
                // none yet.
                FeatureCaptionText.text = caption;
                FeatureCaptionText.gameObject.SetActive(!string.IsNullOrEmpty(caption));
            }
        }

        private void RefreshTargetHeader()
        {
            bool hasTarget = _selectedMonsterId > 0 && ClientContentRegistry.TryGetMonster(_selectedMonsterId, out _);

            if (TargetHealthRoot != null)
            {
                TargetHealthRoot.SetActive(hasTarget);
            }

            if (!hasTarget) return;

            ClientContentRegistry.TryGetMonster(_selectedMonsterId, out MonsterEntry monster);
            if (TargetNameText != null)
            {
                TargetNameText.text = monster.Name;
            }

            RefreshLiveTargetHealth();
        }

        // Live HP only applies while the server is actually simulating this
        // monster - VisualCurrentMonsterId is what the character is really
        // fighting right now, which may not be the one being browsed.
        private void RefreshLiveTargetHealth()
        {
            if (_selectedMonsterId <= 0 || SyncProxy == null) return;
            if (!ClientContentRegistry.TryGetMonster(_selectedMonsterId, out MonsterEntry monster)) return;

            bool isEngaged = SyncProxy.VisualCurrentMonsterId == _selectedMonsterId;
            float currentHp = isEngaged ? Mathf.Max(0f, SyncProxy.VisualMonsterHp) : monster.MaxHp;
            float fraction = monster.MaxHp > 0 ? Mathf.Clamp01(currentHp / monster.MaxHp) : 0f;

            if (TargetHealthText != null)
            {
                TargetHealthText.text = isEngaged
                    ? UiCombatMonsterRow.FormatCompact((long)currentHp) + " / " + UiCombatMonsterRow.FormatCompact(monster.MaxHp) + " HP"
                    : UiCombatMonsterRow.FormatCompact(monster.MaxHp) + " HP   -   not engaged";
            }

            if (TargetHealthFill != null)
            {
                Vector2 anchorMax = TargetHealthFill.anchorMax;
                anchorMax.x = fraction;
                TargetHealthFill.anchorMax = anchorMax;
            }
        }

        // ------------------------------------------------------------
        // Character
        // ------------------------------------------------------------
        private void RefreshCharacterStats()
        {
            if (SyncProxy == null) return;

            if (CharacterStatsText != null)
            {
                CharacterStatsText.text =
                    "Level " + SyncProxy.VisualPlayerLevel +
                    "    STR " + SyncProxy.VisualSTR +
                    "    DEX " + SyncProxy.VisualDEX +
                    "    CON " + SyncProxy.VisualCON +
                    "    LCK " + SyncProxy.VisualLCK +
                    "\nAccuracy " + SyncProxy.VisualPlayerAccuracyRating +
                    "    Armour " + SyncProxy.VisualPlayerArmorRating +
                    "    Block " + SyncProxy.VisualPlayerBlockStrengthPct.ToString("0.#") + "%";
            }

            if (CharacterHealthText != null)
            {
                CharacterHealthText.text = "Your HP: " + Mathf.RoundToInt(SyncProxy.VisualPlayerHp);
            }
        }

        private void RefreshCharacterSlots()
        {
            IReadOnlyList<BreedingRosterEntryData> entries = BreedingRosterCache.Entries;

            for (int i = 0; i < CharacterSlotTexts.Length; i++)
            {
                bool hasEntry = i < entries.Count;

                if (CharacterSlotTexts[i] != null)
                {
                    CharacterSlotTexts[i].text = hasEntry
                        ? "Lv. " + entries[i].Level + "\nGen " + entries[i].GenerationIndex
                        : "empty";
                }

                if (CharacterSlotButtons[i] != null)
                {
                    CharacterSlotButtons[i].interactable = hasEntry;
                }

                if (CharacterSlotSelectedHighlights[i] != null)
                {
                    CharacterSlotSelectedHighlights[i].SetActive(i == _selectedCharacterSlotIndex);
                }
            }
        }

        private void HandleCharacterSlotClicked(int index)
        {
            _selectedCharacterSlotIndex = index;
            RefreshCharacterSlots();
            SetStatus(string.Empty);
        }

        // ------------------------------------------------------------
        // Consumables
        // ------------------------------------------------------------
        private void PopulateConsumableDropdowns()
        {
            FillDropdown(FoodDropdown, ClientContentRegistry.Foods);
            FillDropdown(PotionDropdown, ClientContentRegistry.Potions);
        }

        private void FillDropdown(TMP_Dropdown dropdown, IReadOnlyList<ItemEntry> items)
        {
            if (dropdown == null) return;

            _dropdownOptionBuffer.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                _dropdownOptionBuffer.Add(ClientContentRegistry.GetItemDisplayName(items[i]));
            }

            if (_dropdownOptionBuffer.Count == 0)
            {
                _dropdownOptionBuffer.Add("none available");
            }

            dropdown.ClearOptions();
            dropdown.AddOptions(_dropdownOptionBuffer);
        }

        private void HandleUseFoodClicked()
        {
            UseConsumable(FoodDropdown, ClientContentRegistry.Foods, "food");
        }

        private void HandleUsePotionClicked()
        {
            UseConsumable(PotionDropdown, ClientContentRegistry.Potions, "potion");
        }

        private void UseConsumable(TMP_Dropdown dropdown, IReadOnlyList<ItemEntry> items, string kindLabel)
        {
            if (dropdown == null || NetworkClient == null) return;

            int index = dropdown.value;
            if (index < 0 || index >= items.Count)
            {
                SetStatus("No " + kindLabel + " selected.");
                return;
            }

            // CommandType.ConsumeConsumableAsset = 45. Slot target is unused
            // by the food/potion path (ConsumableEngine.TryApplyConsumable
            // writes the buff slot the item's own BaseId marker implies), so
            // 0 is correct rather than arbitrary.
            NetworkClient.SendConsumableCommandZeroAlloc(45, (uint)items[index].Id, 0u);
            SetStatus("Used " + ClientContentRegistry.GetItemDisplayName(items[index]) + ".");
        }

        private void RefreshActiveBuffs()
        {
            if (ActiveBuffText == null || SyncProxy == null) return;

            string offensive = DescribeBuff(SyncProxy.VisualActiveOffensivePotionId, SyncProxy.VisualOffensivePotionDurationMs);
            string defensive = DescribeBuff(SyncProxy.VisualActiveDefensivePotionId, SyncProxy.VisualDefensivePotionDurationMs);

            if (offensive == null && defensive == null)
            {
                ActiveBuffText.text = "No active potion.";
                return;
            }

            ActiveBuffText.text = offensive != null && defensive != null
                ? offensive + "   |   " + defensive
                : offensive ?? defensive;
        }

        private static string DescribeBuff(int itemId, int remainingMs)
        {
            if (itemId <= 0 || remainingMs <= 0) return null;
            if (!ClientContentRegistry.TryGetItemById(itemId, out ItemEntry item)) return null;

            int seconds = remainingMs / 1000;
            return ClientContentRegistry.GetItemDisplayName(item) + " (" + (seconds / 60) + "m " + (seconds % 60) + "s)";
        }

        // ------------------------------------------------------------
        // Session tally
        // ------------------------------------------------------------
        private void RefreshSessionTally()
        {
            CombatSessionTracker tracker = CombatSessionTracker.Instance;

            if (SessionSummaryText != null)
            {
                SessionSummaryText.text = tracker == null || !tracker.HasSession
                    ? "Send a character out to start tracking this session."
                    : tracker.TotalKills + " kills   -   " + tracker.TotalItemsDropped + " items   -   "
                        + UiCombatMonsterRow.FormatCompact(tracker.GoldGained) + " gold   -   "
                        + UiCombatMonsterRow.FormatCompact(tracker.XpGained) + " XP";
            }

            if (SessionKillListText == null) return;

            if (tracker == null || (tracker.Kills.Count == 0 && tracker.Drops.Count == 0))
            {
                SessionKillListText.text = string.Empty;
                return;
            }

            // Rebuilt only when the tracker reports a change (a kill tally
            // refresh, or an inbound loot packet), never per frame - so the
            // StringBuilder here is off the hot path entirely.
            _sessionTextBuilder.Clear();

            IReadOnlyList<SessionDropEntry> drops = tracker.Drops;
            if (drops.Count > 0)
            {
                _sessionTextBuilder.Append("Loot:  ");
                for (int i = 0; i < drops.Count && i < MaxSessionDropLines; i++)
                {
                    if (i > 0) _sessionTextBuilder.Append(",  ");
                    _sessionTextBuilder.Append(drops[i].ItemName).Append(" x").Append(drops[i].Quantity);

                    // Equipment carries a rarity roll worth calling out - a
                    // Legendary drop should not read the same as a lump of
                    // ore. Materials and scrap report tier 0 and are skipped.
                    if (drops[i].BestQualityTier > 1)
                    {
                        _sessionTextBuilder.Append(" [").Append(DescribeRarity(drops[i].BestQualityTier)).Append(']');
                    }
                }

                if (drops.Count > MaxSessionDropLines)
                {
                    _sessionTextBuilder.Append(",  +").Append(drops.Count - MaxSessionDropLines).Append(" more");
                }

                _sessionTextBuilder.Append('\n');
            }

            IReadOnlyList<SessionKillEntry> kills = tracker.Kills;
            for (int i = 0; i < kills.Count && i < MaxSessionKillLines; i++)
            {
                _sessionTextBuilder.Append(kills[i].MonsterName).Append("  x").Append(kills[i].Kills).Append('\n');
            }

            SessionKillListText.SetText(_sessionTextBuilder);
        }

        // Mirrors the server's RarityTier constants (1 Normal through 14
        // Transcendent) - these are the names CombatLootEngine's own weight
        // table is written against.
        private static string DescribeRarity(byte tier)
        {
            switch (tier)
            {
                case 2: return "Common";
                case 3: return "Uncommon";
                case 4: return "Rare";
                case 5: return "Ultra Rare";
                case 6: return "Epic";
                case 7: return "Legendary";
                case 8: return "Mythic";
                case 9: return "Relic";
                case 10: return "Ancient";
                case 11: return "Divine";
                case 12: return "Demonic";
                case 13: return "Godly";
                case 14: return "Transcendent";
                default: return "Normal";
            }
        }

        // ------------------------------------------------------------
        // Deploy
        // ------------------------------------------------------------
        private void HandleDeployClicked()
        {
            if (_selectedMonsterId <= 0)
            {
                SetStatus("Pick a monster to fight first.");
                return;
            }

            if (!TryResolveSelectedCharacterGuid(out System.Guid characterGuid))
            {
                SetStatus("Pick which character to send.");
                return;
            }

            if (NetworkClient == null)
            {
                SetStatus("Not connected.");
                return;
            }

            // The server accepts a monster id directly as an ActiveActivityId
            // (ClientCommandValidator.ValidateChangeActivityRequest), so
            // "fight monster X" already IS ChangeActivity(X) - no dedicated
            // combat command exists or is needed.
            NetworkClient.SendChangeActivityCommandZeroAlloc(_selectedMonsterId, characterGuid);
            CombatSessionTracker.Instance?.BeginSession();

            ClientContentRegistry.TryGetMonster(_selectedMonsterId, out MonsterEntry monster);
            SetStatus("Fighting " + (monster != null ? monster.Name : "target") + ".");

            RefreshSessionTally();
        }

        private bool TryResolveSelectedCharacterGuid(out System.Guid characterGuid)
        {
            characterGuid = System.Guid.Empty;

            IReadOnlyList<BreedingRosterEntryData> entries = BreedingRosterCache.Entries;
            if (_selectedCharacterSlotIndex < 0 || _selectedCharacterSlotIndex >= entries.Count)
            {
                return false;
            }

            return System.Guid.TryParse(entries[_selectedCharacterSlotIndex].CharacterId, out characterGuid);
        }

        // Modul: the old Deploy button switched screens unconditionally,
        // even when it had silently discarded both selections and dispatched
        // nothing. Now the screen switch is a separate, explicitly-labelled
        // button, so "fight" and "go watch the fight" are not the same tap.
        public void OpenCharacterScreen()
        {
            ScreenTabGroup?.ShowIndex(CharacterScreenIndex);
        }

        private void SetStatus(string message)
        {
            if (StatusText != null)
            {
                StatusText.text = message;
            }
        }
    }
}
