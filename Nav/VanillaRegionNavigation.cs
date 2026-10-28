using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

internal sealed class VanillaRegionNavigation : IDisposable
{
    readonly Func<JsonObject, CancellationToken, Task<JsonObject>> send;
    CancellationTokenSource cancellation;
    Task<JsonObject> pending;
    JsonObject requested, active;
    VanillaPrediction.RegionPlayback playback;
    Rectangle? expandedArea;
    VanillaKeys walkKeys;
    long walkTick = -1;
    bool settlingWalk;
    public string Status { get; private set; } = "准备区域寻路";
    public int Accepted { get; private set; }
    public int Rejected { get; private set; }
    public JsonObject Result => active;
    public VanillaRegionNavigation(Func<JsonObject, CancellationToken, Task<JsonObject>> send) => this.send = send;
    public bool HasInput(long tick) => walkTick == tick || playback?.HasInput(tick) == true;
    public int Index(long tick) => playback?.Index(tick) ?? -1;
    public VanillaPrediction.Input InputAt(long tick) => walkTick == tick ? new(walkKeys) : playback?.At(tick) ?? new(VanillaKeys.None);

    void Cancel()
    {
        var source = cancellation;
        cancellation = null;
        source?.Cancel();
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; source?.Dispose(); }, TaskScheduler.Default);
        else source?.Dispose();
        pending = null; requested = null;
    }
    public void Dispose() { Cancel(); playback = null; active = null; walkTick = -1; settlingWalk = false; }

    void Request(JsonObject snapshot)
    {
        var p = snapshot["player"];
        float x = p["Terraria.Entity:position"]["Microsoft.Xna.Framework.Vector2:X"].GetValue<float>();
        float y = p["Terraria.Entity:position"]["Microsoft.Xna.Framework.Vector2:Y"].GetValue<float>();
        var bounds = snapshot["area"];
        var region = Rectangle.Intersect(new Rectangle((int)Math.Floor(x) - 160, (int)Math.Floor(y) - 192, 320, 384),
            new Rectangle(bounds[0].GetValue<int>() * 16, bounds[1].GetValue<int>() * 16,
                bounds[2].GetValue<int>() * 16, bounds[3].GetValue<int>() * 16));
        requested = snapshot;
        cancellation = new CancellationTokenSource();
        var request = new JsonObject { ["command"] = "region_plan", ["snapshot"] = snapshot.DeepClone(),
            ["guidance"] = VanillaNavBridge.CaptureGuidance(snapshot),
            ["region"] = new JsonArray(region.X, region.Y, region.Width, region.Height),
            ["terrain_motion"] = true,
            ["horizon"] = 180, ["beam"] = 6, ["quantum"] = 12 };
        pending = send(request, cancellation.Token);
    }

    public void Tick(Player player, long tick, Vector2 goal)
    {
        if (playback != null && !playback.Complete(tick - 1) && !playback.Matches(player, tick, out string difference))
        {
            DiagLog.Write("[region-diverged] " + difference);
            Rejected++; Dispose(); Status = "实际状态偏离，重新规划";
        }
        bool executing = playback != null && !playback.Complete(tick);
        bool wasWalking = walkTick == tick;
        walkTick = -1;
        if (!executing && VanillaContactRegion.TryWalk(player, goal, out walkKeys))
        {
            if (pending != null) Cancel();
            playback = null; active = null; walkTick = tick + 1;
            settlingWalk = false;
            Status = "沿安全平地连续前进";
            if (!wasWalking) DiagLog.Write("[region-walk-start] no_worker_request=true");
            return;
        }
        if (wasWalking) { settlingWalk = true; DiagLog.Write("[region-walk-end] terrain_or_goal_requires_planning=true"); }
        if (settlingWalk)
        {
            if (Math.Abs(player.velocity.X) > .01f) { Status = "障碍前减速，准备局部推演"; return; }
            settlingWalk = false;
        }
        if (pending?.IsCompleted == true && !executing)
        {
            var result = pending.GetAwaiter().GetResult();
            pending = null; cancellation.Dispose(); cancellation = null;
            string status = result["status"]?.GetValue<string>();
            DiagLog.Write($"[region-result] status={status} compute_ms={result["planning_wall_ms"]} roundtrip_ms={result["roundtrip_ms"]}");
            if (status == "snapshot_required")
            {
                var a = requested["area"];
                var tile = result["missing_tile"];
                expandedArea = Rectangle.Union(new Rectangle(a[0].GetValue<int>(), a[1].GetValue<int>(),
                    a[2].GetValue<int>(), a[3].GetValue<int>()),
                    new Rectangle(tile[0].GetValue<int>() - 16, tile[1].GetValue<int>() - 16, 33, 33));
            }
            else if (status == "reached")
            {
                var candidate = new VanillaPrediction.RegionPlayback(result, tick);
                if (candidate.Matches(player, tick, out string mismatch)
                    && VanillaFrame.ContextMatches(requested, player, out mismatch, result["frames"].AsArray()))
                {
                    playback = candidate; active = result; Accepted++;
                    Status = "执行区域路线，准备下一段";
                    var end = result["end_snapshot"].AsObject();
                    var position = end["player"]["Terraria.Entity:position"];
                    var destination = new Vector2(position["Microsoft.Xna.Framework.Vector2:X"].GetValue<float>(),
                        position["Microsoft.Xna.Framework.Vector2:Y"].GetValue<float>());
                    var a = end["area"];
                    var observed = new Rectangle(a[0].GetValue<int>() * 16, a[1].GetValue<int>() * 16,
                        a[2].GetValue<int>() * 16, a[3].GetValue<int>() * 16);
                    var needed = new Rectangle((int)Math.Floor(destination.X) - 160,
                        (int)Math.Floor(destination.Y) - 192, 320, 384);
                    var next = end;
                    if (!observed.Contains(needed))
                    {
                        long started = System.Diagnostics.Stopwatch.GetTimestamp();
                        next = VanillaFrame.RefreshFutureTerrain(player, requested, end,
                            VanillaLocalPlanner.SnapshotArea(player, destination));
                        DiagLog.Write($"[region-observation] area={next["area"]} refresh_ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
                    }
                    Request(next);
                    return;
                }
                DiagLog.Write("[region-rejected] " + mismatch);
                Rejected++;
            }
            else throw new InvalidOperationException("region_no_route:" + status);
            playback = null; active = null;
        }
        if (pending != null) { Status = executing ? "执行区域路线，准备下一段" : "等待区域规划"; return; }
        if (executing) return;
        playback = null; active = null;
        var area = VanillaLocalPlanner.SnapshotArea(player, goal);
        if (expandedArea is Rectangle expanded)
        {
            area = Rectangle.Intersect(Rectangle.Union(area, expanded), new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
            expandedArea = null;
        }
        if (area.Width > 256 || area.Height > 256) throw new InvalidOperationException("region_snapshot_size_limit");
        Request(VanillaFrame.Capture(player, area, true));
        Status = "等待区域规划";
    }
}
