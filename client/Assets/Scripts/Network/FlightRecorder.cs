using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FolkIdle.Client.Network
{
    public static unsafe class FlightRecorder
    {
        private const int BufferSizeBytes = 64 * 1024;
        private const int RecordSizeBytes = 32; // 16 bytes * 2 chars/byte
        private static IntPtr _buffer;
        private static int _cursor;
        private static bool _initialized;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void EditorCleanup()
        {
            Shutdown();
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _buffer = Marshal.AllocHGlobal(BufferSizeBytes);
            byte* ptr = (byte*)_buffer;
            for (int i = 0; i < BufferSizeBytes; i++)
            {
                ptr[i] = 0;
            }

            _cursor = 0;
            _initialized = true;
        }

        public static void RecordInbound(byte packetKind, int byteLength, long logicEpoch)
        {
            WriteRecord(1, packetKind, byteLength, logicEpoch);
        }

        public static void RecordOutbound(byte commandType, int byteLength, long logicEpoch)
        {
            WriteRecord(2, commandType, byteLength, logicEpoch);
        }

        public static void RecordNetworkState(byte stateCode)
        {
            WriteRecord(3, stateCode, 0, 0);
        }

        public static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _cursor = 0;
            _initialized = false;
        }

        private static void WriteRecord(byte eventType, byte code, int byteLength, long logicEpoch)
        {
            if (!_initialized)
            {
                Initialize();
            }

            // Atomic Interlocked add ensures thread safety from network thread and main thread
            int startCursor = Interlocked.Add(ref _cursor, RecordSizeBytes) - RecordSizeBytes;
            int localCursor = startCursor;
            byte* ptr = (byte*)_buffer;

            void WriteHexByte(byte b)
            {
                int high = (b >> 4) & 0xF;
                byte charHigh = (byte)(high < 10 ? 0x30 + high : 0x41 + (high - 10));
                ptr[(localCursor++) & (BufferSizeBytes - 1)] = charHigh;
                
                int low = b & 0xF;
                byte charLow = (byte)(low < 10 ? 0x30 + low : 0x41 + (low - 10));
                ptr[(localCursor++) & (BufferSizeBytes - 1)] = charLow;
            }

            WriteHexByte(eventType);
            WriteHexByte(code);
            WriteHexByte((byte)(byteLength & 0xFF));
            WriteHexByte((byte)((byteLength >> 8) & 0xFF));
            
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < 8; i++)
            {
                WriteHexByte((byte)((timestamp >> (i * 8)) & 0xFF));
            }
            
            int epoch = unchecked((int)(logicEpoch & 0x7FFFFFFF));
            for (int i = 0; i < 4; i++)
            {
                WriteHexByte((byte)((epoch >> (i * 8)) & 0xFF));
            }
        }
    }
}
