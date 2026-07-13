namespace FolkIdle.Server.Engine
{
    public unsafe struct ConsumableApplicationSignal
    {
        public uint StatusEffectModifierBitmask;
        public uint DurationTicks;
        public fixed int ActiveModifiers[8];
    }
}
