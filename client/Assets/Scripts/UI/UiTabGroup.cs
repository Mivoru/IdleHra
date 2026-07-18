using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Full-Game UI Architecture, Part 4. Generic mutually-exclusive
    // tab group - parallel Groups[]/Buttons[] arrays, clicking Buttons[i]
    // shows Groups[i] and hides every other entry. Used for sub-tab groups
    // nested inside an individual window (Market vs Bank, Guild Roster vs
    // Logistics vs Raid vs War) where a portrait-width screen has no room
    // to show every section at once. Self-wires its own button listeners
    // in Awake, matching every other interactive component in this
    // codebase - MainSceneBuilder only assigns the array references, never
    // attaches click handlers itself.
    public class UiTabGroup : MonoBehaviour
    {
        public GameObject[] Groups;
        public Button[] Buttons;

        private void Awake()
        {
            for (int i = 0; i < Buttons.Length; i++)
            {
                int index = i;
                if (Buttons[i] != null)
                {
                    Buttons[i].onClick.AddListener(() => ShowIndex(index));
                }
            }
        }

        public void ShowIndex(int index)
        {
            for (int i = 0; i < Groups.Length; i++)
            {
                if (Groups[i] != null)
                {
                    Groups[i].SetActive(i == index);
                }
            }
        }
    }
}
