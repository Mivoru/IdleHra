using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Combat pre-fight picker (map hub "Combat" zone). Five fixed region
    // rows mirror the real Codex region-completion data (CodexRegionsCache -
    // the only existing per-location progress source; the server has no
    // separate "monster roster per region" table, so every region's
    // dropdown lists the player's full real Codex inventory
    // (CodexInventoryCache) rather than inventing a fake per-region
    // subset). Character assignment slots mirror the real breeding roster
    // (BreedingRosterCache - the only existing multi-character list) as a
    // fixed 4-slot quick-select, matching this codebase's established
    // "small fixed slot count for a small real roster" convention (see
    // UiVillageBuildingRow/UiSkillTreeWindow).
    //
    // There is no server command yet for "start combat against monster X
    // in region Y" - Deploy is therefore navigation-only: it switches the
    // active screen to the existing Character/Arena tab via ScreenTabGroup.
    // When a real "begin encounter" command exists server-side, this is the
    // place to dispatch it before switching screens.
    public class UiCombatSelectionPanel : MonoBehaviour
    {
        private const int RegionRowCount = 5;
        private const int CharacterSlotCount = 4;

        public UiTabGroup ScreenTabGroup;
        public int CharacterScreenIndex;

        [Header("Region Rows - fixed 5, real CodexRegionsCache data")]
        public TMP_Text[] RegionLabelTexts = new TMP_Text[RegionRowCount];
        public TMP_Dropdown[] MonsterDropdowns = new TMP_Dropdown[RegionRowCount];
        public Button[] DeployButtons = new Button[RegionRowCount];

        [Header("Character Assignment Slots - fixed 4, real BreedingRosterCache data")]
        public TMP_Text[] CharacterSlotTexts = new TMP_Text[CharacterSlotCount];
        public Button[] CharacterSlotButtons = new Button[CharacterSlotCount];
        public GameObject[] CharacterSlotSelectedHighlights = new GameObject[CharacterSlotCount];

        private readonly List<string> _monsterOptionsBuffer = new List<string>(32);
        private int _selectedCharacterSlotIndex = -1;

        private void Awake()
        {
            for (int i = 0; i < DeployButtons.Length; i++)
            {
                if (DeployButtons[i] != null)
                {
                    DeployButtons[i].onClick.AddListener(HandleDeployClicked);
                }
            }

            for (int i = 0; i < CharacterSlotButtons.Length; i++)
            {
                int index = i;
                if (CharacterSlotButtons[i] != null)
                {
                    CharacterSlotButtons[i].onClick.AddListener(() => HandleCharacterSlotClicked(index));
                }
            }
        }

        private void OnEnable()
        {
            CodexRegionsCache.OnCodexRegionsCacheUpdated += RefreshRegionRows;
            CodexInventoryCache.OnCodexCacheUpdated += RefreshMonsterDropdowns;
            BreedingRosterCache.OnRosterCacheUpdated += RefreshCharacterSlots;

            CodexRegionsCache.RequestSnapshot();
            CodexInventoryCache.RequestSnapshot();
            BreedingRosterCache.RequestSnapshot();

            RefreshRegionRows();
            RefreshMonsterDropdowns();
            RefreshCharacterSlots();
        }

        private void OnDisable()
        {
            CodexRegionsCache.OnCodexRegionsCacheUpdated -= RefreshRegionRows;
            CodexInventoryCache.OnCodexCacheUpdated -= RefreshMonsterDropdowns;
            BreedingRosterCache.OnRosterCacheUpdated -= RefreshCharacterSlots;
        }

        private void RefreshRegionRows()
        {
            IReadOnlyList<RegionProgressData> regions = CodexRegionsCache.Regions;

            for (int i = 0; i < RegionLabelTexts.Length; i++)
            {
                if (RegionLabelTexts[i] == null) continue;

                if (i < regions.Count)
                {
                    RegionProgressData region = regions[i];
                    RegionLabelTexts[i].text = "Region " + region.RegionId + "  (" + region.CurrentKills + " / " + region.RequiredKills + ")" +
                        (region.IsCompleted ? "  Cleared" : string.Empty);
                }
                else
                {
                    RegionLabelTexts[i].text = "Region " + (i + 1) + "  (locked)";
                }
            }
        }

        private void RefreshMonsterDropdowns()
        {
            IReadOnlyList<CodexSnapshotEntryData> entries = CodexInventoryCache.Entries;

            _monsterOptionsBuffer.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                _monsterOptionsBuffer.Add("Monster " + entries[i].MonsterId + "  Lv. " + entries[i].Level);
            }

            if (_monsterOptionsBuffer.Count == 0)
            {
                _monsterOptionsBuffer.Add("No monsters discovered yet");
            }

            for (int i = 0; i < MonsterDropdowns.Length; i++)
            {
                if (MonsterDropdowns[i] == null) continue;
                MonsterDropdowns[i].ClearOptions();
                MonsterDropdowns[i].AddOptions(_monsterOptionsBuffer);
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
                        ? "Lv. " + entries[i].Level + "  Gen " + entries[i].GenerationIndex
                        : "(empty)";
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
        }

        private void HandleDeployClicked()
        {
            if (ScreenTabGroup != null)
            {
                ScreenTabGroup.ShowIndex(CharacterScreenIndex);
            }
        }
    }
}
