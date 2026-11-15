using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class VanillaNavigationDemo : ModSystem
{
    public readonly record struct ControlFrame(int Keys, bool Queued, string Plan, int Index,
        bool Terminal, Vector2 Goal, Vector2 PlanGoal, Vector2 Anchor);
    public ControlFrame LastControl { get; private set; }
    public static VanillaNavigationDemo Instance => ModContent.GetInstance<VanillaNavigationDemo>();
    public static bool Active => Instance.running;
    public string Status { get; private set; } = "未启动";
    public bool Reached { get; private set; }
    public bool NavigationOwned { get; private set; }
    public int RoutePoints => route.Length;
    public string PlannerStatus => regions?.Status ?? movement?.Status ?? Status;
    public JsonObject PlannerResult => regions?.Result ?? movement?.ActiveResult;
    internal bool HasQueuedMovement => regions?.HasInput(tick + 1) ?? movement?.HasInputAt(tick + 1) == true;
    public string PlannerRejection => movement?.LastRejection;
    public string PlannerMismatch => movement?.LastMismatch;
    public int PlanAccepted => regions?.Accepted ?? movement?.Accepted ?? 0;
    public int PlanRejected => regions?.Rejected ?? movement?.Rejected ?? 0;
    public bool PlannerReady => movement != null;
    internal bool WorkerReady => worker?.Alive == true && workerWarmed;
    bool workerWarmed;
    Task<JsonObject> warmup;
    bool terrainPaused;
    bool running;
    long tick;
    Vector2 goal;
    Vector2[] route = Array.Empty<Vector2>();
    public bool ContactDescentLanded => movement?.ContactDescentLanded == true;
    JsonObject recordedDivergence;
    JsonObject loggedRepairResult;
    CancellationTokenSource cancellation;
    Task<VanillaWorkerConnection> startup;
    VanillaWorkerConnection worker;
    VanillaAsyncMovement movement;
    VanillaRegionNavigation regions;
    JsonObject drawnRegion;
    int responseFrames;
    CancellationTokenSource terrainCancellation;
    Task<JsonObject> terrainRequest;
    int previousAccepted;
    int stalled;
    float closest;
    double captureMs;
    double requestMs;
    long captureBytes, requestBytes;
    int loggedAccepted, loggedRejected, idleFrames;
    int starvedFrames, starvation, longestStarvation;
    readonly System.Collections.Generic.HashSet<(int, int)> recordedObstructions = new();

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Player.ResetControls += ResetInput;
        On_TriggersSet.CopyInto += CopyInput;
    }

    public void Start(Vector2 target) => StartCore(target, false);
    internal void StartNavigation(Vector2 target) => StartCore(target, true);
    internal void UpdateNavigationGoal(Vector2 target) { goal = target; closest = Vector2.Distance(Main.LocalPlayer.position, goal); }
    internal void PauseForTerrain()
    {
        movement?.CancelPending();
        terrainPaused = true; movement = null; Status = "推演地形动作";
    }
    internal void CancelTerrain()
    {
        var cancelled = terrainCancellation; terrainCancellation = null;
        cancelled?.Cancel();
        if (terrainRequest != null)
            _ = terrainRequest.ContinueWith(task => { _ = task.Exception; cancelled?.Dispose(); }, TaskScheduler.Default);
        else cancelled?.Dispose();
        terrainRequest = null;
    }
    internal Task<JsonObject> PrepareTerrain(JsonObject request)
    {
        CancelTerrain();
        terrainCancellation = new CancellationTokenSource();
        var token = terrainCancellation.Token;
        return terrainRequest = Task.Run(() => worker.Send(request, token));
    }
    internal void TerrainChanged(int x, int y) => movement?.TerrainChanged(x, y, Main.LocalPlayer, tick);
    internal bool ResumeTerrain(JsonObject result)
    {
        movement = ConnectMovement(worker);
        if (!movement.InstallTerrain(result, Main.LocalPlayer, tick, goal)) return false;
        terrainPaused = false; Status = "执行地形改变后的引擎轨迹";
        ApplyInput(Main.LocalPlayer, false);
        return true;
    }
    internal void ReplanAfterTerrain()
    {
        movement = ConnectMovement(worker);
        terrainPaused = false; Status = "地形已变化，重新推演";
    }

    void StartCore(Vector2 target, bool navigation)
    {
        var player = Main.LocalPlayer;
        if (Main.netMode != NetmodeID.SinglePlayer || Main.ActiveWorldFileData.IsCloudSave)
            throw new InvalidOperationException("目前是单人本地世界的原版移动实验入口。");
        if (player.dead || player.ghost) throw new InvalidOperationException("请先复活。");
        if (Vector2.Distance(player.position, target) > 60 * 16) throw new InvalidOperationException("请选60格以内的落脚点。");
        VanillaFrame.Capture(player, VanillaLocalPlanner.SnapshotArea(player, target), navigation);
        if (!navigation) { Halt.All(); NativeAgent.Stop(); }
        if (!navigation && !AxisLock.Take("vanilla-demo", Ax.Move | Ax.Jump | Ax.Vertical | Ax.Use, () => running))
            throw new InvalidOperationException("人物控制权被其他动作占用。");
        running = true; NavigationOwned = navigation; terrainPaused = false; Reached = false; goal = target; tick = 0; stalled = 0;
        regions?.Dispose();
        regions = navigation && VanillaNavBridge.RegionsEnabled
            ? new VanillaRegionNavigation((request, token) => Task.Run(() => worker.Send(request, token))) : null;
        closest = Vector2.Distance(player.position, goal); previousAccepted = 0;
        loggedAccepted = loggedRejected = idleFrames = starvedFrames = starvation = longestStarvation = 0;
        route = Array.Empty<Vector2>();
        recordedObstructions.Clear();
        EnsureWorker();
        if (worker?.Alive == true)
        {
            movement = regions == null ? ConnectMovement(worker) : null; Status = "推演中"; return;
        }
        Status = "正在启动独立推演进程……";
    }

    void EnsureWorker()
    {
        if (worker?.Alive == true || startup != null) return;
        worker?.Dispose(); worker = null;
        workerWarmed = false;
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        byte[] package = Mod.GetFileBytes("Assets/VanillaWorker.bin");
        string world = Main.ActiveWorldFileData.Path;
        var token = cancellation.Token;
        startup = Task.Run(() => VanillaWorkerConnection.Start(package, world, token));
        DiagLog.Write("[engine-worker-start] prewarm=" + !running);
    }

    internal async Task<JsonObject> Observe(JsonObject request, CancellationToken token)
    {
        EnsureWorker();
        var connection = worker?.Alive == true ? worker : await startup.WaitAsync(token);
        return await Task.Run(() => connection.Send(request, token), token);
    }

    void WarmWorker()
    {
        try
        {
            var player = Main.LocalPlayer;
            int x = (int)(player.Center.X / 16), y = (int)(player.Center.Y / 16);
            var area = Rectangle.Intersect(new Rectangle(x - 10, y - 10, 21, 21),
                new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
            var request = new JsonObject { ["command"] = "replay", ["snapshot"] = VanillaFrame.Capture(player, area, true),
                ["controls"] = new JsonArray(0, 2, 4), ["detailed"] = false, ["stop_on_damage"] = true };
            var connection = worker;
            var token = cancellation.Token;
            warmup = Task.Run(() => connection.Send(request, token), token);
        }
        catch (Exception error)
        {
            workerWarmed = true;
            DiagLog.Write("[engine-worker-warmup-skipped] " + DescribeError(error));
        }
    }

    public override void OnWorldLoad()
    {
        if (Main.dedServ || Main.netMode != NetmodeID.SinglePlayer || Main.ActiveWorldFileData.IsCloudSave) return;
        try { EnsureWorker(); }
        catch (Exception error) { DiagLog.Write("[engine-worker-prewarm-failed] " + DescribeError(error)); }
    }

    public void Stop(string reason = "已停止", bool closeWorker = false)
    {
        running = false; NavigationOwned = false; terrainPaused = false; Status = reason;
        regions?.Dispose(); regions = null;
        drawnRegion = null;
        movement?.CancelPending(); movement = null; CancelTerrain();
        if (closeWorker)
        {
            cancellation?.Cancel();
            if (warmup != null) _ = warmup.ContinueWith(task => { _ = task.Exception; });
            warmup = null; workerWarmed = false;
            worker?.Dispose(); worker = null;
            responseFrames = 0;
            if (startup != null)
            {
                _ = startup.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; });
                startup = null;
            }
            cancellation?.Dispose(); cancellation = null;
        }
        AxisLock.Release("vanilla-demo");
    }

    VanillaAsyncMovement ConnectMovement(VanillaWorkerConnection connection)
        => new((request, token) =>
        {
            if (NavigationOwned)
            {
                request["guidance"] = VanillaNavBridge.CaptureGuidance(request["snapshot"].AsObject());
                request["terminal"] = VanillaNavBridge.Terminal;
                request["route_direction"] = VanillaNavBridge.RouteDirection;
            }
            return Task.Run(() => connection.Send(request, token));
        }, Math.Clamp(responseFrames + 6, VanillaAsyncMovement.LeadFrames, 90));

    void ResetInput(On_Player.orig_ResetControls original, Player player) { original(player); ApplyInput(player); }
    void CopyInput(On_TriggersSet.orig_CopyInto original, TriggersSet triggers, Player player) { original(triggers, player); ApplyInput(player); }

    void ApplyInput(Player player, bool applyTerrainUse = true)
    {
        if (!running || player != Main.LocalPlayer) return;
        if (regions != null)
        {
            var input = regions.InputAt(tick + 1);
            if (input.UseSlot >= 0)
            {
                var item = player.inventory[input.UseSlot];
                var target = input.Target.Value;
                if (item.IsAir || item.pick <= 0 && item.createTile < 0
                    || item.pick > 0 && !LavaSafety.CanMine(target.X, target.Y)
                    || item.createTile >= 0 && LavaSafety.IsLava(target.X, target.Y))
                {
                    RecedingNav.LastStop = "region_item_or_terrain_changed";
                    RecedingNav.Stop(); Stop("区域路线物品或地形已变化"); return;
                }
                player.delayUseItem = player.mouseInterface = false;
                Main.HoveringOverAnNPC = Main.SmartInteractShowingGenuine = false;
            }
            VanillaPrediction.ApplyInput(player, input);
            LastControl = new ControlFrame((int)input.Keys, regions.HasInput(tick + 1), "region",
                regions.Index(tick + 1), false, goal, goal, Vector2.Zero);
            return;
        }
        if (NavigationOwned && !terrainPaused && movement?.ProtectFall(player, tick + 1) == true)
            DiagLog.Write($"[engine-emergency-jump] tick={tick} x={player.position.X:0.##} y={player.position.Y:0.##} vy={player.velocity.Y:0.##} fall_start={player.fallStart}");
        int keys = terrainPaused ? 0 : movement?.InputAt(tick + 1) ?? 0;
        LastControl = new ControlFrame(keys, movement?.HasInputAt(tick + 1) == true, movement?.ActivePlan ?? "none",
            movement?.PlanFrameAt(tick + 1) ?? -1, VanillaNavBridge.Terminal, goal,
            movement?.ActiveGoal ?? Vector2.Zero, movement?.ActiveAnchor ?? Vector2.Zero);
        VanillaPrediction.ApplyInput(player, new VanillaPrediction.Input((VanillaKeys)keys));
        if (NavigationOwned && applyTerrainUse) VanillaNavBridge.ApplyTerrainUse(player);
    }


    public override void PostUpdateEverything()
    {
        try
        {
            if (startup != null)
            {
                if (!startup.IsCompleted) return;
                var completed = startup; startup = null;
                worker = completed.GetAwaiter().GetResult();
                DiagLog.Write("[engine-worker-ready] " + worker.StartupTiming.ToJsonString());
                WarmWorker();
                if (running && regions == null) movement = ConnectMovement(worker);
            }
            if (warmup?.IsCompleted == true)
            {
                var completed = warmup; warmup = null;
                workerWarmed = true;
                try
                {
                    var result = completed.GetAwaiter().GetResult();
                    DiagLog.Write($"[engine-worker-warmup] compute_ms={result["elapsed_ms"]} roundtrip_ms={result["roundtrip_ms"]}");
                }
                catch (Exception error) { DiagLog.Write("[engine-worker-warmup-failed] " + DescribeError(error)); }
            }
            if (!running) return;
            var player = Main.LocalPlayer;
            tick++;
            if (player.dead || Main.gameMenu) { Stop("人物死亡或离开世界"); return; }
            if (regions != null)
            {
                regions.Tick(player, tick, goal);
                Status = regions.Status;
                if (!ReferenceEquals(drawnRegion, regions.Result))
                {
                    drawnRegion = regions.Result;
                    route = drawnRegion?["frames"]?.AsArray().Select(frame =>
                        new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>()) + player.Size / 2).ToArray()
                        ?? Array.Empty<Vector2>();
                }
                return;
            }
            if (terrainPaused) return;
            int descentCount = movement.ContactDescents;
            movement.Poll(tick, player, 0);
            if (movement.ResponseFrames > 0) responseFrames = movement.ResponseFrames;
            if (NavigationOwned) VanillaNavBridge.Prefetch(player, movement, tick);
            if (movement.InputAt(tick + 1) == 0) idleFrames++;
            if (movement.Accepted > 0 && !movement.HasInputAt(tick + 1))
            { starvedFrames++; starvation++; longestStarvation = Math.Max(longestStarvation, starvation); }
            else starvation = 0;
            if (movement.Accepted != loggedAccepted || movement.Rejected != loggedRejected)
            {
                loggedAccepted = movement.Accepted; loggedRejected = movement.Rejected;
                var result = movement.LastResult;
                if (Terraria.Program.LaunchParameters.ContainsKey("-vnavbaseline")
                    && ReferenceEquals(result, movement.ActiveResult) && result != null
                    && JsonNode.DeepEquals(result["request_id"], movement.LastRequest?["request_id"]))
                {
                    string path = Path.Combine(Main.SavePath, $"native-accepted-{Main.GameUpdateCount}.json");
                    string report = new JsonObject { ["request"] = movement.LastRequest.DeepClone(),
                        ["result"] = result.DeepClone(), ["plan_index"] = movement.PlanFrameAt(tick + 1) }.ToJsonString();
                    _ = Task.Run(() => File.WriteAllText(path, report));
                }
                if (movement.LastRequest?["continuation_only"]?.GetValue<bool>() == true && result != null && !movement.Busy
                    && !ReferenceEquals(result, loggedRepairResult)
                    && JsonNode.DeepEquals(result["request_id"], movement.LastRequest["request_id"]))
                {
                    loggedRepairResult = result;
                    DiagLog.Write($"[engine-route-repair] status={result?["status"]} selected={result?["selected"]} rejection={movement.LastRejection} prefix_frames={movement.LastRequest["prefix"]?.AsArray().Count} roundtrip_ms={result?["roundtrip_ms"]}");
                }
                var cell = ((int)(player.Center.X / 16), (int)(player.Bottom.Y / 16));
                if (Terraria.Program.LaunchParameters.ContainsKey("-vnavbaseline")
                    && result?["status"]?.GetValue<string>() == "planned"
                    && result["route_progress"]?.GetValue<double>() <= 0 && recordedObstructions.Add(cell))
                {
                    string path = Path.Combine(Main.SavePath, $"native-obstruction-{Main.GameUpdateCount}.json");
                    string report = new JsonObject { ["request"] = movement.LastRequest.DeepClone(), ["result"] = result.DeepClone() }.ToJsonString();
                    _ = Task.Run(() => File.WriteAllText(path, report));
                }
                DiagLog.Write($"[engine-nav-timing] tick={tick} capture_ms={captureMs:0.###} request_ms={requestMs:0.###} capture_bytes={captureBytes} request_bytes={requestBytes} compute_ms={result?["request_compute_ms"]} roundtrip_ms={result?["roundtrip_ms"]} breakdown={result?["timing"]?.ToJsonString()} response_frames={movement.ResponseFrames} lead_frames={movement.PlanningLead} zero_input_frames={idleFrames} starved_frames={starvedFrames} longest_starvation={longestStarvation} accepted={loggedAccepted} rejected={loggedRejected} caught_up={movement.LateAccepted} rebased={movement.Rebased} reason={movement.LastRejection} selected={result?["selected"]}");
            }
            if (movement.Status == "worker_error") throw new IOException("推演进程出错，请查看临时目录中的 worker.log");
            float distance = Vector2.Distance(player.position, goal);
            if (!NavigationOwned && distance <= 20 && Math.Abs(player.velocity.Y) < .01f)
            { Reached = true; Stop("已到达目标"); Main.NewText("原版移动实验：已到达目标", Color.LightGreen); return; }
            if (distance < closest - 4) { closest = distance; stalled = 0; } else stalled++;
            if (!NavigationOwned && (stalled > 600 || tick > 1800)) { Stop("未能继续接近目标，请换一个落脚点"); return; }
            if (movement.Accepted != previousAccepted)
            {
                previousAccepted = movement.Accepted;
                route = movement.AcceptedRoute.Select(point => point + player.Size / 2).ToArray();
            }
            Status = $"{Explain(movement.LastRejection ?? movement.Status)}  接受{movement.Accepted}/拒绝{movement.Rejected}  距离{distance / 16:0.0}格";
            int contactCount = movement.ContactContinuations;
            bool needsPlan = !VanillaNavBridge.ApproachingTerrain
                && (!NavigationOwned || movement.NeedsPlan(tick, player, VanillaNavBridge.Terminal));
            if (movement.ContactDescents != descentCount)
                DiagLog.Write($"[engine-contact-descent] x={player.position.X:0.###} y={player.position.Y:0.###} vy={player.velocity.Y:0.###}; retaining safe descent despite timing differences");
            if (movement.LastDivergence is JsonObject divergence && !ReferenceEquals(divergence, recordedDivergence))
            {
                recordedDivergence = divergence;
                string path = Path.Combine(Main.SavePath, $"vanilla-divergence-{Main.GameUpdateCount}.json");
                string report = divergence.ToJsonString();
                _ = Task.Run(() => File.WriteAllText(path, report));
            }
            if (movement.ContactContinuations != contactCount)
            {
                route = movement.AcceptedRoute.Select(point => point + player.Size / 2).ToArray();
                DiagLog.Write($"[engine-contact-continue] offset_x={movement.ContactOffset} retained_frames={movement.ContactRetainedFrames}");
            }
            if (needsPlan && !movement.Busy)
            {
                if ((player.itemAnimation > 0 || player.itemTime > 0) && !(NavigationOwned && player.HeldItem.pick > 0)) return;
                long captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
                var snapshot = VanillaFrame.Capture(player,
                    VanillaLocalPlanner.SnapshotArea(player, goal, movement.PlanningLead), NavigationOwned);
                captureMs = System.Diagnostics.Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds;
                captureBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
                long requestStart = System.Diagnostics.Stopwatch.GetTimestamp();
                allocatedStart = GC.GetAllocatedBytesForCurrentThread();
                movement.Request(snapshot, tick, goal, 0, takeOwnership: true);
                requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                requestBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            }
        }
        catch (Exception error)
        {
            if (regions != null && NavigationOwned)
            {
                RecedingNav.LastStop = "region_error:" + DescribeError(error);
                RecedingNav.Stop();
            }
            Stop("失败：" + DescribeError(error), true); Main.NewText(Status, Color.OrangeRed);
        }
    }

    public static string DescribeError(Exception error) => error.GetBaseException().Message switch
    {
        "active_mount_or_grapple" => "请先下坐骑并收回钩爪，当前实验尚不支持它们",
        "active_item_use" => "请松开使用物品，等使用动画结束",
        "modded_item" => "当前实验只支持原版物品和装备",
        "dynamic_entities_in_snapshot" => "附近有NPC、弹幕或掉落物，当前实验尚不支持动态实体",
        "wiring_in_snapshot" => "附近有导线，当前实验尚不支持机关",
        "engine_mismatch" => "推演器与游戏版本不一致，请重新编译模组",
        _ => error.GetBaseException().Message
    };

    static string Explain(string state) => state switch
    {
        "planning" => "推演中", "accepted" => "执行路线", "late_result" => "结果迟到，重新推演",
        "no_safe_plan" => "没有安全候选", "context_changed" => "装备或环境改变，重新推演",
        _ when state.StartsWith("state_changed:") => "状态变化，重新推演",
        "worker_error" => "推演进程出错", _ => state
    };

    public override void PostDrawTiles()
    {
        if (Main.dedServ || Main.gameMenu || !running && route.Length == 0) return;
        var batch = Main.spriteBatch;
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
        for (int i = 1; i < route.Length; i++)
        {
            var delta = route[i] - route[i - 1];
            batch.Draw(TextureAssets.MagicPixel.Value, route[i - 1] - Main.screenPosition, new Rectangle(0, 0, 1, 1), Color.Cyan * .8f,
                delta.ToRotation(), Vector2.Zero, new Vector2(delta.Length(), 2), SpriteEffects.None, 0);
        }
        batch.Draw(TextureAssets.MagicPixel.Value, goal + new Vector2(Main.LocalPlayer.width / 2, Main.LocalPlayer.height) - Main.screenPosition,
            new Rectangle(0, 0, 1, 1), Color.Yellow, 0, new Vector2(.5f), new Vector2(10), SpriteEffects.None, 0);
        batch.End();
    }

    public override void PostDrawInterface(SpriteBatch batch)
    {
        if (Main.gameMenu || Status == "未启动") return;
        Utils.DrawBorderString(batch, "原版移动实验：" + Status, new Vector2(24, 92), Color.Cyan, .85f);
    }

    public override void OnWorldUnload() { Stop(closeWorker: true); route = Array.Empty<Vector2>(); Status = "未启动"; }
    public override void Unload()
    {
        Stop(closeWorker: true);
        if (startup != null) _ = startup.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; });
        if (Main.dedServ) return;
        On_Player.ResetControls -= ResetInput; On_TriggersSet.CopyInto -= CopyInput;
    }
}

