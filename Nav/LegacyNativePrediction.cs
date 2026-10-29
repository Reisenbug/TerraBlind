using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

internal static class LegacyNativePrediction
{
    static Task<JsonObject> pending;
    static CancellationTokenSource cancellation;
    static StateSpacePlanner.SSResult plan;
    static JsonObject snapshot, anchor;
    static long requestedAt;

    internal static void Reset()
    {
        cancellation?.Cancel();
        var source = cancellation;
        if (pending != null)
            _ = pending.ContinueWith(task => { _ = task.Exception; source?.Dispose(); }, TaskScheduler.Default);
        else source?.Dispose();
        pending = null; cancellation = null; plan = null; snapshot = anchor = null;
    }

    internal static bool Start(Player player, StateSpacePlanner.SSResult candidate)
    {
        if (Main.netMode != 0 || player.velocity != Vector2.Zero || player.itemAnimation > 0
            || player.itemTime > 0 || !player.releaseJump || candidate.Steps.Count != 1) return false;
        var step = candidate.Steps[0];
        var frames = step.Frames;
        if (step.Dig || step.Pillar || step.Bridge || frames == null || frames.Count is < 1 or > 240) return false;
        var placements = frames.Where(frame => frame.Place).Select(frame => new Point(frame.PlaceCx, frame.PlaceCy)).Distinct().ToArray();
        if (placements.Length > 1) return false;
        if (placements.Length == 1 && frames[0].Place) return false;
        int placeSlot = placements.Length == 0 ? -1 : NavCoordinator.FindPlatformSlot(player);
        if (placements.Length > 0 && placeSlot < 0) return false;
        try
        {
            requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            float minX = Math.Min(player.position.X, frames.Min(frame => frame.Px));
            float minY = Math.Min(player.position.Y, frames.Min(frame => frame.Py));
            float maxX = Math.Max(player.Right.X, frames.Max(frame => frame.Px) + player.width);
            float maxY = Math.Max(player.Bottom.Y, frames.Max(frame => frame.Py) + player.height);
            var area = Rectangle.Intersect(new Rectangle((int)Math.Floor(minX / 16) - 8,
                (int)Math.Floor(minY / 16) - 8, (int)Math.Ceiling((maxX - minX) / 16) + 18,
                (int)Math.Ceiling((maxY - minY) / 16) + 18), new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
            if (area.Width > 256 || area.Height > 256) return false;
            snapshot = VanillaFrame.Capture(player, area, true);
            anchor = VanillaPrediction.Observe(player, false);
            plan = candidate;
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var request = new JsonObject {
                ["command"] = "replay", ["snapshot"] = snapshot.DeepClone(), ["detailed"] = false, ["stop_on_damage"] = true,
                ["controls"] = new JsonArray(frames.Select(frame => JsonValue.Create(
                    (frame.Left ? 1 : 0) | (frame.Right ? 2 : 0) | (frame.Jump ? 4 : 0) | (frame.Down ? 16 : 0))).ToArray())
            };
            if (placements.Length == 1)
            {
                request["command"] = "placement_sequence";
                request["native_use"] = true; request["legacy_use"] = true;
                request["place_after"] = frames.FindIndex(frame => frame.Place);
                request["place_slot"] = placeSlot;
                request["tile_x"] = placements[0].X; request["tile_y"] = placements[0].Y;
            }
            pending = VanillaNavigationDemo.Instance.Observe(request, cancellation.Token);
            DiagLog.Write($"[legacy-native-start] frames={frames.Count} placement={placements.Length == 1} target=({candidate.GoalWx},{candidate.GoalWy})");
            return true;
        }
        catch (Exception error)
        {
            DiagLog.Write("[legacy-native-unavailable] " + error.Message);
            Reset(); return false;
        }
    }

    internal static bool Poll(Player player, out StateSpacePlanner.SSResult ready)
    {
        ready = null;
        if (pending == null) return false;
        if (!pending.IsCompleted) return true;
        try
        {
            if (!VanillaAsyncMovement.StateMatches(player, anchor, true, out _, out _)
                || !VanillaFrame.ContextMatches(snapshot, player))
            {
                DiagLog.Write("[legacy-native-discard] start_or_terrain_changed");
                return true;
            }
            var result = pending.GetAwaiter().GetResult();
            var samples = result["frames"].AsArray();
            var frames = plan.Steps[0].Frames;
            var last = samples.Count > 0 ? samples[^1] : null;
            string outcome = (result["outcome"] ?? result["status"])?.GetValue<string>();
            bool valid = outcome == "complete" && samples.Count == frames.Count
                && (result["placed"] == null || result["placed"].GetValue<bool>() && result["supported"].GetValue<bool>())
                && samples.All(frame => frame["hp"].GetValue<int>() >= anchor["hp"].GetValue<int>())
                && !last["dead"].GetValue<bool>()
                && Math.Abs(last["x"].GetValue<float>() - frames[^1].Px) <= 16
                && Math.Abs(last["y"].GetValue<float>() - frames[^1].Py) <= 16;
            if (!valid)
            {
                var from = StateSpacePlanner.StandCell(player.position.X, player.position.Y);
                StateSpacePlanner.PenalizeEdges(new[] { (from.Item1, from.Item2, plan.GoalWx, plan.GoalWy) }, 200f);
                DiagLog.Write("[legacy-native-rejected] outcome=" + outcome);
                return true;
            }
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                frame.Px = samples[i]["x"].GetValue<float>(); frame.Py = samples[i]["y"].GetValue<float>();
                frame.Vx = samples[i]["vx"].GetValue<float>(); frame.Vy = samples[i]["vy"].GetValue<float>();
                frames[i] = frame;
            }
            plan.StartPx = player.position.X; plan.StartPy = player.position.Y;
            plan.Steps[0].LandPx = frames[^1].Px; plan.Steps[0].LandPy = frames[^1].Py;
            ready = plan;
            DiagLog.Write($"[legacy-native-accepted] frames={frames.Count} compute_ms={result["elapsed_ms"]} total_ms={System.Diagnostics.Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:0.###}");
        }
        catch (Exception error)
        {
            DiagLog.Write("[legacy-native-fallback] " + error.Message);
            ready = plan;
        }
        finally { Reset(); }
        return true;
    }
}
