using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

internal static class NativeMovementPlanner
{
    static Task<JsonObject> pending;
    static CancellationTokenSource cancellation;
    static JsonObject snapshot, anchor;
    static JsonObject request;
    static long started;
    static int initialCost;
    static Dictionary<(int, int), int> activeField;
    static JsonObject nextRequest, nextAnchor;
    static Dictionary<(int, int), int> nextField;
    static bool prefetched;

    internal static void Reset()
    {
        ClearPending();
        nextRequest = nextAnchor = null; nextField = null;
    }

    static void ClearPending()
    {
        cancellation?.Cancel();
        var source = cancellation;
        if (pending != null)
            _ = pending.ContinueWith(task => { _ = task.Exception; source?.Dispose(); }, TaskScheduler.Default);
        else source?.Dispose();
        pending = null; cancellation = null; snapshot = anchor = request = null; activeField = null;
        prefetched = false;
    }

    internal static void Prefetch()
    {
        if (nextRequest == null || pending != null) return;
        request = nextRequest; anchor = nextAnchor; activeField = nextField;
        nextRequest = nextAnchor = null; nextField = null;
        snapshot = request["snapshot"].AsObject();
        var cell = VanillaLocalPlanner.FootCell(new Vector2(anchor["x"].GetValue<float>(), anchor["y"].GetValue<float>()),
            Main.LocalPlayer.width, Main.LocalPlayer.height);
        if (!activeField.TryGetValue(cell, out initialCost)) { ClearPending(); return; }
        started = Stopwatch.GetTimestamp(); prefetched = true;
        cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        pending = VanillaNavigationDemo.Instance.Observe(request, cancellation.Token);
        DiagLog.Write($"[native-prefetch] at={cell} current_action_running={StateSpacePlanner.ExecRunning}");
    }

    internal static void Start(Player player, int goalX, int goalY)
    {
        var field = MazeWand.PeekFieldOrNull(goalX, goalY)
            ?? throw new InvalidOperationException("native_movement_missing_field");
        activeField = field;
        var cell = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
        var standing = StateSpacePlanner.StandCell(player.position.X, player.position.Y);
        if (!field.TryGetValue(cell, out initialCost) && !field.TryGetValue(standing, out initialCost))
            throw new InvalidOperationException("native_movement_start_outside_field");
        var area = Rectangle.Intersect(new Rectangle(cell.x - 80, cell.y - 32, 161, 256),
            new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
        var guidance = new List<int>();
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
            if (field.TryGetValue((x, y), out int cost))
            { guidance.Add(x); guidance.Add(y); guidance.Add(cost); }
        if (!field.ContainsKey(cell))
        { guidance.Add(cell.x); guidance.Add(cell.y); guidance.Add(initialCost); }
        started = Stopwatch.GetTimestamp();
        snapshot = VanillaFrame.Capture(player, area, true);
        anchor = VanillaPrediction.Observe(player, false);
        cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        request = new JsonObject {
            ["command"] = "native_movement", ["snapshot"] = snapshot.DeepClone(),
            ["x"] = goalX * 16, ["y"] = (goalY + 1) * 16 - player.height,
            ["guidance"] = VanillaFrame.PackInts(guidance.ToArray())
        };
        pending = VanillaNavigationDemo.Instance.Observe(request, cancellation.Token);
        DiagLog.Write($"[native-movement-start] at={cell} field_cells={guidance.Count / 3}");
    }

    internal static bool Poll(Player player, out StateSpacePlanner.SSResult ready, out string status)
    {
        ready = null; status = null;
        if (pending == null) return false;
        if (prefetched && StateSpacePlanner.ExecRunning && !StateSpacePlanner.NativeReplayComplete) return false;
        if (!pending.IsCompleted) return true;
        try
        {
            if (!VanillaAsyncMovement.StateMatches(player, anchor, true, out _, out _)
                || !VanillaFrame.ContextMatches(snapshot, player))
            { status = "stale"; DiagLog.Write($"[native-movement-stale] prefetched={prefetched}"); return true; }
            var result = pending.GetAwaiter().GetResult();
            status = result["status"].GetValue<string>();
            DiagLog.Write($"[native-movement-result] status={status} compute_ms={result["elapsed_ms"]} total_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
            if (status != "planned")
            {
                var path = System.IO.Path.Combine(Main.SavePath, "native-movement-failure.json");
                System.IO.File.WriteAllText(path, new JsonObject { ["request"] = request.DeepClone(), ["result"] = result.DeepClone() }.ToJsonString());
                return true;
            }
            var samples = result["forecast"]["frames"].AsArray();
            var controls = result["controls"].AsArray();
            var frames = new List<PhysicsSimulator.ControlInput>();
            for (int i = 0; i < samples.Count; i++)
            {
                int key = controls[i].GetValue<int>();
                var frame = samples[i];
                frames.Add(new PhysicsSimulator.ControlInput { Left = (key & 1) != 0, Right = (key & 2) != 0,
                    Jump = (key & 4) != 0, Down = (key & 16) != 0,
                    Px = frame["x"].GetValue<float>(), Py = frame["y"].GetValue<float>(),
                    Vx = frame["vx"].GetValue<float>(), Vy = frame["vy"].GetValue<float>() });
            }
            var last = frames[^1];
            var cell = StateSpacePlanner.StandCell(last.Px, last.Py);
            var raw = VanillaLocalPlanner.FootCell(new Vector2(last.Px, last.Py), player.width, player.height);
            DiagLog.Write($"[native-movement-cost] initial={initialCost} raw={raw}:{activeField.GetValueOrDefault(raw, -1)} standing={cell}:{activeField.GetValueOrDefault(cell, -1)} end=({last.Px},{last.Py})");
            ready = new StateSpacePlanner.SSResult { Found = true, NativeVerified = true,
                StartPx = anchor["x"].GetValue<float>(), StartPy = anchor["y"].GetValue<float>(),
                GoalWx = cell.Item1, GoalWy = cell.Item2, CurH = initialCost, CostFrames = frames.Count, ExecFrames = frames };
            ready.Steps.Add(new StateSpacePlanner.ExecStep { TargetCx = cell.Item1, TargetCy = cell.Item2,
                Frames = frames, LandPx = last.Px, LandPy = last.Py });
            nextRequest = request.DeepClone().AsObject();
            nextRequest["snapshot"] = result["forecast"]["end_snapshot"].DeepClone();
            nextAnchor = samples[^1].DeepClone().AsObject();
            nextField = activeField;
            DiagLog.Write($"[native-handoff] prefetched={prefetched} end_vx={last.Vx} frames={frames.Count}");
        }
        catch (Exception error) { status = "failed"; DiagLog.Write("[native-movement-error] " + error); }
        finally { ClearPending(); }
        return true;
    }
}
