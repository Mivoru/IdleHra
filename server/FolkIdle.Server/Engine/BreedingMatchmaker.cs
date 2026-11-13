using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// "Who should my hero marry?" - ranks a candidate partner by the child the
    /// pair would make, using the same bands the preview quotes.
    /// </summary>
    /// <remarks>
    /// Modul: THE QUESTION THE SCREEN NEVER ANSWERED (2026-11-13). The owner's
    /// brief called breeding "unclear": choosing a partner meant opening the
    /// preview for each of up to thirty candidates in turn and comparing four
    /// bands in your head, while dodging relatives the list did not mark. This
    /// does that comparison on the server, from BreedingAptitudes.PreviewOne
    /// and PreviewSelected - the bands the preview shows - so the suggestion
    /// can never disagree with the forecast the player then sees.
    ///
    /// PURE AND STATIC like the rest of the breeding rules: the endpoint loads
    /// the rows and the relatedness, this only does arithmetic.
    /// </remarks>
    public static class BreedingMatchmaker
    {
        /// <param name="Score">Expected child aptitude total, in tenths, with the best selection.</param>
        /// <param name="SelectionMask">The aptitudes worth ticking for this pair (at most the Grounds' count).</param>
        /// <param name="ChildFloorTotal">The lowest total the child can come out with, with that selection.</param>
        public readonly record struct Match(int Score, int SelectionMask, int ChildFloorTotal);

        public static Match Score(int[] hero, int[] partner, int ceiling, bool isRelated, int selectableCount)
        {
            if (hero is null || hero.Length < BreedingAptitudes.Count) throw new ArgumentException("hero vector", nameof(hero));
            if (partner is null || partner.Length < BreedingAptitudes.Count) throw new ArgumentException("partner vector", nameof(partner));

            var unselectedMid = new int[BreedingAptitudes.Count];
            var unselectedMin = new int[BreedingAptitudes.Count];
            var selectedMid = new int[BreedingAptitudes.Count];
            var selectedMin = new int[BreedingAptitudes.Count];
            for (int i = 0; i < BreedingAptitudes.Count; i++)
            {
                BreedingAptitudes.PreviewOne(hero[i], partner[i], out int min, out int max, ceiling, isRelated);
                BreedingAptitudes.PreviewSelected(hero[i], partner[i], out int selMin, out int selMax, ceiling, isRelated);
                // Tenths: the middle of each band, a fair stand-in for its mean.
                unselectedMid[i] = (min + max) * 5;
                unselectedMin[i] = min;
                selectedMid[i] = (selMin + selMax) * 5;
                selectedMin[i] = selMin;
            }

            // Tick where ticking gains the most - greedy is exact here, because
            // each aptitude's gain is independent of the others.
            int mask = 0;
            int picks = Math.Clamp(selectableCount, 0, BreedingAptitudes.Count - 1);
            for (int k = 0; k < picks; k++)
            {
                int best = -1;
                int bestGain = 0;
                for (int i = 0; i < BreedingAptitudes.Count; i++)
                {
                    if ((mask & (1 << i)) != 0) continue;
                    int gain = selectedMid[i] - unselectedMid[i];
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        best = i;
                    }
                }
                if (best < 0) break;
                mask |= 1 << best;
            }

            int score = 0;
            int floor = 0;
            for (int i = 0; i < BreedingAptitudes.Count; i++)
            {
                bool selected = (mask & (1 << i)) != 0;
                score += selected ? selectedMid[i] : unselectedMid[i];
                floor += selected ? selectedMin[i] : unselectedMin[i];
            }
            return new Match(score, mask, floor);
        }
    }
}
