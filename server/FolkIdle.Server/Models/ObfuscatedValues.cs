namespace FolkIdle.Server.Models
{
    public struct ObfuscatedInt64
    {
        private long _clandestineValue;
        private long _sessionXorKey;

        public ObfuscatedInt64(long value, long sessionXorKey)
        {
            _sessionXorKey = sessionXorKey == 0L ? 0x5F3759DF5F3759DFL : sessionXorKey;
            _clandestineValue = value ^ _sessionXorKey;
        }

        public long Value
        {
            readonly get => _clandestineValue ^ _sessionXorKey;
            set => _clandestineValue = value ^ _sessionXorKey;
        }

        public void ResetKey(long sessionXorKey)
        {
            long clearValue = Value;
            _sessionXorKey = sessionXorKey == 0L ? 0x5F3759DF5F3759DFL : sessionXorKey;
            _clandestineValue = clearValue ^ _sessionXorKey;
        }
    }

    public struct ObfuscatedInt32
    {
        private int _clandestineValue;
        private int _sessionXorKey;

        public ObfuscatedInt32(int value, int sessionXorKey)
        {
            _sessionXorKey = sessionXorKey == 0 ? unchecked((int)0x9E3779B9) : sessionXorKey;
            _clandestineValue = value ^ _sessionXorKey;
        }

        public int Value
        {
            readonly get => _clandestineValue ^ _sessionXorKey;
            set => _clandestineValue = value ^ _sessionXorKey;
        }

        public void ResetKey(int sessionXorKey)
        {
            int clearValue = Value;
            _sessionXorKey = sessionXorKey == 0 ? unchecked((int)0x9E3779B9) : sessionXorKey;
            _clandestineValue = clearValue ^ _sessionXorKey;
        }
    }
}
