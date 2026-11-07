using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Network
{
    // Build presets (owner, 2026-11-07): up to three saved builds - the four
    // attributes and the War half of the skill tree - under
    // /api/v1/skilltree/presets*. The tree is applied here, through the same
    // respec and commit the skill tree screen uses; the attributes are applied
    // by the client with the ordinary respec and spend commands, because the
    // tick owns them (AttributeTickCoordinator). See BuildPreset.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>GET: the player's saved builds.</summary>
        private async Task HandleBuildPresetList(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                var presets = await new BuildPresetEngine(_serviceProvider, _playerSessionRegistry).ListAsync(playerId);
                await WriteJsonAsync(context, new { MaxSlots = BuildPresetRules.MaxSlots, Presets = presets });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Build preset list error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>POST { Slot, Name, STR, DEX, CON, LCK }: saves the current build under the slot.</summary>
        private async Task HandleBuildPresetSave(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int slot, str, dex, con, lck;
                string? name;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    var root = parsed.RootElement;
                    slot = IntOf(root, "Slot");
                    str = IntOf(root, "STR");
                    dex = IntOf(root, "DEX");
                    con = IntOf(root, "CON");
                    lck = IntOf(root, "LCK");
                    name = root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                }
                catch (JsonException) { context.Response.StatusCode = 400; return; }
                if (!BuildPresetRules.IsSlot(slot)) { context.Response.StatusCode = 400; return; }

                var saved = await new BuildPresetEngine(_serviceProvider, _playerSessionRegistry).SaveAsync(playerId, slot, name, str, dex, con, lck);
                await WriteJsonAsync(context, new { Preset = saved });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Build preset save error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>POST { Slot }: empties the slot.</summary>
        private async Task HandleBuildPresetDelete(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                int slot;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    slot = IntOf(parsed.RootElement, "Slot");
                }
                catch (JsonException) { context.Response.StatusCode = 400; return; }
                if (!BuildPresetRules.IsSlot(slot)) { context.Response.StatusCode = 400; return; }

                bool removed = await new BuildPresetEngine(_serviceProvider, _playerSessionRegistry).DeleteAsync(playerId, slot);
                await WriteJsonAsync(context, new { Removed = removed });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Build preset delete error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        /// <summary>
        /// POST { Slot, Gold }: makes the War half of the tree the preset's. A
        /// refusal answers 200 with its Reason, like the commit and the respec.
        /// </summary>
        private async Task HandleBuildPresetApply(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                int slot;
                bool gold;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    slot = IntOf(parsed.RootElement, "Slot");
                    gold = parsed.RootElement.TryGetProperty("Gold", out var g) && g.ValueKind == JsonValueKind.True;
                }
                catch (JsonException) { context.Response.StatusCode = 400; return; }
                if (!BuildPresetRules.IsSlot(slot)) { context.Response.StatusCode = 400; return; }

                var result = await new BuildPresetEngine(_serviceProvider, _playerSessionRegistry).ApplyTreeAsync(playerId, slot, gold);
                await WriteJsonAsync(context, result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Build preset apply error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }

        private static int IntOf(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;
    }
}
