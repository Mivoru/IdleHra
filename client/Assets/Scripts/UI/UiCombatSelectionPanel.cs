using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

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
    // Modul: UI audit follow-up. Deploy previously only switched screens,
    // silently discarding both selections - ClientCommandValidator.
    // ValidateChangeActivityRequest (server) already accepts any monster id
    // within ContentRegistry.Monsters' range as a valid ActiveActivityId, so
    // "start combat against monster X" already IS "ChangeActivity(monsterId)"
    // - no new command was needed, only a real dispatch call (see
    // WebSocketClient.SendChangeActivityCommandZeroAlloc, added alongside
    // this) using the dropdown's selected MonsterId and the selected
    // character slot's Guid (TargetGuid - previously never set by anything,
    // so the server's per-character-slot branch was unreachable).
    public class UiCombatSelectionPanel : MonoBehaviour
    {
        private const int RegionRowCount = 5;
        private const int CharacterSlotCount = 4;

        public UiTabGroup ScreenTabGroup;
        public int CharacterScreenIndex;
        public WebSocketClient NetworkClient;

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
                int rowIndex = i;
                if (DeployButtons[i] != null)
                {
                    DeployButtons[i].onClick.AddListener(() => HandleDeployClicked(rowIndex));
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

        private void HandleDeployClicked(int rowIndex)
        {
            if (TryResolveSelectedMonsterId(rowIndex, out long monsterId) &&
                TryResolveSelectedCharacterGuid(out System.Guid characterGuid) &&
                NetworkClient != null)
            {
                NetworkClient.SendChangeActivityCommandZeroAlloc(monsterId, characterGuid);
            }

            if (ScreenTabGroup != null)
            {
                ScreenTabGroup.ShowIndex(CharacterScreenIndex);
            }
        }

        private bool TryResolveSelectedMonsterId(int rowIndex, out long monsterId)
        {
            monsterId = 0;

            if (rowIndex < 0 || rowIndex >= MonsterDropdowns.Length || MonsterDropdowns[rowIndex] == null)
            {
                return false;
            }

            IReadOnlyList<CodexSnapshotEntryData> entries = CodexInventoryCache.Entries;
            int selectedIndex = MonsterDropdowns[rowIndex].value;
            if (selectedIndex < 0 || selectedIndex >= entries.Count)
            {
                // Either nothing discovered yet (placeholder option) or a
                // stale index from before the last refresh - either way
                // there is no real monster to deploy against.
                return false;
            }

            monsterId = entries[selectedIndex].MonsterId;
            return true;
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
    }
}
