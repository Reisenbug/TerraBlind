using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

internal static class LegacyMovementLookahead
{
    static Task<JsonObject> pending;
    static CancellationTokenSource cancellation;
    static JsonObject context;
    static long started;

    internal static void Reset()
    {
        cancellation?.Cancel();
        var source = cancellation;
        if (pending != null)
            _ = pending.ContinueWith(task => { _ = task.Exception; source?.Dispose(); }, TaskScheduler.Default);
        else source?.Dispose();
        pending = null; cancellation = null; context = null;
    }

    internal static void Start(Player player, StateSpacePlanner.SSResult current, int goalX, int goalY)
    {
        Reset();
        if (!VanillaNavigationDemo.Instance.WorkerReady || Main.netMode != 0
            || current.Steps.Count != 1 || current.Altered != 0
            || Math.Abs(player.Center.X / 16 - goalX) > 120
            || Math.Abs(player.Bottom.Y / 16 - goalY) > 120) return;
        try { StartCore(player, current, goalX, goalY); }
        catch (Exception error) { Reset(); DiagLog.Write("[recede-lookahead-miss] " + error.Message); }
    }

    static void StartCore(Player player, StateSpacePlanner.SSResult current, int goalX, int goalY)
    {
        var frames = current.Steps[0].Frames;
        if (frames == null || frames.Count < 30 || frames.Any(frame => frame.Place)) return;
        var field = MazeWand.PeekFieldOrNull(goalX, goalY);
        if (field == null) return;
        var goal = new Vector2(goalX * 16 + 8 - player.width / 2f, (goalY + 1) * 16 - player.height);
        var area = VanillaLocalPlanner.SnapshotArea(player, goal);
        if (frames.Any(frame => !area.Contains((int)(frame.Px / 16), (int)(frame.Py / 16))
            || !area.Contains((int)((frame.Px + player.width) / 16), (int)((frame.Py + player.height) / 16)))) return;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        context = VanillaFrame.Capture(player, area, true);
        var guidance = new List<int>();
        for (int x = area.Left; x < area.Right; x++)
        for (int y = area.Top; y < area.Bottom; y++)
            if (field.TryGetValue((x, y), out int cost)) { guidance.Add(x); guidance.Add(y); guidance.Add(cost); }
        var prefix = new JsonArray(frames.Select(frame => JsonValue.Create(
            (frame.Left ? 1 : 0) | (frame.Right ? 2 : 0) | (frame.Jump ? 4 : 0) | (frame.Down ? 16 : 0))).ToArray());
        var request = new JsonObject { ["command"] = "plan", ["snapshot"] = context.DeepClone(),
            ["prefix"] = prefix, ["x"] = goal.X, ["y"] = goal.Y,
            ["guidance"] = VanillaFrame.PackInts(guidance.ToArray()), ["terminal"] = false };
        cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        pending = VanillaNavigationDemo.Instance.Observe(request, cancellation.Token);
        DiagLog.Write($"[recede-lookahead-start] prefix={frames.Count} capture_ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
    }

    internal static StateSpacePlanner.SSResult Take(Player player, int goalX, int goalY)
    {
        if (pending == null) return null;
        try
        {
            if (!pending.IsCompleted)
            { DiagLog.Write($"[recede-lookahead-miss] not_ready elapsed_ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}"); return null; }
            var result = pending.GetAwaiter().GetResult();
            if (result["status"]?.GetValue<string>() != "planned"
                || result["anchor"] is not JsonObject anchor)
            { DiagLog.Write("[recede-lookahead-miss] no_plan"); return null; }
            if (!VanillaAsyncMovement.StateMatches(player, anchor, true, out var reason, out var mismatch))
            { DiagLog.Write($"[recede-lookahead-miss] {reason} {mismatch}"); return null; }
            var samples = result["forecast"]?["frames"] as JsonArray;
            var controls = result["controls"] as JsonArray;
            if (result["forecast"]?["outcome"]?.GetValue<string>() != "complete"
                || samples == null || samples.Count == 0 || controls?.Count != samples.Count
                || player.velocity.Y != 0 || controls.Any(key => (key.GetValue<int>() & ~23) != 0)
                || !VanillaFrame.ContextMatches(context, player, out _, samples)
                || samples.Any(frame => frame["dead"].GetValue<bool>()
                    || frame["hp"].GetValue<int>() < anchor["hp"].GetValue<int>())) return null;
            var last = samples[^1];
            var from = StateSpacePlanner.StandCell(player.position.X, player.position.Y);
            var cell = StateSpacePlanner.StandCell(last["x"].GetValue<float>(), last["y"].GetValue<float>());
            var field = MazeWand.PeekFieldOrNull(goalX, goalY);
            if (field == null || MazeWand.FieldPickStale() || !field.TryGetValue(from, out int before)
                || !field.TryGetValue(cell, out int after) || after >= before) return null;
            var frames = new List<PhysicsSimulator.ControlInput>();
            for (int i = 0; i < samples.Count; i++)
            {
                int keys = controls[i].GetValue<int>();
                frames.Add(new PhysicsSimulator.ControlInput { Left = (keys & 1) != 0, Right = (keys & 2) != 0,
                    Jump = (keys & 4) != 0, Down = (keys & 16) != 0,
                    Px = samples[i]["x"].GetValue<float>(), Py = samples[i]["y"].GetValue<float>(),
                    Vx = samples[i]["vx"].GetValue<float>(), Vy = samples[i]["vy"].GetValue<float>() });
            }
            var next = new StateSpacePlanner.SSResult { Found = true, NativeVerified = true,
                StartPx = player.position.X, StartPy = player.position.Y, CurH = before,
                GoalWx = cell.Item1, GoalWy = cell.Item2, CostFrames = frames.Count, ExecFrames = frames };
            next.Steps.Add(new StateSpacePlanner.ExecStep { TargetCx = cell.Item1, TargetCy = cell.Item2,
                Frames = frames, LandPx = frames[^1].Px, LandPy = frames[^1].Py });
            DiagLog.Write($"[recede-lookahead-hit] frames={frames.Count} total_ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###} compute_ms={result["request_compute_ms"]}");
            return next;
        }
        catch (Exception error) { DiagLog.Write("[recede-lookahead-miss] " + error.Message); return null; }
        finally { Reset(); }
    }
}
