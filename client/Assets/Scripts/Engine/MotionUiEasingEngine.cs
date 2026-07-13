using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FolkIdle.Client.Engine
{
    public struct UiTransitionState
    {
        public long StartTimestamp;
        public long DurationTicks;
        public float StartValue;
        public float TargetValue;
    }

    public static class MotionUiEasingEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StartTransition(ref UiTransitionState state, float start, float target, double durationMs)
        {
            state.StartTimestamp = Stopwatch.GetTimestamp();
            state.DurationTicks = (long)(durationMs * Stopwatch.Frequency / 1000.0);
            state.StartValue = start;
            state.TargetValue = target;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Evaluate(ref UiTransitionState state)
        {
            if (state.StartTimestamp == 0) return state.TargetValue;

            long currentTimestamp = Stopwatch.GetTimestamp();
            long elapsed = currentTimestamp - state.StartTimestamp;
            
            if (elapsed >= state.DurationTicks)
            {
                return state.TargetValue;
            }

            if (elapsed <= 0)
            {
                return state.StartValue;
            }

            float t = (float)((double)elapsed / state.DurationTicks);
            
            // Cubic Ease-Out: 1 - (1 - t)^3
            float invT = 1.0f - t;
            float easedT = 1.0f - (invT * invT * invT);

            return state.StartValue + (state.TargetValue - state.StartValue) * easedT;
        }
    }
}
