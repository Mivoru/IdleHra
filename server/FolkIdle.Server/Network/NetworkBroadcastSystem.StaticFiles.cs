using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    // Modul: moved out of NetworkBroadcastSystem.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md). Routing stays in RouteCoreAsync.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>
        /// Stores this device's push token against the signed-in player.
        /// </summary>
        /// <remarks>
        /// Modul: EVERY REFUSAL HERE IS AUDIBLE, ON PURPOSE.
        ///
        /// The engine behind this used to be reachable only through opcode 33,
        /// whose whole failure mode was silence: `RegisterDeviceAsync` returned
        /// early on a bad length and the client - which had no reply channel
        /// anyway - carried on believing it had registered. This route answers,
        /// and the Settings screen repeats the answer to the player, because a
        /// push feature that silently never fires is indistinguishable from one
        /// the player turned off.
        ///
        ///   400  the body is not a token, or names a platform that is not
        ///        ios/android
        ///   401  no session
        ///   503  the engine was never registered (a start-up fault, not the
        ///        player's)
        ///   500  the write failed
        ///
        /// The platform arrives as a WORD and is mapped to the stored 1/2 here,
        /// once. A magic number crossing the wire would be that mapping written
        /// down in two languages, which is how KNOWN_AFFIX_IDS drifted.
        /// </remarks>
        /// <summary>
        /// Tells a phone which web bundle it should be running.
        ///
        /// Modul: FAILS SAFE, and that is the most important property here.
        ///
        /// With no bundle configured this answers "you are up to date". It does
        /// not guess a version, it does not 500, and it does not hand back a URL
        /// that might not exist - because the consequence of getting this wrong
        /// is not a failed request, it is every phone downloading and applying
        /// something broken. Silence is always a safe answer for an update
        /// check; a wrong answer never is.
        ///
        /// So the feature is INERT until FOLKIDLE_BUNDLE_VERSION and
        /// FOLKIDLE_BUNDLE_URL are both set. Deploying this code without them
        /// changes nothing for anybody, which is the state it should stay in
        /// until somebody has watched an update land on a real device.
        ///
        /// The bundle itself is a static file served by Caddy - see
        /// ops/oracle/caddy/Caddyfile. This endpoint only ever names it.
        /// </summary>
        private async Task HandleLiveBundleManifest(HttpListenerContext context)
        {
            try
            {
                string version = Environment.GetEnvironmentVariable("FOLKIDLE_BUNDLE_VERSION") ?? string.Empty;
                string url = Environment.GetEnvironmentVariable("FOLKIDLE_BUNDLE_URL") ?? string.Empty;

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";

                // The plugin treats a reply carrying no `url` as "nothing to do".
                string payload = (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
                    ? "{\"message\":\"no bundle configured\"}"
                    : "{\"version\":\"" + JsonEscape(version) + "\",\"url\":\"" + JsonEscape(url) + "\"}";

                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleLiveBundleManifest failed: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private async Task HandleGameDataFile(HttpListenerContext context, string fileName)
        {
            try
            {
                if (!GameDataFileNamePattern.IsMatch(fileName) || ServerOnlyGameDataFiles.Contains(fileName))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string fullPath = System.IO.Path.Combine(GameDataDirectory, fileName);
                if (!System.IO.File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var info = new System.IO.FileInfo(fullPath);

                // Content changes only on deploy, but a stale content file is
                // a silent wrong-data bug rather than a visible failure, so
                // this revalidates every time and answers 304 when nothing
                // moved rather than letting a browser cache it blind.
                string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
                if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Close();
                    return;
                }

                byte[] payload = await System.IO.File.ReadAllBytesAsync(fullPath);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.Headers["ETag"] = etag;
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GameData file error for '{fileName}': {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Same shape as HandleGameDataFile: the name is validated rather than
        // sanitized, so a request that is not exactly "word.wav" is rejected
        // outright and path traversal never needs reasoning about.
        private async Task HandleAudioFile(HttpListenerContext context, string fileName)
        {
            try
            {
                if (!AudioFileNamePattern.IsMatch(fileName))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string fullPath = System.IO.Path.Combine(AudioDirectory, fileName);
                if (!System.IO.File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var info = new System.IO.FileInfo(fullPath);
                string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
                if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Close();
                    return;
                }

                byte[] payload = await System.IO.File.ReadAllBytesAsync(fullPath);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "audio/wav";
                context.Response.Headers["ETag"] = etag;
                // Sound effects change only on a deploy and are fetched once
                // per session, so a long cache is safe and saves ten requests.
                context.Response.Headers["Cache-Control"] = "public, max-age=86400";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio file error for '{fileName}': {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleMusicManifest(HttpListenerContext context)
        {
            try
            {
                var files = System.IO.Directory.Exists(MusicDirectory)
                    ? System.IO.Directory.GetFiles(MusicDirectory, "*.mp3")
                        .Select(f => System.IO.Path.GetFileName(f))
                        .Where(n => MusicFileNamePattern.IsMatch(n))
                        // Modul: "screen_<name>.mp3" is a screen's own theme (Nav's,
                        // 2026-11-19), played by the client while that screen is
                        // open - never part of the everywhere cycle.
                        .Where(n => !n.StartsWith(ScreenThemePrefix, StringComparison.Ordinal))
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList()
                    : new System.Collections.Generic.List<string>();

                var tracks = files.Select(f => new
                {
                    File = f,
                    // "Where_the_Path_Begins.mp3" -> "Where the Path Begins".
                    Title = System.IO.Path.GetFileNameWithoutExtension(f).Replace('_', ' '),
                });

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.Headers["Cache-Control"] = "public, max-age=3600";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Tracks = tracks });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Music manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleMusicFile(HttpListenerContext context, string fileName)
        {
            try
            {
                if (!MusicFileNamePattern.IsMatch(fileName))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string fullPath = System.IO.Path.Combine(MusicDirectory, fileName);
                if (!System.IO.File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                long length = new System.IO.FileInfo(fullPath).Length;
                long start = 0, end = length - 1;
                bool partial = false;

                string? range = context.Request.Headers["Range"];
                if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string[] bounds = range.Substring(6).Split('-', 2);
                    if (bounds.Length == 2 && long.TryParse(bounds[0], out long rangeStart) && rangeStart >= 0 && rangeStart < length)
                    {
                        start = rangeStart;
                        end = long.TryParse(bounds[1], out long rangeEnd) && rangeEnd >= start && rangeEnd < length ? rangeEnd : length - 1;
                        partial = true;
                    }
                    else
                    {
                        context.Response.StatusCode = 416;
                        context.Response.Headers["Content-Range"] = $"bytes */{length}";
                        context.Response.Close();
                        return;
                    }
                }

                long count = end - start + 1;
                context.Response.StatusCode = partial ? 206 : 200;
                context.Response.ContentType = "audio/mpeg";
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.Headers["Cache-Control"] = "public, max-age=86400";
                if (partial) context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
                context.Response.ContentLength64 = count;

                await using var stream = new System.IO.FileStream(fullPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                stream.Seek(start, System.IO.SeekOrigin.Begin);
                byte[] buffer = new byte[64 * 1024];
                long remaining = count;
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, read);
                    remaining -= read;
                }
            }
            catch (Exception ex)
            {
                // A listener that closed the tab mid-track lands here too - one line, not an alarm.
                Console.WriteLine($"Music file '{fileName}': {ex.Message}");
            }

            try { context.Response.Close(); } catch { }
        }

        private async Task HandleSpriteFile(HttpListenerContext context, string relativePath)
        {
            try
            {
                string decoded = Uri.UnescapeDataString(relativePath);

                // WebP, not PNG - this art is hand-painted with gradients,
                // which is exactly what PNG compresses worst. See
                // tools/clean_sprites.py.
                if (!decoded.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string[] segments = decoded.Split('/');
                foreach (string segment in segments)
                {
                    // An empty segment ("a//b"), a dot segment, or anything
                    // outside the permitted set is refused rather than cleaned.
                    if (segment.Length == 0 || segment == "." || segment == ".." || !SpriteSegmentPattern.IsMatch(segment))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                }

                string fullPath = System.IO.Path.Combine(SpritesDirectory, System.IO.Path.Combine(segments));

                // Belt and braces: even with the segment check above, the
                // resolved path is confirmed to still be inside the sprite
                // root before anything is read.
                string resolved = System.IO.Path.GetFullPath(fullPath);
                string root = System.IO.Path.GetFullPath(SpritesDirectory);
                if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(resolved))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var info = new System.IO.FileInfo(resolved);
                string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
                if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Close();
                    return;
                }

                byte[] payload = await System.IO.File.ReadAllBytesAsync(resolved);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "image/webp";
                context.Response.Headers["ETag"] = etag;
                // Art changes only on a deploy, and a screen can ask for fifty
                // of these at once, so a long cache matters more here than it
                // does for the ten sound effects.
                context.Response.Headers["Cache-Control"] = "public, max-age=604800";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sprite file error for '{relativePath}': {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Lists every sprite as a forward-slashed relative path, so the web
        // client's generator can build its lookup tables from what actually
        // shipped rather than from a checked-in list that drifts.
        private async Task HandleSpriteManifest(HttpListenerContext context)
        {
            try
            {
                var files = new System.Collections.Generic.List<string>();
                if (System.IO.Directory.Exists(SpritesDirectory))
                {
                    string root = System.IO.Path.GetFullPath(SpritesDirectory);
                    foreach (string path in System.IO.Directory.EnumerateFiles(root, "*.webp", System.IO.SearchOption.AllDirectories))
                    {
                        string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                        if (relative.Split('/').All(s => SpriteSegmentPattern.IsMatch(s))) files.Add(relative);
                    }
                }

                files.Sort(StringComparer.Ordinal);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GameDataManifestResponse { Files = files });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sprite manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleAudioManifest(HttpListenerContext context)
        {
            try
            {
                var files = new System.Collections.Generic.List<string>();
                if (System.IO.Directory.Exists(AudioDirectory))
                {
                    foreach (string path in System.IO.Directory.EnumerateFiles(AudioDirectory, "*.wav"))
                    {
                        string name = System.IO.Path.GetFileName(path);
                        if (AudioFileNamePattern.IsMatch(name)) files.Add(name);
                    }
                }

                files.Sort(StringComparer.Ordinal);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GameDataManifestResponse { Files = files });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGameDataManifest(HttpListenerContext context)
        {
            try
            {
                var files = new System.Collections.Generic.List<string>();
                if (System.IO.Directory.Exists(GameDataDirectory))
                {
                    foreach (string path in System.IO.Directory.EnumerateFiles(GameDataDirectory, "*.json"))
                    {
                        string name = System.IO.Path.GetFileName(path);
                        if (GameDataFileNamePattern.IsMatch(name) && !ServerOnlyGameDataFiles.Contains(name))
                        {
                            files.Add(name);
                        }
                    }
                }

                files.Sort(StringComparer.Ordinal);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GameDataManifestResponse { Files = files });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GameData manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
    }
}
