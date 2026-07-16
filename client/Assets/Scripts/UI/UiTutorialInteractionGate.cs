using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: place on any Button that must respect tutorial step-blocking
    // (see NEXT_STEPS_BACKLOG-tracked onboarding gap). Queries
    // TutorialStateMachine.IsInteractionAllowed every frame rather than
    // subscribing to OnStepChanged, so a gate added to a button that is
    // instantiated/enabled after a step transition already happened (e.g.
    // a pooled row) still resolves correctly on its own first Update
    // instead of waiting for the next transition. Caches the last applied
    // value and only writes GatedButton.interactable on an actual change,
    // matching this codebase's established "only mutate on change" UI
    // convention (see VisualSyncProxy's ApplyVillagePacketState/
    // ApplyGuildPacketState).
    public class UiTutorialInteractionGate : MonoBehaviour
    {
        public UiTutorialController Controller;
        public TutorialUiElement Element;
        public Button GatedButton;

        private bool _hasAppliedValue;
        private bool _lastAppliedValue;

        private void Update()
        {
            if (GatedButton == null) return;

            bool allowed = Controller == null || Controller.StateMachine == null || Controller.StateMachine.IsInteractionAllowed(Element);

            if (_hasAppliedValue && allowed == _lastAppliedValue) return;

            GatedButton.interactable = allowed;
            _lastAppliedValue = allowed;
            _hasAppliedValue = true;
        }
    }
}
