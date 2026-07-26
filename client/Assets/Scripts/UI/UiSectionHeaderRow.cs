using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: Inventory and Crafting screens. A pooled, non-interactive
    // divider line inside a pooled list ("EQUIPPED", "SMELTING", and so on).
    //
    // Pooled from its own separate pool rather than reusing the entry-row
    // pool, because UIComponentPool is typed per component and a header is a
    // genuinely different shape - shorter, no icon, no quantity column. The
    // caller interleaves the two pools by calling SetAsLastSibling as it
    // emits rows, so ordering stays correct across both.
    public class UiSectionHeaderRow : MonoBehaviour
    {
        public TMP_Text TitleText;

        public void Bind(string title)
        {
            if (TitleText != null)
            {
                TitleText.text = title;
            }
        }
    }
}
