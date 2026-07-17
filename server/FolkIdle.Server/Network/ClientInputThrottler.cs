using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    public class ClientInputThrottler
    {
        private readonly long[] _timestamps = new long[5];
        private int _index = 0;

        public bool IsPacketAllowed()
        {
            long currentTick = Environment.TickCount64;
            int oldestIndex = (_index + 1) % 5;
            long oldestTick = _timestamps[oldestIndex];

            // If the buffer hasn't filled yet, oldestTick will be 0, which correctly allows the packet.
            if (oldestTick != 0 && (currentTick - oldestTick) < 100)
            {
                // Discard packet: Delta between oldest and current is less than 100ms
                return false;
            }

            // Accept packet: write timestamp into ring buffer
            _timestamps[_index] = currentTick;
            _index = oldestIndex;
            return true;
        }
    }
}
