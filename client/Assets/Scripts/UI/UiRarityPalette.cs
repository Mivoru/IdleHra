using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: rarity colour + glow, 2026-08-01.
    //
    // One authority for what every rarity LOOKS like, so item names, affix
    // rows, panel borders and chat announcements cannot drift apart. The
    // colours are deliberately defined once here rather than passed around as
    // literals - the same reason ClientServerConfig exists for the server
    // address.
    //
    // Two scales, because the game has two: AFFIX rarity is 1-5
    // (Common..Legendary) and ITEM rarity is the GDD's 1-14
    // (Normal..Transcendent). They share a palette family so a Legendary affix
    // and a Legendary item read as the same colour, but the item scale has to
    // interpolate across more steps.
    public static class UiRarityPalette
    {
        // Affix rarity 1-5. Chosen for contrast against the dark UI panels
        // rather than for saturation: grey/green/blue/purple/gold is the
        // convention players already read without a legend.
        private static readonly Color[] _affixColors =
        {
            new Color(0.75f, 0.75f, 0.75f, 1f),  // Common    - grey
            new Color(0.45f, 0.85f, 0.40f, 1f),  // Uncommon  - green
            new Color(0.35f, 0.65f, 1.00f, 1f),  // Rare      - blue
            new Color(0.72f, 0.45f, 0.95f, 1f),  // Epic      - purple
            new Color(1.00f, 0.78f, 0.25f, 1f)   // Legendary - gold
        };

        private static readonly string[] _affixRarityNames =
        {
            "Common", "Uncommon", "Rare", "Epic", "Legendary"
        };

        // Only Epic and Legendary glow. If everything glows, nothing reads as
        // special - and a list of twenty glowing rows is unreadable.
        public const int MinimumGlowingAffixRarity = 4;

        public static Color GetAffixRarityColor(int affixRarity)
        {
            if (affixRarity < 1) affixRarity = 1;
            if (affixRarity > _affixColors.Length) affixRarity = _affixColors.Length;
            return _affixColors[affixRarity - 1];
        }

        public static string GetAffixRarityName(int affixRarity)
        {
            if (affixRarity < 1) affixRarity = 1;
            if (affixRarity > _affixRarityNames.Length) affixRarity = _affixRarityNames.Length;
            return _affixRarityNames[affixRarity - 1];
        }

        public static bool ShouldGlow(int affixRarity) => affixRarity >= MinimumGlowingAffixRarity;

        // Maps the 14-tier ITEM scale onto the same five colour bands, so an
        // item and its affixes speak the same visual language. The bands follow
        // GDD 5.2's own affix-count groupings rather than an arbitrary split -
        // the tiers that share an affix count share a colour.
        public static Color GetItemRarityColor(int itemRarityTier)
        {
            if (itemRarityTier <= 3) return _affixColors[0];
            if (itemRarityTier <= 6) return _affixColors[1];
            if (itemRarityTier <= 9) return _affixColors[2];
            if (itemRarityTier <= 12) return _affixColors[3];
            return _affixColors[4];
        }

        public static bool ItemShouldGlow(int itemRarityTier) => itemRarityTier >= 10;

        // Applies colour and, for Epic and above, a pulsing vertex glow.
        //
        // Uses TMP's built-in Glow face material rather than a custom shader so
        // it needs no asset import and survives a scene rebuild - the scene is
        // reconstructed entirely from MainSceneBuilder, so anything requiring a
        // hand-assigned material would be lost on the next rebuild.
        public static void ApplyAffixRarity(TextMeshProUGUI target, int affixRarity)
        {
            if (target == null) return;

            target.color = GetAffixRarityColor(affixRarity);

            UiRarityGlow glow = target.GetComponent<UiRarityGlow>();
            if (ShouldGlow(affixRarity))
            {
                if (glow == null) glow = target.gameObject.AddComponent<UiRarityGlow>();
                glow.Target = target;
                glow.GlowColor = GetAffixRarityColor(affixRarity);
                glow.enabled = true;
            }
            else if (glow != null)
            {
                // Reset rather than destroy: rows are pooled and rebound, so a
                // Legendary row recycled for a Common affix must stop glowing
                // without churning components every refresh.
                glow.enabled = false;
                glow.ResetToPlain();
            }
        }
    }
}
