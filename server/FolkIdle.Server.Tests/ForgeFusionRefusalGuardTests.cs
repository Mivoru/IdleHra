using System;
using System.IO;
using System.Linq;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A refused fusion must never tear the session down.
    ///
    /// Modul: "when I fuse quickly the forge breaks for a second". The Forge
    /// picks the next three pieces from a list fetched before the previous
    /// fusion committed, so a quick second tap names a sacrifice that is
    /// already gone. ForgeSplicingEngine answers that with TargetNotFound and
    /// returns InvalidRequest - and the coordinator's continuation turned
    /// InvalidRequest into ForceDisconnect, so the player watched the
    /// connection drop and come back. The handler is wired through the tick's
    /// coordinator context, so this pins it by source: the single-fusion
    /// handler contains no ForceDisconnect at all.
    /// </summary>
    public class ForgeFusionRefusalGuardTests
    {
        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "FolkIdle.Server");
        }

        [Fact]
        public void TheSingleFusionHandlerNeverDisconnects()
        {
            string file = Path.Combine(ServerRoot(), "Domain", "Economy", "ForgeTickCoordinator.cs");
            string source = File.ReadAllText(file);

            int start = source.IndexOf("internal static void HandleExecuteForgeFusion(", StringComparison.Ordinal);
            int end = source.IndexOf("internal static void HandleFuseStack(", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "the fusion handlers moved; update this guard");

            var code = source[start..end]
                .Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//"));

            Assert.DoesNotContain(code, line => line.Contains("ForceDisconnect"));
        }
    }
}
