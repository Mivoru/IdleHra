using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The source text of a class that is split over partial files, for the
    /// tests that pin a call site or forbid a pattern by reading the source.
    /// </summary>
    /// <remarks>
    /// Modul: A SOURCE GUARD MUST FOLLOW THE CODE WHEN IT MOVES (2026-11-20).
    /// NetworkBroadcastSystem's handlers were split into
    /// NetworkBroadcastSystem.*.cs. Five tests read "NetworkBroadcastSystem.cs"
    /// by name. A "does not contain" guard over one file would have kept
    /// passing while the pattern it forbids moved next door. So every such
    /// test reads the whole class: the base file and every partial beside it,
    /// in name order, each headed by its file name. SimulationEngine was split
    /// the same way on the same day.
    /// </remarks>
    internal static class PartialClassSource
    {
        /// <summary>
        /// A file's text, or - when partial files named "Name.*.cs" sit beside
        /// it - the whole class. Drop-in for File.ReadAllText in a source guard.
        /// </summary>
        public static string ReadFile(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string name = Path.GetFileNameWithoutExtension(path);
            return Directory.GetFiles(directory, name + ".*.cs").Length > 0 ? Read(directory, name) : File.ReadAllText(path);
        }

        /// <param name="directory">The folder that holds the class's files.</param>
        /// <param name="className">The base file's name without ".cs".</param>
        public static string Read(string directory, string className)
        {
            var files = new[] { Path.Combine(directory, className + ".cs") }
                .Concat(Directory.GetFiles(directory, className + ".*.cs").OrderBy(f => f, StringComparer.Ordinal))
                .Where(File.Exists)
                .ToArray();
            if (files.Length == 0) throw new FileNotFoundException($"{className}.cs not found in {directory}");
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.Append("// FILE: ").Append(Path.GetFileName(file)).Append('\n');
                sb.Append(File.ReadAllText(file)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
