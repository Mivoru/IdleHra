using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Client.Network
{
    // Modul: generic Cheat-Engine-style memory obfuscation for int/long/float
    // client state. The plaintext value is never held in a field - only ever
    // materialized transiently on the stack inside Value's getter/setter -
    // and _sessionXorKey can be re-rolled in place via Rotate() so the
    // stored bytes keep changing even while the logical value stays put,
    // defeating a scanner that looks for a byte pattern that never moves.
    // Backing storage always widens through a 64-bit XOR regardless of T's
    // real width (float's bit pattern is reinterpreted through Widen/Narrow,
    // not its numeric value, so the round trip is exact and lossless), so
    // one implementation covers all three supported types instead of a
    // separate struct per type. typeof(T) checks below are resolved to a
    // single branch per closed generic instantiation at JIT time (a
    // standard, allocation-free way to dispatch on an unmanaged value type
    // in .NET), so this stays zero heap overhead like its fixed-type
    // predecessors ObfuscatedInt32/ObfuscatedInt64 below and the hand-rolled
    // XorShift32 PRNGs used throughout combat/genetics.
    public struct ObfuscatedValue<T> where T : unmanaged
    {
        private long _clandestineValue;
        private long _sessionXorKey;

        public ObfuscatedValue(T value, long sessionXorKey)
        {
            _sessionXorKey = sessionXorKey == 0L ? 0x5F3759DF5F3759DFL : sessionXorKey;
            _clandestineValue = Widen(value) ^ _sessionXorKey;
        }

        public T Value
        {
            readonly get => Narrow(_clandestineValue ^ _sessionXorKey);
            set => _clandestineValue = Widen(value) ^ _sessionXorKey;
        }

        // Re-keys in place with a freshly rolled key: decrypts with the old
        // key, re-encrypts with the new one. The plaintext exists only for
        // the duration of this call, on the stack, never written back to
        // any field in cleartext.
        public void Rotate(long newSessionXorKey)
        {
            T clearValue = Value;
            _sessionXorKey = newSessionXorKey == 0L ? 0x5F3759DF5F3759DFL : newSessionXorKey;
            _clandestineValue = Widen(clearValue) ^ _sessionXorKey;
        }

        private static long Widen(T value)
        {
            if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref value);
            if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref value);
            if (typeof(T) == typeof(float)) return Unsafe.As<T, int>(ref value);
            throw new NotSupportedException("ObfuscatedValue<T> supports only int, long, and float.");
        }

        private static T Narrow(long widened)
        {
            if (typeof(T) == typeof(int))
            {
                int narrowed = unchecked((int)widened);
                return Unsafe.As<int, T>(ref narrowed);
            }
            if (typeof(T) == typeof(long))
            {
                return Unsafe.As<long, T>(ref widened);
            }
            if (typeof(T) == typeof(float))
            {
                int bits = unchecked((int)widened);
                return Unsafe.As<int, T>(ref bits);
            }
            throw new NotSupportedException("ObfuscatedValue<T> supports only int, long, and float.");
        }
    }

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
