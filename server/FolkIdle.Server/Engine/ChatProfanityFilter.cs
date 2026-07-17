using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    // Modul: Comprehensive Game System Audit, Part 2.3. Allocation-free
    // profanity/slur filter for the global and guild chat channels -
    // previously no moderation of any kind existed on the chat path (see
    // ChatEngine; messages flowed from RequestChatMessagePacket straight
    // to Redis pub/sub broadcast verbatim).
    //
    // Zero-allocation design: the blacklist is compiled once at class
    // load into a flat byte[][] of lowercase UTF-8 patterns, and
    // FilterInPlace scans the packet's fixed MessageText byte buffer
    // directly - before ExtractChatMessageText ever materializes a
    // managed string - replacing every matched span with 0x2A ('*')
    // bytes in place. The scan itself is pure index arithmetic over
    // spans of preallocated arrays: no substring, no ToLower string
    // copy (case folding is done per-byte with arithmetic), no regex,
    // no transient garbage per message. Called once per inbound chat
    // packet on the WebSocket receive loop, which is the single choke
    // point both channels (global and guild) share, so one pass covers
    // everything.
    //
    // Matching is case-insensitive (ASCII fold) and substring-based:
    // a blacklisted sequence embedded inside a longer word is still
    // masked. Substring matching is deliberate - evasion by suffixing
    // ("wordxyz") defeats whole-word-only filters - and the word list
    // is curated to terms with effectively no innocent-collision
    // surface in normal game chat rather than an exhaustive dictionary,
    // which keeps false positives negligible without needing
    // per-language morphology.
    public static class ChatProfanityFilter
    {
        // Curated lowercase blacklist. UTF-8 compiled once below - the
        // string literals themselves exist only during static init, never
        // on the per-message path.
        private static readonly string[] BlacklistWords =
        {
            "fuck",
            "shit",
            "bitch",
            "cunt",
            "asshole",
            "faggot",
            "nigger",
            "nigga",
            "retard",
            "whore",
            "slut",
            "kurva",
            "pica",
            "zmrd",
            "debil",
            "hovno",
            "curak",
            "negr"
        };

        private static readonly byte[][] CompiledPatterns = CompilePatterns();

        private static byte[][] CompilePatterns()
        {
            var compiled = new byte[BlacklistWords.Length][];
            for (int i = 0; i < BlacklistWords.Length; i++)
            {
                compiled[i] = System.Text.Encoding.UTF8.GetBytes(BlacklistWords[i]);
            }
            return compiled;
        }

        // Folds an ASCII uppercase byte to lowercase; leaves every other
        // byte (including multi-byte UTF-8 continuation bytes) untouched.
        // Pure arithmetic, no table allocation.
        private static byte FoldAsciiLower(byte value)
        {
            return value >= (byte)'A' && value <= (byte)'Z' ? (byte)(value + 32) : value;
        }

        // Scans messageBytes[0..messageLength) for every compiled pattern
        // and overwrites each match with '*' bytes in place. Returns the
        // number of matches masked (0 means the message passed clean).
        // Operates directly on the caller's buffer - the unsafe fixed
        // MessageText buffer of RequestChatMessagePacket via a Span, or
        // any byte[] in tests - and allocates nothing on any path.
        public static int FilterInPlace(Span<byte> messageBytes, int messageLength)
        {
            if (messageLength <= 0 || messageLength > messageBytes.Length)
            {
                return 0;
            }

            int totalMasked = 0;
            byte[][] patterns = CompiledPatterns;

            for (int p = 0; p < patterns.Length; p++)
            {
                byte[] pattern = patterns[p];
                int patternLength = pattern.Length;
                if (patternLength == 0 || patternLength > messageLength)
                {
                    continue;
                }

                int scanLimit = messageLength - patternLength;
                for (int start = 0; start <= scanLimit; start++)
                {
                    bool matched = true;
                    for (int j = 0; j < patternLength; j++)
                    {
                        if (FoldAsciiLower(messageBytes[start + j]) != pattern[j])
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (matched)
                    {
                        for (int j = 0; j < patternLength; j++)
                        {
                            messageBytes[start + j] = (byte)'*';
                        }
                        totalMasked++;
                        start += patternLength - 1;
                    }
                }
            }

            return totalMasked;
        }
    }
}
