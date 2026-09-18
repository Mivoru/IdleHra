using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Extends StateUpdatePacketFieldCoverageTests to a second layer: does
    /// RedisSessionCache.TryStoreFrame's frame actually carry what it claims
    /// to, and does it stay the narrow, fixed-size thing CLAUDE.md documents
    /// rather than silently growing?
    ///
    /// The frame is not a struct declaration - it is an inline HashEntry[]
    /// literal - so there is no separate "declared fields" list to compare
    /// against an "assigned fields" list the way the wire test does. What CAN
    /// be checked mechanically: (1) every entry's value expression actually
    /// reads from the payload (`state.X`), rather than a hardcoded literal or
    /// a copy-pasted wrong field - the same "silently assigns nothing useful"
    /// defect class the wire test guards, applied to this site; and (2) the
    /// frame's field COUNT and NAMES stay pinned to what this comment (and
    /// CLAUDE.md's own description: "twelve fields - level, xp, lineage,
    /// logout stamp, time bank, epoch, quarantine, gold, three counters")
    /// says it is, so a silent addition here - which would look like "the
    /// frame now covers this too" - forces a conscious decision instead,
    /// since CLAUDE.md's "A Redis frame is not a checkpoint" trap is
    /// specifically about the frame being mistaken for durable persistence
    /// it never was.
    /// </summary>
    public class RedisFrameFieldCoverageTests
    {
        // The frame's field names, in the order TryStoreFrame declares them -
        // the same twelve CLAUDE.md's "A Redis frame is not a checkpoint"
        // trap names (level, xp, lineage, logout stamp, time bank, epoch,
        // quarantine, gold, three counters - player_id/inventory_space/
        // ticks_since_last_flush being the "three counters", updated_at the
        // twelfth). Excludes the conditional gold/wood/stone/iron buffer
        // writes below the main HashEntry array, which are their own
        // explicit if-guarded blocks rather than part of the fixed frame.
        private static readonly string[] ExpectedFrameFields =
        {
            "player_id",
            "current_level",
            "current_xp",
            "selected_lineage_id",
            "last_logout_ts",
            "accumulated_time_bank_seconds",
            "logic_epoch_counter",
            "is_quarantined",
            "current_gold_frame",
            "inventory_space_remaining",
            "ticks_since_last_flush",
            "updated_at",
        };

        private static string LocateSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "FolkIdle.Server", "Engine", "RedisSessionCache.cs");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException("RedisSessionCache.cs not found from " + AppContext.BaseDirectory);
        }

        private static string ReadFrameEntriesBlock(string source)
        {
            int start = source.IndexOf("var entries = new HashEntry[]", StringComparison.Ordinal);
            Assert.True(start >= 0, "could not find the frame's HashEntry[] literal - has TryStoreFrame been restructured?");

            int openBrace = source.IndexOf('{', start);
            int depth = 1;
            int i = openBrace + 1;
            for (; i < source.Length && depth > 0; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}') depth--;
            }

            return source.Substring(openBrace, i - openBrace);
        }

        [Fact]
        public void TheFrameIsExactlyTheDocumentedFields()
        {
            string source = File.ReadAllText(LocateSource());
            string block = ReadFrameEntriesBlock(source);

            var actualFields = Regex.Matches(block, @"new\(""([a-z_]+)""")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.Equal(ExpectedFrameFields, actualFields);
        }

        [Fact]
        public void EveryFrameFieldReadsFromThePayloadNotALiteral()
        {
            string source = File.ReadAllText(LocateSource());
            string block = ReadFrameEntriesBlock(source);

            // Each entry sits on its own line - `new("name", <value-expr>)`,
            // or `new("name", <expr> ? 1 : 0)` for the boolean-flag case -
            // so a per-line match avoids the ambiguity a multiline regex hits
            // when the value expression itself contains parentheses (e.g.
            // `ToUnixTimeSeconds()`, whose own closing paren is not the
            // entry's closing paren).
            var entryLines = block
                .Split('\n')
                .Select(line => Regex.Match(line, @"new\(""([a-z_]+)"",\s*(.+)\)"))
                .Where(m => m.Success)
                .ToList();

            Assert.True(entryLines.Count >= ExpectedFrameFields.Length,
                $"expected to parse at least {ExpectedFrameFields.Length} frame entries, found {entryLines.Count} - " +
                "the parsing regex may no longer match TryStoreFrame's current formatting");

            var notFromState = new List<string>();
            foreach (Match m in entryLines)
            {
                string name = m.Groups[1].Value;
                string valueExpr = m.Groups[2].Value;
                if (name == "updated_at") continue; // a timestamp, deliberately not payload-sourced

                if (!valueExpr.Contains("state.", StringComparison.Ordinal))
                {
                    notFromState.Add(name);
                }
            }

            Assert.True(notFromState.Count == 0,
                "these frame entries do not read from `state` at all, so they ship a hardcoded or stale " +
                "value into every session's Redis frame: " + string.Join(", ", notFromState));
        }
    }
}
