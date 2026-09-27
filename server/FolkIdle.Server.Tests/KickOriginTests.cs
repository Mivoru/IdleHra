using System.Runtime.CompilerServices;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: a forced disconnect logs which handler kicked (see
    // NetworkBroadcastSystem.DescribeKickOrigin). If the stack walk ever
    // stops naming the caller, "[kick]" lines in production go blank again.
    public class KickOriginTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string AValidatorThatRefused() => NetworkBroadcastSystem.DescribeKickOrigin(0);

        [Fact]
        public void TheOriginNamesTheCallingHandler()
        {
            string origin = AValidatorThatRefused();
            Assert.StartsWith("KickOriginTests.AValidatorThatRefused", origin);
        }
    }
}