public sealed class VanillaNavigationCommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "vnav";
    public override string Description => "原版移动实验：/vnav [explore|replay|clear|落脚点格子X 格子Y|stop|status]";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        var demo = VanillaNavigationDemo.Instance;
        if (args.Length == 1 && args[0] is "explore" or "replay" or "clear")
        {
            var explorer = ModContent.GetInstance<VanillaReachability>();
            try
            {
                if (args[0] == "explore") explorer.Start();
                else if (args[0] == "replay") explorer.Replay();
                else explorer.Clear();
                caller.Reply(explorer.Status);
            }
            catch (Exception error) { caller.Reply(error.GetBaseException().Message); }
            return;
        }
        if (args.Length == 1 && args[0] == "stop") { ModContent.GetInstance<VanillaReachability>().Clear(); demo.Stop(); caller.Reply(demo.Status); return; }
        if (args.Length == 1 && args[0] == "status") { caller.Reply(demo.Status); return; }
        if (args.Length == 2 && args[0] == "engine" && args[1] is "on" or "off")
        {
            VanillaNavBridge.Reset(); VanillaNavBridge.Enabled = args[1] == "on";
            caller.Reply("现有寻路的引擎移动后端：" + (VanillaNavBridge.Enabled ? "开" : "关")); return;
        }
        if (args.Length == 3 && args[0] is "route" or "reach" or "regions" && int.TryParse(args[1], out int gx) && int.TryParse(args[2], out int gy))
        {
            Halt.All(); NativeAgent.Stop(); VanillaNavBridge.Enabled = true;
            VanillaNavBridge.RegionsEnabled = args[0] == "regions";
            RecedingNav.Start(gx, gy, args[0] == "reach" ? RecedingNav.Mode.Reach : RecedingNav.Mode.Snap);
            caller.Reply("开始带挖掘/搭建的引擎寻路；/halt 停止。"); return;
        }
        if (args.Length == 1 && args[0] == "descent")
        {
            Halt.All(); NativeAgent.Stop(); VanillaNavBridge.Enabled = true;
            VanillaDescentRun.Instance.Start(); caller.Reply("开始规划丛林入口→地狱落脚点；/halt 停止。"); return;
        }
        try
        {
            Vector2 feet;
            if (args.Length == 0) feet = Main.MouseWorld;
            else if (args.Length == 2 && int.TryParse(args[0], out int x) && int.TryParse(args[1], out int y)) feet = new Vector2(x * 16, y * 16);
            else throw new InvalidOperationException("用法：/vnav [X Y|stop|status]，X Y是落脚处的格子坐标。");
            demo.Start(feet - new Vector2(Main.LocalPlayer.width / 2, Main.LocalPlayer.height));
            caller.Reply("开始接管移动。青线是预测路线，黄点是目标；/vnav stop 停止。");
        }
        catch (Exception error) { caller.Reply(VanillaNavigationDemo.DescribeError(error)); }
    }
}
