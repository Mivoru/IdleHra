using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 13.4.3: renders a single locus comparison row for the Breeding
    // Lab preview (Race/Speed/Crit/Yield) - parent dominant alleles side by
    // side with the predicted offspring range, plus the mutation chance. One
    // instance per locus, assigned explicitly in the Inspector (matching
    // UiVillageBuildingRow's fixed-slot style, since the locus set is a
    // stable fixed 4, not a variable-length list needing a pool). Bind() is
    // only called when UiBreedingLabWindow's preview data actually changes,
    // never from an Update() loop.
    public class UiGeneVectorRenderer : MonoBehaviour
    {
        // Alleles are packed as a single byte in GeneticVector (see
        // FolkIdle.Server.Engine.Locus), so 255 is the true maximum any
        // allele value can take.
        private const float MaxAlleleValue = 255f;

        public TMP_Text LocusNameText;
        public Image PaternalAlleleBar;
        public Image MaternalAlleleBar;
        public Image PredictedMinBar;
        public Image PredictedMaxBar;
        public TMP_Text PredictedRangeText;
        public TMP_Text MutationChanceText;

        private readonly char[] _rangeUiBuffer = new char[32];
        private readonly char[] _mutationUiBuffer = new char[24];

        public void Bind(GenePreviewLocusData locus)
        {
            if (locus == null)
            {
                Clear();
                return;
            }

            if (LocusNameText != null)
            {
                LocusNameText.text = locus.LocusName;
            }

            SetFill(PaternalAlleleBar, locus.ParentPaternalDominant);
            SetFill(MaternalAlleleBar, locus.ParentMaternalDominant);
            SetFill(PredictedMinBar, locus.PredictedMinDominant);
            SetFill(PredictedMaxBar, locus.PredictedMaxDominant);

            if (PredictedRangeText != null)
            {
                int offset = WriteIntToBuffer(_rangeUiBuffer, 0, locus.PredictedMinDominant);
                offset = WriteTextToBuffer(_rangeUiBuffer, offset, " - ");
                offset = WriteIntToBuffer(_rangeUiBuffer, offset, locus.PredictedMaxDominant);
                PredictedRangeText.SetCharArray(_rangeUiBuffer, 0, offset);
            }

            if (MutationChanceText != null)
            {
                int tenths = Mathf.RoundToInt((float)locus.MutationChancePct * 10f);
                if (tenths < 0) tenths = 0;
                int whole = tenths / 10;
                int frac = tenths % 10;

                int offset = WriteIntToBuffer(_mutationUiBuffer, 0, whole);
                _mutationUiBuffer[offset++] = '.';
                _mutationUiBuffer[offset++] = (char)('0' + frac);
                _mutationUiBuffer[offset++] = '%';
                MutationChanceText.SetCharArray(_mutationUiBuffer, 0, offset);
            }
        }

        public void Clear()
        {
            SetFill(PaternalAlleleBar, 0);
            SetFill(MaternalAlleleBar, 0);
            SetFill(PredictedMinBar, 0);
            SetFill(PredictedMaxBar, 0);

            if (PredictedRangeText != null) PredictedRangeText.SetCharArray(_rangeUiBuffer, 0, 0);
            if (MutationChanceText != null) MutationChanceText.SetCharArray(_mutationUiBuffer, 0, 0);
        }

        private static void SetFill(Image bar, int alleleValue)
        {
            if (bar != null)
            {
                bar.fillAmount = Mathf.Clamp01(alleleValue / MaxAlleleValue);
            }
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            if (value < 0)
            {
                buffer[offset++] = '-';
                value = -value;
            }

            int temp = value;
            int length = 0;
            while (temp > 0)
            {
                temp /= 10;
                length++;
            }

            int endOffset = offset + length;
            temp = value;
            for (int i = endOffset - 1; i >= offset; i--)
            {
                buffer[i] = (char)('0' + (temp % 10));
                temp /= 10;
            }
            return endOffset;
        }
    }
}
