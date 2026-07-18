#nullable enable
using System;

namespace FolkIdle.Client.Engine
{
    // Modul: FTUE step ladder. Values are explicit and contiguous because
    // Completed is persisted indirectly (PlayerPrefs flag written by the UI
    // driver) and the server-side integration tests assert on the numeric
    // ordering Inactive < LootFirstItem < CraftFirstItem < WinFirstCombat
    // < Completed rather than on enum names.
    public enum TutorialStep
    {
        Inactive = 0,
        LootFirstItem = 1,
        CraftFirstItem = 2,
        WinFirstCombat = 3,
        Completed = 4
    }

    // Modul: the coarse UI surfaces the interaction gate can block. This is
    // deliberately a closed list of top-level windows, not per-button IDs -
    // the tutorial only needs to funnel the player toward one window at a
    // time, and a coarse enum keeps IsInteractionAllowed a pure function
    // the xUnit suite can exercise exhaustively.
    public enum TutorialUiElement
    {
        Inventory,
        Forge,
        Arena,
        Market,
        Guild,
        SkillTree,
        Chat,
        Settings
    }

    // Modul: pure C# FTUE state machine, shared verbatim with the server's
    // net8.0 xUnit test project via a csproj file link. It must therefore
    // never reference UnityEngine (no MonoBehaviour, no PlayerPrefs, no
    // Debug.Log) - all engine concerns (persistence, highlights, signal
    // detection) live in UiTutorialController, which merely drives this
    // class. All transitions funnel through Transition() so OnStepChanged
    // fires exactly once per state change.
    public class TutorialStateMachine
    {
        public TutorialStep CurrentStep { get; private set; } = TutorialStep.Inactive;

        // Modul: "active" means the player is inside the guided flow
        // (steps 1-3). Inactive and Completed are both fully unrestricted.
        public bool IsActive =>
            CurrentStep >= TutorialStep.LootFirstItem &&
            CurrentStep <= TutorialStep.WinFirstCombat;

        public event Action<TutorialStep>? OnStepChanged;

        // Modul: Begin is idempotent - it only arms the tutorial from the
        // pristine Inactive state. A re-login on an account that already
        // progressed (or completed/skipped) must never restart the flow.
        public void Begin()
        {
            if (CurrentStep != TutorialStep.Inactive) return;
            Transition(TutorialStep.LootFirstItem);
        }

        // Modul: each Notify* advances only when the machine is sitting on
        // exactly the matching step. Out-of-order signals are dropped, not
        // queued - crafting an item during LootFirstItem must not let the
        // player skip the loot step, and stale combat wins arriving after
        // completion are harmless no-ops.
        public void NotifyItemLooted()
        {
            if (CurrentStep != TutorialStep.LootFirstItem) return;
            Transition(TutorialStep.CraftFirstItem);
        }

        public void NotifyItemCrafted()
        {
            if (CurrentStep != TutorialStep.CraftFirstItem) return;
            Transition(TutorialStep.WinFirstCombat);
        }

        public void NotifyCombatWon()
        {
            if (CurrentStep != TutorialStep.WinFirstCombat) return;
            Transition(TutorialStep.Completed);
        }

        // Modul: opt-out escape hatch. Valid from any state, including
        // Inactive (a player may skip before the first step ever arms) -
        // the only no-op case is already being Completed, so the completion
        // event never fires twice.
        public void SkipTutorial()
        {
            if (CurrentStep == TutorialStep.Completed) return;
            Transition(TutorialStep.Completed);
        }

        // Modul: interaction gate rule verified by the integration tests.
        // While active, ONLY the single window the current step needs is
        // interactable; everything else is blocked. Settings is exempted
        // unconditionally - a tutorial must never trap a player away from
        // settings/logout. Outside the active range (Inactive, Completed)
        // everything is allowed.
        public bool IsInteractionAllowed(TutorialUiElement element)
        {
            if (!IsActive) return true;
            if (element == TutorialUiElement.Settings) return true;

            switch (CurrentStep)
            {
                case TutorialStep.LootFirstItem: return element == TutorialUiElement.Inventory;
                case TutorialStep.CraftFirstItem: return element == TutorialUiElement.Forge;
                case TutorialStep.WinFirstCombat: return element == TutorialUiElement.Arena;
                default: return true;
            }
        }

        private void Transition(TutorialStep next)
        {
            CurrentStep = next;
            OnStepChanged?.Invoke(next);
        }
    }
}
