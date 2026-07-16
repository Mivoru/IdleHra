using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: drives the pure-logic TutorialStateMachine against real
    // VisualSyncProxy signals and Unity-side concerns (highlight toggling,
    // instruction text, PlayerPrefs persistence). UiLoginWindow calls
    // BeginTutorial() once, after confirming the account is fresh and the
    // tutorial has not already been completed on this device; every other
    // gated UI element queries StateMachine.IsInteractionAllowed directly
    // (see UiTutorialInteractionGate) rather than going through this
    // controller, so this class owns state, not every button's enabled
    // flag.
    public class UiTutorialController : MonoBehaviour
    {
        private const string TutorialCompletedPrefsKey = "folkidle_tutorial_completed";

        public VisualSyncProxy SyncProxy;

        [Header("Call-to-Action Highlights")]
        public UiTutorialHighlight InventoryHighlight;
        public UiTutorialHighlight ForgeHighlight;
        public UiTutorialHighlight ArenaHighlight;

        [Header("Overlay")]
        public GameObject TutorialOverlayRoot;
        public TMP_Text InstructionLabel;
        public Button SkipButton;

        public TutorialStateMachine StateMachine { get; } = new TutorialStateMachine();

        // Modul: combat-win detection is a heuristic, not a direct server
        // signal - there is no dedicated "you won" packet field. A win is
        // inferred from OnCombatInstanceChanged firing (a new monster
        // instance started, i.e. the previous one resolved) while the
        // previously observed monster HP had reached zero. Tracked here
        // rather than in VisualSyncProxy so the heuristic and its
        // documented limitation stay local to the one feature that needs
        // it.
        private float _previousObservedMonsterHp;
        private bool _hasObservedMonsterHp;

        // Modul: crafted-item detection compares VisualTotalItemsCraftedCount
        // frame to frame - the only crafting completion signal exposed on
        // VisualSyncProxy is a running total, not an edge-triggered event.
        private uint _previousTotalItemsCrafted;
        private bool _hasObservedCraftedCount;

        private void Awake()
        {
            StateMachine.OnStepChanged += HandleStepChanged;
            if (SkipButton != null) SkipButton.onClick.AddListener(SkipTutorial);
            if (TutorialOverlayRoot != null) TutorialOverlayRoot.SetActive(false);
        }

        private void OnDestroy()
        {
            StateMachine.OnStepChanged -= HandleStepChanged;
        }

        // Modul: called by UiLoginWindow once per fresh-account login, never
        // by this controller itself - login is the only place that knows
        // both "is this account fresh" and "has the tutorial already run on
        // this device" (the PlayerPrefs check below is a second, local
        // guard against re-arming after SkipTutorial/Completed on THIS
        // install, independent of the server-side freshness flag).
        public void BeginTutorial()
        {
            if (PlayerPrefs.GetInt(TutorialCompletedPrefsKey, 0) == 1) return;
            StateMachine.Begin();
        }

        public void NotifyItemLooted() => StateMachine.NotifyItemLooted();
        public void NotifyItemCrafted() => StateMachine.NotifyItemCrafted();
        public void NotifyCombatWon() => StateMachine.NotifyCombatWon();

        public void SkipTutorial()
        {
            StateMachine.SkipTutorial();
        }

        private void Update()
        {
            if (SyncProxy == null || !StateMachine.IsActive) return;

            // Crafted-item edge detection.
            uint totalCrafted = SyncProxy.VisualTotalItemsCraftedCount;
            if (!_hasObservedCraftedCount)
            {
                _previousTotalItemsCrafted = totalCrafted;
                _hasObservedCraftedCount = true;
            }
            else if (totalCrafted > _previousTotalItemsCrafted)
            {
                _previousTotalItemsCrafted = totalCrafted;
                StateMachine.NotifyItemCrafted();
            }

            // Combat-win edge detection: track the last observed monster HP
            // every frame, so HandleCombatInstanceChanged (fired by
            // VisualSyncProxy at packet-arrival time, not from Update) can
            // tell "the previous instance ended at zero HP" apart from "a
            // fresh monster's very first observation."
            _previousObservedMonsterHp = SyncProxy.VisualMonsterHp;
            _hasObservedMonsterHp = true;
        }

        private void HandleCombatInstanceChanged()
        {
            if (StateMachine.CurrentStep != TutorialStep.WinFirstCombat) return;
            if (_hasObservedMonsterHp && _previousObservedMonsterHp <= 0f)
            {
                StateMachine.NotifyCombatWon();
            }
        }

        private void OnEnable()
        {
            if (SyncProxy != null) SyncProxy.OnCombatInstanceChanged += HandleCombatInstanceChanged;
        }

        private void OnDisable()
        {
            if (SyncProxy != null) SyncProxy.OnCombatInstanceChanged -= HandleCombatInstanceChanged;
        }

        private void HandleStepChanged(TutorialStep step)
        {
            SetHighlightActive(InventoryHighlight, step == TutorialStep.LootFirstItem);
            SetHighlightActive(ForgeHighlight, step == TutorialStep.CraftFirstItem);
            SetHighlightActive(ArenaHighlight, step == TutorialStep.WinFirstCombat);

            bool overlayVisible = StateMachine.IsActive;
            if (TutorialOverlayRoot != null) TutorialOverlayRoot.SetActive(overlayVisible);

            if (InstructionLabel != null)
            {
                InstructionLabel.text = step switch
                {
                    TutorialStep.LootFirstItem => "Open your inventory and loot your first item",
                    TutorialStep.CraftFirstItem => "Craft your first item at the Forge",
                    TutorialStep.WinFirstCombat => "Win your first combat in the Arena",
                    _ => string.Empty
                };
            }

            if (step == TutorialStep.Completed)
            {
                PlayerPrefs.SetInt(TutorialCompletedPrefsKey, 1);
                PlayerPrefs.Save();
            }
        }

        private static void SetHighlightActive(UiTutorialHighlight highlight, bool active)
        {
            if (highlight == null) return;
            highlight.gameObject.SetActive(active);
        }
    }
}
