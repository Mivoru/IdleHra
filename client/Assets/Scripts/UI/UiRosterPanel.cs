using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: roster. The screen where a player sees all three characters at
    // once and gives each one a job.
    //
    // Until now the game could only ever show one character. The server has
    // simulated three since the multi-slot pass, and CharacterRecord has
    // carried a per-character ActiveActivityId far longer than that, but there
    // was no UI able to assign anything to a character other than the main one -
    // so slots 2 and 3 were unreachable no matter how far the village had been
    // built.
    //
    // Two rules are enforced server-side and only mirrored here, never
    // duplicated as client authority: a slot must be unlocked by the Town Hall
    // (3 for the second, 5 for the third), and no two characters may run the
    // SAME activity. The dropdown greys out activities another character has
    // already taken so the player finds out before pressing the button rather
    // than through a rejection, but the server still refuses the command if the
    // client is wrong or stale.
    public class UiRosterPanel : MonoBehaviour
    {
        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;

        public const int SlotCount = 3;

        // Mirrors CharacterSlotEngine.Slot2TownHallRequirement /
        // Slot3TownHallRequirement. Display only - the server re-checks.
        private static readonly int[] SlotTownHallRequirement = { 0, 3, 5 };

        [Header("Per-slot rows (index order: 1, 2, 3)")]
        public TMP_Text[] SlotHeaderTexts = new TMP_Text[SlotCount];
        public TMP_Text[] SlotStatusTexts = new TMP_Text[SlotCount];
        public TMP_Dropdown[] SlotActivityDropdowns = new TMP_Dropdown[SlotCount];
        public Button[] SlotAssignButtons = new Button[SlotCount];
        public Button[] SlotStopButtons = new Button[SlotCount];
        public GameObject[] SlotLockedOverlays = new GameObject[SlotCount];

        // Modul: roster loadouts. What each character is wearing. Fed by
        // PlayerInventoryCache rather than the 10Hz wire, which deliberately
        // carries only the ACTIVE character's gear - so before this the Roster
        // could say what a character was doing but not what it was holding.
        public TMP_Text[] SlotGearTexts = new TMP_Text[SlotCount];

        [Header("Status")]
        public TMP_Text SummaryText;

        // Index-aligned with every dropdown's option list. Built once from the
        // content registry and reused, so opening the screen does not rebuild
        // the whole activity catalogue.
        private readonly List<long> _offeredActivityIds = new List<long>();
        private readonly List<string> _offeredActivityLabels = new List<string>();
        private readonly StringBuilder _labelBuilder = new StringBuilder(96);

        private bool _optionsBuilt;

        private void Awake()
        {
            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                int capturedSlot = slotIndex;

                if (SlotAssignButtons != null && slotIndex < SlotAssignButtons.Length && SlotAssignButtons[slotIndex] != null)
                {
                    SlotAssignButtons[slotIndex].onClick.AddListener(() => HandleAssignClicked(capturedSlot));
                }

                if (SlotStopButtons != null && slotIndex < SlotStopButtons.Length && SlotStopButtons[slotIndex] != null)
                {
                    SlotStopButtons[slotIndex].onClick.AddListener(() => HandleStopClicked(capturedSlot));
                }
            }
        }

        private void OnEnable()
        {
            BuildActivityOptionsOnce();
            // Modul: roster loadouts. Gear comes from the REST snapshot, not
            // the tick stream, so it has to be pulled when the screen opens.
            // Cheap and on-demand - the cache no-ops a request already in
            // flight.
            PlayerInventoryCache.RequestSnapshot();
            Refresh();
        }

        private void Update()
        {
            // The roster is a small, fixed set of rows and the underlying data
            // is a handful of value-type proxy fields, so a per-frame refresh
            // costs a few comparisons and no allocation. Event-driven would
            // need a change signal the proxy does not raise for activity ids.
            Refresh();
        }

        // Every activity a character can be given: the five regions' monsters
        // and every gathering node. Built from ClientContentRegistry so the
        // list can never drift from what the server will accept.
        private void BuildActivityOptionsOnce()
        {
            if (_optionsBuilt) return;
            _optionsBuilt = true;

            _offeredActivityIds.Clear();
            _offeredActivityLabels.Clear();

            // Idle is always first, so index 0 is a valid "do nothing" choice
            // and the dropdown is never empty even before content loads.
            _offeredActivityIds.Add(0L);
            _offeredActivityLabels.Add("Idle");

            for (int regionId = 1; regionId <= ClientContentRegistry.CanonicalRegionCount; regionId++)
            {
                IReadOnlyList<MonsterEntry> monsters = ClientContentRegistry.GetMonstersInRegion(regionId);
                if (monsters == null) continue;

                for (int i = 0; i < monsters.Count; i++)
                {
                    MonsterEntry monster = monsters[i];

                    // Modul: roster. Monster ids and gathering node ids share
                    // one activity space and genuinely collide - the whole of
                    // Region 3 (ids 101-105) sits on top of Woodcutting nodes
                    // 101-105. The server resolves an activity id by checking
                    // TryGetGatheringNode FIRST, so sending 101 starts
                    // woodcutting, never the Desert Crab.
                    //
                    // Offering those monsters here would put a "Fight: Desert
                    // Crab" entry in the list that silently starts chopping
                    // wood. They are withheld until the id spaces are
                    // separated; a menu that omits an option is honest, one
                    // that does something else entirely is not.
                    if (ClientContentRegistry.TryGetGatheringNode(monster.Id, out _))
                    {
                        continue;
                    }

                    _labelBuilder.Clear();
                    _labelBuilder.Append("Fight: ");
                    _labelBuilder.Append(monster.Name);
                    _labelBuilder.Append("  (");
                    _labelBuilder.Append(ClientContentRegistry.GetRegionName(regionId));
                    _labelBuilder.Append(')');

                    _offeredActivityIds.Add(monster.Id);
                    _offeredActivityLabels.Add(_labelBuilder.ToString());
                }
            }

            IReadOnlyList<GatheringNodeEntry> nodes = ClientContentRegistry.GatheringNodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                GatheringNodeEntry node = nodes[i];
                _labelBuilder.Clear();
                _labelBuilder.Append(DescribeProfession(node.ProfessionType));
                _labelBuilder.Append(" node ");
                _labelBuilder.Append(node.ActivityId);

                _offeredActivityIds.Add(node.ActivityId);
                _offeredActivityLabels.Add(_labelBuilder.ToString());
            }

            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                if (SlotActivityDropdowns == null || slotIndex >= SlotActivityDropdowns.Length) break;
                TMP_Dropdown dropdown = SlotActivityDropdowns[slotIndex];
                if (dropdown == null) continue;

                dropdown.ClearOptions();
                dropdown.AddOptions(_offeredActivityLabels);
                dropdown.value = 0;
                dropdown.RefreshShownValue();
            }
        }

        private static string DescribeProfession(int professionType)
        {
            switch (professionType)
            {
                case 0: return "Woodcutting";
                case 1: return "Mining";
                case 2: return "Fishing";
                default: return "Herbalism";
            }
        }

        private void Refresh()
        {
            if (SyncProxy == null) return;

            int townHallLevel = SyncProxy.VisualTownHallLevel;
            int workingCount = 0;

            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                bool unlocked = townHallLevel >= SlotTownHallRequirement[slotIndex];
                System.Guid characterId = GetSlotCharacterId(slotIndex);
                bool occupied = characterId != System.Guid.Empty;
                long activityId = GetSlotActivityId(slotIndex);
                byte haltReason = GetSlotHaltReason(slotIndex);

                if (unlocked && occupied && activityId > 0) workingCount++;

                if (SlotLockedOverlays != null && slotIndex < SlotLockedOverlays.Length && SlotLockedOverlays[slotIndex] != null)
                {
                    SlotLockedOverlays[slotIndex].SetActive(!unlocked);
                }

                if (SlotHeaderTexts != null && slotIndex < SlotHeaderTexts.Length && SlotHeaderTexts[slotIndex] != null)
                {
                    _labelBuilder.Clear();
                    _labelBuilder.Append("CHARACTER ");
                    _labelBuilder.Append(slotIndex + 1);
                    if (!unlocked)
                    {
                        _labelBuilder.Append("  -  LOCKED UNTIL TOWN HALL ");
                        _labelBuilder.Append(SlotTownHallRequirement[slotIndex]);
                    }
                    SlotHeaderTexts[slotIndex].text = _labelBuilder.ToString();
                }

                if (SlotStatusTexts != null && slotIndex < SlotStatusTexts.Length && SlotStatusTexts[slotIndex] != null)
                {
                    SlotStatusTexts[slotIndex].text = DescribeSlot(unlocked, occupied, activityId, haltReason);
                }

                if (SlotGearTexts != null && slotIndex < SlotGearTexts.Length && SlotGearTexts[slotIndex] != null)
                {
                    SlotGearTexts[slotIndex].text = DescribeSlotGear(slotIndex, unlocked, occupied);
                }

                bool interactable = unlocked && occupied;
                if (SlotActivityDropdowns != null && slotIndex < SlotActivityDropdowns.Length && SlotActivityDropdowns[slotIndex] != null)
                {
                    SlotActivityDropdowns[slotIndex].interactable = interactable;
                }
                if (SlotAssignButtons != null && slotIndex < SlotAssignButtons.Length && SlotAssignButtons[slotIndex] != null)
                {
                    SlotAssignButtons[slotIndex].interactable = interactable;
                }
                if (SlotStopButtons != null && slotIndex < SlotStopButtons.Length && SlotStopButtons[slotIndex] != null)
                {
                    SlotStopButtons[slotIndex].interactable = interactable && activityId > 0;
                }
            }

            if (SummaryText != null)
            {
                _labelBuilder.Clear();
                _labelBuilder.Append(workingCount);
                _labelBuilder.Append(workingCount == 1 ? " character working." : " characters working.");
                if (townHallLevel < SlotTownHallRequirement[SlotCount - 1])
                {
                    _labelBuilder.Append("  Upgrade the Town Hall to field more at once.");
                }
                SummaryText.text = _labelBuilder.ToString();
            }
        }

        // Modul: roster loadouts. One line naming what this character wears.
        //
        // Reads PlayerInventoryCache, whose entries now carry
        // EquippedByCharacterSlot - the account-wide "IsEquipped" flag alone
        // could say an item was worn but never by WHICH character, which is
        // exactly the question this row asks.
        private string DescribeSlotGear(int slotIndex, bool unlocked, bool occupied)
        {
            if (!unlocked || !occupied) return string.Empty;

            var equipment = PlayerInventoryCache.Equipment;
            if (equipment == null || equipment.Count == 0) return "Gear: (loading)";

            _labelBuilder.Clear();
            int wornCount = 0;

            for (int i = 0; i < equipment.Count; i++)
            {
                var item = equipment[i];
                if (item.EquippedByCharacterSlot != slotIndex) continue;

                if (wornCount > 0) _labelBuilder.Append(", ");
                _labelBuilder.Append(ClientContentRegistry.GetItemDisplayName(item.BaseItemId));
                wornCount++;
            }

            if (wornCount == 0) return "Gear: nothing equipped.";

            // Prefixed with the count so a partially-kitted character reads as
            // deliberately partial rather than as a truncated list.
            string wornNames = _labelBuilder.ToString();
            _labelBuilder.Clear();
            _labelBuilder.Append("Gear (");
            _labelBuilder.Append(wornCount);
            _labelBuilder.Append("/7): ");
            _labelBuilder.Append(wornNames);
            return _labelBuilder.ToString();
        }

        private string DescribeSlot(bool unlocked, bool occupied, long activityId, byte haltReason)
        {
            if (!unlocked) return "Build the Town Hall higher to unlock this slot.";
            if (!occupied) return "No character in this slot.";

            if (haltReason != ActivityHaltReason.None)
            {
                switch (haltReason)
                {
                    case ActivityHaltReason.OutOfFood: return "Stopped - the larder is empty.";
                    case ActivityHaltReason.Died: return "Stopped - killed and respawned.";
                    case ActivityHaltReason.InventoryFull: return "Backpack full - finds are being discarded.";
                    case ActivityHaltReason.NoEligibleCharacter: return "No character free to send out.";
                }
            }

            if (activityId <= 0) return "Idle. Pick something for them to do.";

            _labelBuilder.Clear();
            _labelBuilder.Append("Working: ");
            _labelBuilder.Append(DescribeActivity(activityId));
            return _labelBuilder.ToString();
        }

        // Gathering is checked FIRST, matching ProcessSubTick's own resolution
        // order on the server. Getting this backwards made the roster report a
        // character on Woodcutting node 101 as "Working: Desert Crab", because
        // monster 101 and woodcutting node 101 are the same activity id.
        private static string DescribeActivity(long activityId)
        {
            if (ClientContentRegistry.TryGetGatheringNode(activityId, out GatheringNodeEntry node))
            {
                return DescribeProfession(node.ProfessionType) + " node " + node.ActivityId;
            }
            if (ClientContentRegistry.TryGetMonster((int)activityId, out MonsterEntry monster))
            {
                return monster.Name;
            }
            return "activity " + activityId;
        }

        private System.Guid GetSlotCharacterId(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: return SyncProxy.VisualSlot1CharacterId;
                case 1: return SyncProxy.VisualSlot2CharacterId;
                default: return SyncProxy.VisualSlot3CharacterId;
            }
        }

        // Slot 1 reads the main ActiveActivityId rather than a mirrored copy -
        // the wire deliberately carries it once so the two cannot disagree.
        private long GetSlotActivityId(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: return SyncProxy.VisualActiveActivityId;
                case 1: return SyncProxy.VisualSlot2ActivityId;
                default: return SyncProxy.VisualSlot3ActivityId;
            }
        }

        private byte GetSlotHaltReason(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: return SyncProxy.VisualActivityHaltReason;
                case 1: return SyncProxy.VisualSlot2ActivityHaltReason;
                default: return SyncProxy.VisualSlot3ActivityHaltReason;
            }
        }

        private void HandleAssignClicked(int slotIndex)
        {
            if (NetworkClient == null || SyncProxy == null) return;
            if (SlotActivityDropdowns == null || slotIndex >= SlotActivityDropdowns.Length) return;

            TMP_Dropdown dropdown = SlotActivityDropdowns[slotIndex];
            if (dropdown == null) return;

            int selection = dropdown.value;
            if (selection < 0 || selection >= _offeredActivityIds.Count) return;

            long activityId = _offeredActivityIds[selection];
            System.Guid characterId = GetSlotCharacterId(slotIndex);
            if (characterId == System.Guid.Empty) return;

            // Mirror of the server's occupancy mutex, reported before the
            // command rather than after the rejection. The server still decides.
            if (activityId > 0 && IsActivityTakenByAnotherSlot(slotIndex, activityId))
            {
                if (SlotStatusTexts != null && slotIndex < SlotStatusTexts.Length && SlotStatusTexts[slotIndex] != null)
                {
                    SlotStatusTexts[slotIndex].text = "Another character is already doing that. Two characters cannot share one activity.";
                }
                return;
            }

            NetworkClient.SendAssignCharacterActivityCommandZeroAlloc(characterId, activityId);
        }

        private void HandleStopClicked(int slotIndex)
        {
            if (NetworkClient == null || SyncProxy == null) return;

            System.Guid characterId = GetSlotCharacterId(slotIndex);
            if (characterId == System.Guid.Empty) return;

            NetworkClient.SendAssignCharacterActivityCommandZeroAlloc(characterId, 0L);
        }

        private bool IsActivityTakenByAnotherSlot(int requestingSlotIndex, long activityId)
        {
            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            {
                if (slotIndex == requestingSlotIndex) continue;
                if (GetSlotActivityId(slotIndex) == activityId) return true;
            }
            return false;
        }
    }
}
