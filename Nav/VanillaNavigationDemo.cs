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
    public static VanillaNavigationDemo Instance => ModContent.GetInstance<VanillaNavigationDemo>();
    public static bool Active => Instance.running;
    public string Status { get; private set; } = "未启动";
    public bool Reached { get; private set; }
    public bool NavigationOwned { get; private set; }
    public int RoutePoints => route.Length;
    public string PlannerStatus => movement?.Status ?? Status;
    public JsonObject PlannerResult => movement?.LastResult;
    public string PlannerRejection => movement?.LastRejection;
    public string PlannerMismatch => movement?.LastMismatch;
    public int PlanAccepted => movement?.Accepted ?? 0;
    public int PlanRejected => movement?.Rejected ?? 0;
    public bool PlannerReady => movement != null;
    bool running;
    long tick;
    Vector2 goal;
    Vector2[] route = Array.Empty<Vector2>();
    CancellationTokenSource cancellation;
    Task<VanillaWorkerConnection> startup;
    VanillaWorkerConnection worker;
    VanillaAsyncMovement movement;
    int previousAccepted;
    int stalled;
    float closest;
    double captureMs;
    double requestMs;
    long captureBytes, requestBytes;
    int loggedAccepted, loggedRejected, idleFrames;
    int starvedFrames, starvation, longestStarvation;

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Player.ResetControls += ResetInput;
        On_TriggersSet.CopyInto += CopyInput;
    }

    public void Start(Vector2 target) => StartCore(target, false);
    internal void StartNavigation(Vector2 target) => StartCore(target, true);
    internal void UpdateNavigationGoal(Vector2 target) { goal = target; closest = Vector2.Distance(Main.LocalPlayer.position, goal); }

    void StartCore(Vector2 target, bool navigation)
    {
        var player = Main.LocalPlayer;
        if (Main.netMode != NetmodeID.SinglePlayer || Main.ActiveWorldFileData.IsCloudSave)
            throw new InvalidOperationException("目前是单人本地世界的原版移动实验入口。");
        if (player.dead || player.ghost) throw new InvalidOperationException("请先复活。");
        if (Vector2.Distance(player.position, target) > 60 * 16) throw new InvalidOperationException("请选60格以内的落脚点。");
        VanillaFrame.Capture(player, VanillaLocalPlanner.SnapshotArea(player, target), navigation);
        byte[] package = Mod.GetFileBytes("Assets/VanillaWorker.bin");
        if (!navigation) { Halt.All(); NativeAgent.Stop(); }
        if (!navigation && !AxisLock.Take("vanilla-demo", Ax.Move | Ax.Jump | Ax.Vertical | Ax.Use, () => running))
            throw new InvalidOperationException("人物控制权被其他动作占用。");
        running = true; NavigationOwned = navigation; Reached = false; goal = target; tick = 0; stalled = 0;
        closest = Vector2.Distance(player.position, goal); previousAccepted = 0;
        loggedAccepted = loggedRejected = idleFrames = starvedFrames = starvation = longestStarvation = 0;
        route = Array.Empty<Vector2>();
        cancellation = new CancellationTokenSource();
        if (worker?.Alive == true)
        {
            movement = ConnectMovement(worker); Status = "推演中"; return;
        }
        string world = Main.ActiveWorldFileData.Path;
        var token = cancellation.Token;
        startup = Task.Run(() => VanillaWorkerConnection.Start(package, world, token));
        Status = "正在启动独立推演进程……";
    }

    public void Stop(string reason = "已停止", bool closeWorker = false)
    {
        running = false; NavigationOwned = false; Status = reason;
        cancellation?.Cancel(); movement = null;
        if (closeWorker) { worker?.Dispose(); worker = null; }
        if (startup != null)
        {
            _ = startup.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; });
            startup = null;
        }
        AxisLock.Release("vanilla-demo");
    }

    VanillaAsyncMovement ConnectMovement(VanillaWorkerConnection connection)
        => new(request =>
        {
            if (NavigationOwned)
            {
                request["guidance"] = VanillaNavBridge.CaptureGuidance(request["snapshot"].AsObject());
                request["terminal"] = VanillaNavBridge.Terminal;
            }
            return Task.Run(() => connection.Send(request));
        });

    void ResetInput(On_Player.orig_ResetControls original, Player player) { original(player); ApplyInput(player); }
    void CopyInput(On_TriggersSet.orig_CopyInto original, TriggersSet triggers, Player player) { original(triggers, player); ApplyInput(player); }

    void ApplyInput(Player player)
    {
        if (!running || player != Main.LocalPlayer) return;
        int keys = movement?.InputAt(tick + 1) ?? 0;
        player.controlLeft = (keys & 1) != 0; player.controlRight = (keys & 2) != 0;
        player.controlJump = (keys & 4) != 0; player.controlUp = (keys & 8) != 0; player.controlDown = (keys & 16) != 0;
        player.controlUseItem = player.controlUseTile = player.controlHook = player.controlMount = false;
        if (!NavigationOwned) return;
        if (player.velocity.Y > 0 && !player.noFallDmg && player.gravDir == 1
            && player.position.Y / 16 - player.fallStart > 20)
        {
            var fall = new Vector2(0, player.velocity.Y * 8 + player.gravity * 32);
            bool landingSoon = Collision.TileCollision(player.position, fall, player.width, player.height).Y < fall.Y;
            if (landingSoon)
            {
                bool available = false;
                foreach (var jump in player.ExtraJumps) available |= jump.Available;
                if (available) player.controlJump = player.releaseJump;
                else if (player.itemAnimation == 0)
                {
                    int web = Array.FindIndex(player.inventory, item => item.type == ItemID.Cobweb && item.stack > 0);
                    if (web >= 0)
                    {
                        player.selectedItem = web;
                        Cursor.AimPx(player.Center.X, player.Bottom.Y + player.velocity.Y);
                        player.controlUseItem = true;
                    }
                }
                return;
            }
        }
        NPC foe = null;
        float closestFoe = 320 * 320;
        foreach (var npc in Main.npc)
        {
            if (!npc.active || npc.friendly || npc.damage <= 0 || npc.life <= 0) continue;
            float distance = Vector2.DistanceSquared(player.Center, npc.Center);
            if (distance < closestFoe) { closestFoe = distance; foe = npc; }
        }
        int weapon = Array.FindIndex(player.inventory, item => item.type == ItemID.Starfury && item.stack > 0);
        if (foe != null && weapon >= 0 && movement?.Busy == true
            && movement.HasInputAt(tick + movement.PlanningLead + player.inventory[weapon].useAnimation + 6))
        {
            player.selectedItem = weapon;
            Cursor.AimPx(foe.Center.X, foe.Center.Y);
            player.controlUseItem = player.releaseUseItem;
        }
    }

    public override void PostUpdateEverything()
    {
        if (!running) return;
        try
        {
            if (startup != null)
            {
                if (!startup.IsCompleted) return;
                worker = startup.GetAwaiter().GetResult(); startup = null;
                movement = ConnectMovement(worker);
            }
            var player = Main.LocalPlayer;
            tick++;
            if (player.dead || Main.gameMenu) { Stop("人物死亡或离开世界"); return; }
            movement.Poll(tick, player, 0);
            if (movement.InputAt(tick + 1) == 0) idleFrames++;
            if (movement.Accepted > 0 && !movement.HasInputAt(tick + 1))
            { starvedFrames++; starvation++; longestStarvation = Math.Max(longestStarvation, starvation); }
            else starvation = 0;
            if (movement.Accepted != loggedAccepted || movement.Rejected != loggedRejected)
            {
                loggedAccepted = movement.Accepted; loggedRejected = movement.Rejected;
                var result = movement.LastResult;
                if (movement.LastDivergence is JsonObject divergence)
                {
                    string path = Path.Combine(Main.SavePath, $"vanilla-divergence-{Main.GameUpdateCount}.json");
                    string report = divergence.ToJsonString();
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
            if (!movement.Busy)
            {
                if (player.itemAnimation > 0 || player.itemTime > 0) return;
                long captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
                var snapshot = VanillaFrame.Capture(player, VanillaLocalPlanner.SnapshotArea(player, goal), NavigationOwned);
                captureMs = System.Diagnostics.Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds;
                captureBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
                long requestStart = System.Diagnostics.Stopwatch.GetTimestamp();
                allocatedStart = GC.GetAllocatedBytesForCurrentThread();
                movement.Request(snapshot, tick, goal, 0);
                requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                requestBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            }
        }
        catch (Exception error) { Stop("失败：" + DescribeError(error), true); Main.NewText(Status, Color.OrangeRed); }
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
    public override string Description => "原版移动实验：/vnav [落脚点格子X 格子Y|stop|status]";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        var demo = VanillaNavigationDemo.Instance;
        if (args.Length == 1 && args[0] == "stop") { demo.Stop(); caller.Reply(demo.Status); return; }
        if (args.Length == 1 && args[0] == "status") { caller.Reply(demo.Status); return; }
        if (args.Length == 2 && args[0] == "engine" && args[1] is "on" or "off")
        {
            VanillaNavBridge.Reset(); VanillaNavBridge.Enabled = args[1] == "on";
            caller.Reply("现有寻路的引擎移动后端：" + (VanillaNavBridge.Enabled ? "开" : "关")); return;
        }
        if (args.Length == 3 && args[0] == "route" && int.TryParse(args[1], out int gx) && int.TryParse(args[2], out int gy))
        {
            Halt.All(); NativeAgent.Stop(); VanillaNavBridge.Enabled = true;
            RecedingNav.Start(gx, gy); caller.Reply("开始带挖掘/搭建的引擎寻路；/halt 停止。"); return;
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
