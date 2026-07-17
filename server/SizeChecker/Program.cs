using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace SizeChecker
{
    class Program
    {
        static unsafe void Main()
        {
            Console.WriteLine($"ClientCommandPacket: {sizeof(ClientCommandPacket)} bytes");
            Console.WriteLine($"StateUpdatePacket: {sizeof(StateUpdatePacket)} bytes");
        }
    }
}
