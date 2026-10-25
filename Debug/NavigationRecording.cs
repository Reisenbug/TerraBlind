using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class NavigationRecording : ModSystem
{
    readonly record struct TileState(bool Present, ushort Type, ushort Wall, short FrameX, short FrameY,
        byte Slope, bool Half, bool Actuated, int Liquid, byte Amount);
    static readonly Stopwatch clock = new();
    static StreamWriter frames;
    static JsonObject metadata, before, input;
    static readonly Dictionary<(int x, int y), TileState> tiles = new();
    static Rectangle initialArea;
    static Vector2 goal;
    static string controller;
    static int count;
    static double overheadMs;
    static readonly JsonArray pauseTimeline = new();
    static JsonArray damage = new();
    static bool lastFocus, lastPause;
    public static string LastDirectory { get; private set; }
    public static string LastReason { get; private set; }
    public static bool Active => frames != null;

    public override void Load() => On_Main.Update += UpdateClock;

    static void UpdateClock(On_Main.orig_Update original, Main game, GameTime time)
    {
        original(game, time);
        if (!Active || lastFocus == Main.hasFocus && lastPause == Main.gamePaused) return;
        lastFocus = Main.hasFocus; lastPause = Main.gamePaused;
        pauseTimeline.Add(new JsonObject { ["wall_ms"] = clock.Elapsed.TotalMilliseconds,
            ["focused"] = lastFocus, ["paused"] = lastPause });
    }

    static TileState ReadTile(int x, int y)
    {
        var t = Main.tile[x, y];
        return new(t.HasTile, t.TileType, t.WallType, t.TileFrameX, t.TileFrameY,
            (byte)t.Slope, t.IsHalfBlock, t.IsActuated, t.LiquidType, t.LiquidAmount);
    }

    static void WriteTile(BinaryWriter writer, TileState t)
    {
        writer.Write(t.Present); writer.Write(t.Type); writer.Write(t.Wall);
        writer.Write(t.FrameX); writer.Write(t.FrameY); writer.Write(t.Slope);
        writer.Write(t.Half); writer.Write(t.Actuated); writer.Write(t.Liquid); writer.Write(t.Amount);
    }

    static JsonArray TileJson(TileState t) => new(t.Present, t.Type, t.Wall, t.FrameX, t.FrameY,
        t.Slope, t.Half, t.Actuated, t.Liquid, t.Amount);

    static JsonArray Items(Item[] items) => new(items.Select(item => (JsonNode)new JsonArray(item.type, item.prefix, item.stack)).ToArray());

    public static void Start(string mode, int gx, int gy)
    {
        if (Active) throw new InvalidOperationException("已有录制正在进行。先 /navrecord stop。");
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer || Main.gameMenu || Main.LocalPlayer.dead)
            throw new InvalidOperationException("录制目前仅支持单人世界中的存活角色。");
        if (!Predicates.InBounds(gx, gy)) throw new InvalidOperationException("终点超出世界。");
        if (mode is not "human" and not "bot") throw new InvalidOperationException("控制者必须是 human 或 bot。");
        if (mode == "human" && (RecedingNav.Active || VanillaNavigationDemo.Active))
            throw new InvalidOperationException("先 /halt 停止自动控制，再录制人工操作。");
        var p = Main.LocalPlayer;
        controller = mode; goal = new Vector2(gx, gy); count = 0; overheadMs = 0; LastReason = null;
        pauseTimeline.Clear(); lastFocus = Main.hasFocus; lastPause = Main.gamePaused;
        pauseTimeline.Add(new JsonObject { ["wall_ms"] = 0, ["focused"] = lastFocus, ["paused"] = lastPause });
        before = input = null; damage = new(); tiles.Clear();
        LastDirectory = Path.Combine(Main.SavePath, "NavigationRecordings", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + mode);
        Directory.CreateDirectory(LastDirectory);
        int cx = (int)(p.Center.X / 16), cy = (int)(p.Bottom.Y / 16);
        int left = Math.Max(0, Math.Min(cx, gx) - 160), top = Math.Max(0, Math.Min(cy, gy) - 32);
        initialArea = new Rectangle(left, top, Math.Min(Main.maxTilesX, Math.Max(cx, gx) + 161) - left,
            Math.Min(Main.maxTilesY, Math.Max(cy, gy) + 33) - top);
        string terrain = Path.Combine(LastDirectory, "initial-terrain.bin");
        using (var writer = new BinaryWriter(new BufferedStream(File.Create(terrain), 65536)))
        {
            writer.Write(1); writer.Write(initialArea.X); writer.Write(initialArea.Y);
            writer.Write(initialArea.Width); writer.Write(initialArea.Height);
            for (int y = initialArea.Top; y < initialArea.Bottom; y++)
            for (int x = initialArea.Left; x < initialArea.Right; x++) WriteTile(writer, ReadTile(x, y));
        }
        using var terrainStream = File.OpenRead(terrain);
        metadata = new JsonObject { ["schema"] = 1, ["controller"] = mode, ["utc_start"] = DateTime.UtcNow.ToString("O"),
            ["world"] = Main.ActiveWorldFileData.Path, ["seed"] = Main.ActiveWorldFileData.SeedText,
            ["world_width"] = Main.maxTilesX, ["world_height"] = Main.maxTilesY, ["game_mode"] = Main.GameMode,
            ["goal_x"] = gx, ["goal_y"] = gy, ["goal_tolerance_tiles"] = 1.5,
            ["start"] = VanillaPrediction.Observe(p), ["width"] = p.width, ["height"] = p.height,
            ["inventory"] = Items(p.inventory), ["armor"] = Items(p.armor), ["misc_equips"] = Items(p.miscEquips),
            ["initial_area"] = new JsonArray(initialArea.X, initialArea.Y, initialArea.Width, initialArea.Height),
            ["terrain_sha256"] = Convert.ToHexString(SHA256.HashData(terrainStream)),
            ["tile_fields"] = "present,type,wall,frame_x,frame_y,slope,half,actuated,liquid,amount",
            ["sampling"] = "before=PreUpdatePlayers,input=PreUpdateMovement,after=PostUpdateEverything",
            ["terrain_changes"] = "observed changes within per-frame watch box; not attributed to player",
            ["clock"] = "Stopwatch wall time; terrain preparation excluded; pauses and recording overhead included",
            ["left_initial_area"] = false };
        File.WriteAllText(Path.Combine(LastDirectory, "metadata.json"), metadata.ToJsonString());
        frames = new StreamWriter(Path.Combine(LastDirectory, "frames.jsonl"), false, System.Text.Encoding.UTF8, 65536);
        clock.Restart();
    }

    public static string Stop(string reason)
    {
        if (!Active) return LastDirectory;
        clock.Stop(); frames.Dispose(); frames = null;
        LastReason = reason;
        var p = Main.LocalPlayer;
        metadata["reason"] = reason; metadata["frames"] = count;
        metadata["wall_ms"] = clock.Elapsed.TotalMilliseconds; metadata["recording_overhead_ms"] = overheadMs;
        metadata["end"] = VanillaPrediction.Observe(p);
        metadata["end_inventory"] = Items(p.inventory); metadata["end_armor"] = Items(p.armor);
        metadata["pause_timeline"] = pauseTimeline.DeepClone();
        metadata["reached"] = !p.dead && Vector2.Distance(new Vector2(p.Center.X / 16, p.Bottom.Y / 16), goal) <= 1.5f;
        File.WriteAllText(Path.Combine(LastDirectory, "metadata.json"), metadata.ToJsonString());
        before = input = null; tiles.Clear();
        Main.NewText($"录制已保存：{clock.Elapsed.TotalSeconds:0.000}s / {count}帧；{LastDirectory}", Color.LightGreen);
        return LastDirectory;
    }

    public override void PreUpdatePlayers()
    {
        if (!Active) return;
        long began = Stopwatch.GetTimestamp();
        var p = Main.LocalPlayer;
        before = VanillaPrediction.Observe(p);
        before["wall_ms"] = clock.Elapsed.TotalMilliseconds;
        tiles.Clear(); input = null; damage = new();
        int radius = Math.Max(8, Math.Max(Player.tileRangeX, Player.tileRangeY) + p.HeldItem.tileBoost + p.blockRange + 2);
        int cx = (int)(p.Center.X / 16), cy = (int)(p.Center.Y / 16);
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(Main.maxTilesY - 1, cy + radius); y++)
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(Main.maxTilesX - 1, cx + radius); x++) tiles[(x, y)] = ReadTile(x, y);
        overheadMs += Stopwatch.GetElapsedTime(began).TotalMilliseconds;
    }

    internal static void CaptureInput(Player p)
    {
        if (!Active || p != Main.LocalPlayer) return;
        long began = Stopwatch.GetTimestamp();
        input = new JsonObject { ["keys"] = VanillaPrediction.Observe(p, false)["keys"].DeepClone(),
            ["use_item"] = p.controlUseItem, ["use_tile"] = p.controlUseTile, ["hook"] = p.controlHook,
            ["mount"] = p.controlMount, ["selected_slot"] = p.selectedItem, ["item_type"] = p.HeldItem.type,
            ["item_stack"] = p.HeldItem.stack, ["item_prefix"] = p.HeldItem.prefix,
            ["mouse_x"] = Main.MouseWorld.X, ["mouse_y"] = Main.MouseWorld.Y,
            ["tile_target_x"] = Player.tileTargetX, ["tile_target_y"] = Player.tileTargetY,
            ["smart_cursor"] = Main.SmartCursorWanted_Mouse };
        overheadMs += Stopwatch.GetElapsedTime(began).TotalMilliseconds;
    }

    internal static void CaptureDamage(Player p, Player.HurtInfo info)
    {
        if (!Active || p != Main.LocalPlayer) return;
        damage.Add(new JsonObject { ["wall_ms"] = clock.Elapsed.TotalMilliseconds, ["damage"] = info.Damage,
            ["npc"] = info.DamageSource.SourceNPCIndex, ["projectile"] = info.DamageSource.SourceProjectileType,
            ["other"] = info.DamageSource.SourceOtherIndex });
    }

    public override void PostUpdateEverything()
    {
        if (!Active || before == null) return;
        long began = Stopwatch.GetTimestamp();
        var p = Main.LocalPlayer;
        var changes = new JsonArray();
        foreach (var pair in tiles)
        {
            var after = ReadTile(pair.Key.x, pair.Key.y);
            if (after != pair.Value) changes.Add(new JsonObject { ["x"] = pair.Key.x, ["y"] = pair.Key.y,
                ["before"] = TileJson(pair.Value), ["after"] = TileJson(after) });
        }
        var demo = VanillaNavigationDemo.Instance;
        frames.WriteLine(new JsonObject { ["frame"] = count++, ["game_frame"] = Main.GameUpdateCount,
            ["wall_ms"] = clock.Elapsed.TotalMilliseconds, ["before"] = before, ["input"] = input,
            ["after"] = VanillaPrediction.Observe(p), ["terrain_changes"] = changes,
            ["damage"] = damage,
            ["focused"] = Main.hasFocus, ["game_paused"] = Main.gamePaused,
            ["hostiles"] = Main.npc.Count(npc => npc.active && !npc.friendly && npc.damage > 0),
            ["nav_stage"] = VanillaNavBridge.Stage, ["nav_active"] = RecedingNav.Active,
            ["engine_active"] = VanillaNavigationDemo.Active, ["planner_status"] = demo.PlannerStatus,
            ["compute_ms"] = demo.PlannerResult?["request_compute_ms"]?.DeepClone(),
            ["roundtrip_ms"] = demo.PlannerResult?["roundtrip_ms"]?.DeepClone(),
            ["queued"] = demo.LastControl.Queued, ["plan"] = demo.LastControl.Plan,
            ["plan_index"] = demo.LastControl.Index, ["rejection"] = demo.PlannerRejection }.ToJsonString());
        if (count % 60 == 0) frames.Flush();
        before = input = null;
        if (!initialArea.Contains(p.Center.ToTileCoordinates())) metadata["left_initial_area"] = true;
        overheadMs += Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (p.dead) Stop("dead");
        else if (Vector2.Distance(new Vector2(p.Center.X / 16, p.Bottom.Y / 16), goal) <= 1.5f) Stop("goal");
        else if (controller == "human" && (RecedingNav.Active || VanillaNavigationDemo.Active)) Stop("automation_started_during_human_recording");
    }

    public override void OnWorldUnload() { if (Active) Stop("world_unload"); }
    public override void Unload() { On_Main.Update -= UpdateClock; if (Active) Stop("mod_unload"); }
}

public sealed class NavigationRecordingPlayer : ModPlayer
{
    public override void PreUpdateMovement() => NavigationRecording.CaptureInput(Player);
    public override void OnHurt(Player.HurtInfo info) => NavigationRecording.CaptureDamage(Player, info);
}

public sealed class NavigationRecordingCommand : ModCommand
{
    public override string Command => "navrecord";
    public override CommandType Type => CommandType.Chat;
    public override string Description => "/navrecord start human|bot X Y；/navrecord stop";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "stop")
            { caller.Reply(NavigationRecording.Stop("manual_stop") ?? "没有录制记录。"); return; }
            if (args.Length != 4 || args[0] != "start" || !int.TryParse(args[2], out int gx) || !int.TryParse(args[3], out int gy))
                throw new InvalidOperationException(Description);
            NavigationRecording.Start(args[1], gx, gy);
            if (args[1] == "bot")
            {
                Halt.All(); NativeAgent.Stop(); VanillaNavBridge.Enabled = true;
                RecedingNav.Start(gx, gy);
            }
            caller.Reply("开始逐帧录制；抵达目标或死亡自动保存；/navrecord stop 手动结束。");
        }
        catch (Exception error) { caller.Reply(error.GetBaseException().Message); }
    }
}
