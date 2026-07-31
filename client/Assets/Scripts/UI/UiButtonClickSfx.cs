using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: audio pipeline. Plays the UI click effect for whichever Button
    // sits on the same GameObject.
    //
    // A component rather than a listener added inside MainSceneBuilder.
    // CreateButton, because the builder runs in the EDITOR: a lambda added
    // there with onClick.AddListener is a runtime-only subscription that is
    // never serialised into the scene, so it would be gone by the time anyone
    // pressed the button. Persistent listeners (UnityEventTools.
    // AddPersistentListener) do serialise but can only target a real method on
    // a real object, which is exactly what this component provides - and
    // attaching the component is cheaper than registering ~200 persistent
    // listeners by hand.
    //
    // Subscribing in Awake rather than OnEnable: buttons live inside windows
    // that are toggled off and on constantly, and OnEnable would stack a
    // duplicate subscription every time one was reopened.
    [RequireComponent(typeof(Button))]
    public class UiButtonClickSfx : MonoBehaviour
    {
        private void Awake()
        {
            Button button = GetComponent<Button>();
            if (button == null) return;

            button.onClick.AddListener(HandleClick);
        }

        private void HandleClick()
        {
            // GameAudioDirector.Play is null-safe on every axis: no director in
            // the scene, no clip authored, volume at zero. Nothing here needs a
            // guard of its own.
            GameAudioDirector.Play(GameSfx.UiButtonClick);
        }
    }
}
