namespace FolkIdle.Server.Engine
{
    public unsafe struct StatusEffectBuffer
    {
        public fixed int ActiveModifiers[8];
        public uint ActiveStatusEffectModifierBitmask;
        public uint RemainingBuffDurationTicks;
    }
}
