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
    static bool searchingPlacement;
    static int fieldGoalX, fieldGoalY;

    internal static void Reset()
    {
        cancellation?.Cancel();
        var source = cancellation;
        if (pending != null)
            _ = pending.ContinueWith(task => { _ = task.Exception; source?.Dispose(); }, TaskScheduler.Default);
        else source?.Dispose();
        pending = null; cancellation = null; plan = null; snapshot = anchor = null;
        searchingPlacement = false;
    }

    internal static bool Start(Player player, StateSpacePlanner.SSResult candidate, int goalX, int goalY)
    {
        if (Main.netMode != 0 || player.velocity != Vector2.Zero || player.itemAnimation > 0
            || player.itemTime > 0 || !player.releaseJump || candidate.Steps.Count != 1) return false;
        var step = candidate.Steps[0];
        if (step.Pillar && step.MineTiles?.Count is not > 0
            && step.TargetCy < ActExecutor.OriginCy(player))
            return StartPillar(player, candidate, goalX, goalY);
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

    static bool StartPillar(Player player, StateSpacePlanner.SSResult candidate, int goalX, int goalY)
    {
        int slot = NavCoordinator.FindPlatformSlot(player);
        if (slot < 0 || !VanillaTerrainPlan.HasSupport(player)) return false;
        try
        {
            requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            int x = (int)(player.Center.X / 16), y = (int)(player.Bottom.Y / 16);
            var area = Rectangle.Intersect(new Rectangle(x - 32, y - 64, 65, 81),
                new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
            snapshot = VanillaFrame.Capture(player, area, true);
            anchor = VanillaPrediction.Observe(player, false);
            plan = candidate; searchingPlacement = true; fieldGoalX = goalX; fieldGoalY = goalY;
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            pending = SearchPlacement(snapshot.DeepClone().AsObject(), slot, x, y,
                Math.Sign(candidate.Steps[0].TargetCx - x), cancellation.Token);
            DiagLog.Write($"[legacy-native-pillar] target=({candidate.GoalWx},{candidate.GoalWy})");
            return true;
        }
        catch (Exception error)
        {
            DiagLog.Write("[legacy-native-unavailable] " + error.Message);
            Reset(); return false;
        }
    }

    static async Task<JsonObject> SearchPlacement(JsonObject state, int slot, int x, int y, int direction, CancellationToken token)
    {
        var forecast = VanillaNavigationDemo.Instance;
        var proposal = await forecast.Observe(new JsonObject { ["command"] = "terrain_plan", ["snapshot"] = state.DeepClone(),
            ["jump_place"] = true, ["search_jump_place"] = true, ["tile_x"] = x, ["tile_y"] = y - 1,
            ["tile_type"] = Terraria.ID.TileID.Platforms, ["route_direction"] = direction }, token);
        if (proposal["status"]?.GetValue<string>() != "planned") return proposal;
        var replay = await forecast.Observe(new JsonObject { ["command"] = "placement_sequence", ["snapshot"] = state,
            ["native_use"] = true, ["legacy_use"] = true, ["place_slot"] = slot,
            ["tile_x"] = proposal["tile_x"].DeepClone(), ["tile_y"] = proposal["tile_y"].DeepClone(),
            ["place_after"] = proposal["place_after"].DeepClone(), ["controls"] = proposal["controls"].DeepClone() }, token);
        replay["proposal"] = proposal;
        return replay;
    }

    static StateSpacePlanner.SSResult PlacementResult(JsonObject result)
    {
        if (result["status"]?.GetValue<string>() != "complete" || result["placed"]?.GetValue<bool>() != true
            || result["supported"]?.GetValue<bool>() != true) return null;
        var samples = result["frames"].AsArray();
        if (samples.Count == 0 || samples.Any(frame => frame["dead"].GetValue<bool>()
            || frame["hp"].GetValue<int>() < anchor["hp"].GetValue<int>())) return null;
        var last = samples[^1];
        var cell = StateSpacePlanner.StandCell(last["x"].GetValue<float>(), last["y"].GetValue<float>());
        var field = MazeWand.PeekFieldOrNull(fieldGoalX, fieldGoalY);
        if (field == null || !field.TryGetValue(cell, out int cost) || cost >= plan.CurH) return null;
        var proposal = result["proposal"];
        int placeAt = proposal["place_after"].GetValue<int>();
        var controls = proposal["controls"].AsArray();
        if (controls.Count != samples.Count) return null;
        var frames = new System.Collections.Generic.List<PhysicsSimulator.ControlInput>();
        int frameCount = SettledPlacementLength(samples, controls, placeAt);
        for (int i = 0; i < frameCount; i++)
        {
            int keys = controls[i].GetValue<int>();
            frames.Add(new PhysicsSimulator.ControlInput { Left = (keys & 1) != 0, Right = (keys & 2) != 0,
                Jump = (keys & 4) != 0, Down = (keys & 16) != 0,
                Px = samples[i]["x"].GetValue<float>(), Py = samples[i]["y"].GetValue<float>(),
                Vx = samples[i]["vx"].GetValue<float>(), Vy = samples[i]["vy"].GetValue<float>(),
                Place = i == placeAt, PlaceCx = proposal["tile_x"].GetValue<int>(), PlaceCy = proposal["tile_y"].GetValue<int>() });
        }
        var replacement = new StateSpacePlanner.SSResult { Found = true, StartPx = anchor["x"].GetValue<float>(),
            StartPy = anchor["y"].GetValue<float>(), GoalWx = cell.Item1, GoalWy = cell.Item2,
            CurH = plan.CurH, Altered = 1, CostFrames = frames.Count, ExecFrames = frames, NativeVerified = true };
        replacement.Steps.Add(new StateSpacePlanner.ExecStep { TargetCx = cell.Item1, TargetCy = cell.Item2,
            Frames = frames, LandPx = frames[^1].Px, LandPy = frames[^1].Py });
        return replacement;
    }

    static int SettledPlacementLength(JsonArray samples, JsonArray controls, int placeAt)
    {
        int count = samples.Count;
        while (count > placeAt + 2 && controls[count - 1].GetValue<int>() == 0
            && controls[count - 2].GetValue<int>() == 0)
        {
            var last = samples[count - 1];
            var previous = samples[count - 2];
            if (last["vx"].GetValue<float>() != 0 || last["vy"].GetValue<float>() != 0
                || previous["vx"].GetValue<float>() != 0 || previous["vy"].GetValue<float>() != 0
                || last["x"].GetValue<float>() != previous["x"].GetValue<float>()
                || last["y"].GetValue<float>() != previous["y"].GetValue<float>()) break;
            count--;
        }
        return count;
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
            if (searchingPlacement)
            {
                var replacement = PlacementResult(result);
                if (replacement != null && DiagLog.Trace)
                {
                    string directory = System.IO.Path.Combine(Main.SavePath, "NavSnapshots");
                    System.IO.Directory.CreateDirectory(directory);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(directory, $"native-placement-{Main.GameUpdateCount}.json"),
                        new JsonObject { ["snapshot"] = snapshot.DeepClone(), ["result"] = result.DeepClone(),
                            ["goal_x"] = fieldGoalX, ["goal_y"] = fieldGoalY }.ToJsonString());
                }
                ready = replacement ?? plan;
                DiagLog.Write($"[legacy-native-pillar-result] replaced={replacement != null} status={result["status"]} total_ms={System.Diagnostics.Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:0.###}");
                return true;
            }
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
            plan.NativeVerified = true;
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
