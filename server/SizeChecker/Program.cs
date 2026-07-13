using System;
using FolkIdle.Server.Network;

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
