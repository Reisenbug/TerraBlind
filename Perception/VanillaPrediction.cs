using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

[Flags]
public enum VanillaKeys { None = 0, Left = 1, Right = 2, Jump = 4, Up = 8, Down = 16 }

public sealed class VanillaPrediction
{
    public readonly record struct Segment(Input Input, int Frames);
    public readonly record struct Trial(Player Player, bool Accepted, int Frames);

    public sealed class BranchSimulation
    {
        VanillaFrame.WorkerState knownWorld;
        public double RestoreMs { get; private set; }
        public double UpdateMs { get; private set; }
        public double CaptureMs { get; private set; }

        public Trial Run(VanillaFrame.WorkerState start, Segment operation, Func<Player, bool> acceptFrame)
        {
            CheckCancellation();
            long at = Stopwatch.GetTimestamp();
            var player = start.Restore(knownWorld);
            knownWorld = null;
            RestoreMs += Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            at = Stopwatch.GetTimestamp();
            int frames = 0;
            bool accepted = true;
            for (; frames < operation.Frames;)
            {
                Advance(player, operation.Input.Keys, operation.Input.UseSlot, operation.Input.Target);
                frames++;
                if (!acceptFrame(player)) { accepted = false; break; }
            }
            UpdateMs += Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            return new Trial(player, accepted, frames);
        }

        public VanillaFrame.WorkerState Capture(VanillaFrame.WorkerState start, Trial trial)
        {
            long at = Stopwatch.GetTimestamp();
            knownWorld = start.CaptureCurrent(trial.Player);
            CaptureMs += Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            return knownWorld;
        }
    }

    public sealed class RegionPlayback
    {
        readonly Input[] inputs;
        readonly JsonObject result;
        readonly long start;
        public RegionPlayback(JsonObject result, long completedTick)
        {
            inputs = RegionInputs(result);
            if (result["frames"]?.AsArray().Count != inputs.Length || result["anchor"] is not JsonObject)
                throw new ArgumentException("missing_region_execution_trace");
            this.result = result;
            start = completedTick + 1;
        }
        public int Index(long tick) => checked((int)(tick - start));
        public bool HasInput(long tick) => tick >= start && tick < start + inputs.Length;
        public bool Complete(long tick) => tick >= start + inputs.Length - 1;
        public Input At(long tick) => HasInput(tick) ? inputs[Index(tick)] : new Input(VanillaKeys.None);
        public bool Matches(Player player, long completedTick, out string difference)
        {
            int index = Index(completedTick);
            JsonObject expected = index == -1 ? result["anchor"].AsObject()
                : index >= 0 && index < inputs.Length ? result["frames"][index].AsObject() : null;
            difference = "missing_region_frame";
            if (expected == null) return false;
            bool matches = VanillaAsyncMovement.StateMatches(player, expected, true, out string reason, out string mismatch);
            difference = mismatch ?? reason;
            if (!matches) return false;
            foreach (string key in new[] { "item_animation", "item_time" })
            {
                int actual = key == "item_animation" ? player.itemAnimation : player.itemTime;
                if (actual == expected[key].GetValue<int>()) continue;
                difference = "state_changed:" + key;
                return false;
            }
            return true;
        }
    }

    public readonly record struct Input(VanillaKeys Keys, int UseSlot = -1, Point? Target = null);

    public static Input[] RegionInputs(JsonObject result)
    {
        var keys = result["controls"].AsArray();
        var uses = result["use_slots"].AsArray();
        var targets = result["use_targets"] as JsonArray;
        if (keys.Count is < 1 or > 240 || uses.Count != keys.Count
            || targets != null && targets.Count != keys.Count)
            throw new ArgumentException("invalid_region_inputs");
        Point? fallback = (result["placement_tile"] ?? result["mining_tile"]) is JsonArray tile
            ? new Point(tile[0].GetValue<int>(), tile[1].GetValue<int>()) : null;
        var inputs = new Input[keys.Count];
        for (int i = 0; i < inputs.Length; i++)
        {
            int control = keys[i].GetValue<int>(), slot = uses[i].GetValue<int>();
            Point? target = targets == null ? fallback
                : new Point(targets[i][0].GetValue<int>(), targets[i][1].GetValue<int>());
            if ((control & ~31) != 0 || (control & 3) == 3 || slot < -1 || slot >= 58
                || slot >= 0 && target == null)
                throw new ArgumentException("invalid_region_input");
            inputs[i] = new Input((VanillaKeys)control, slot, target);
        }
        return inputs;
    }

    public static void ApplyInput(Player player, Input input)
    {
        var keys = input.Keys;
        player.controlLeft = keys.HasFlag(VanillaKeys.Left); player.controlRight = keys.HasFlag(VanillaKeys.Right);
        player.controlJump = keys.HasFlag(VanillaKeys.Jump); player.controlUp = keys.HasFlag(VanillaKeys.Up);
        player.controlDown = keys.HasFlag(VanillaKeys.Down);
        player.controlUseItem = input.UseSlot >= 0;
        if (input.UseSlot >= 0)
        {
            player.selectedItem = input.UseSlot;
            if (input.Target is Point aim)
            {
                Main.mouseX = (int)(aim.X * 16 + 8 - Main.screenPosition.X);
                Main.mouseY = (int)(aim.Y * 16 + 8 - Main.screenPosition.Y);
                if (player.gravDir == -1) Main.mouseY = Main.screenHeight - Main.mouseY;
                Main.SmartCursorWanted_Mouse = false;
            }
        }
        player.controlUseTile = player.controlHook = player.controlMount = false;
    }

    internal static Func<bool> CancellationRequested { get; set; }
    internal static void CheckCancellation()
    {
        if (CancellationRequested?.Invoke() == true) throw new OperationCanceledException();
    }
    public readonly record struct MovementSample(Vector2 Position, Vector2 Velocity, int Hp, bool Dead,
        float WingTime, int AvailableJumps, bool Grounded, bool Wet);
    readonly JsonObject checkpoint;
    readonly Rectangle area;
    readonly VanillaFrame.WorkerState compiled;
    static readonly FieldInfo clock = typeof(Main).GetField("_gameUpdateCount", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("engine_clock_field_missing");
    internal static VanillaKeys PendingKeys { get; private set; }
    static int pendingUseSlot = -1;
    static Point? pendingTarget;
    internal static bool UpdatingPlayer { get; private set; }

    internal static void ApplyKeys(Player player)
    {
        ApplyInput(player, new Input(PendingKeys, pendingUseSlot, pendingTarget));
    }

    public VanillaPrediction(JsonObject state)
    {
        VanillaFrame.RequireWorker();
        checkpoint = (JsonObject)state.DeepClone();
        compiled = new VanillaFrame.WorkerState(checkpoint);
        area = new Rectangle(state["area"][0].GetValue<int>() * 16, state["area"][1].GetValue<int>() * 16,
            state["area"][2].GetValue<int>() * 16, state["area"][3].GetValue<int>() * 16);
    }

    public static void Advance(Player player, VanillaKeys keys, int useSlot = -1, Point? target = null)
    {
        CheckCancellation();
        VanillaFrame.RequireWorker();
        if (((int)keys & ~31) != 0 || keys.HasFlag(VanillaKeys.Left) && keys.HasFlag(VanillaKeys.Right))
            throw new ArgumentException("invalid_keys");
        if (useSlot >= 0 && (useSlot >= player.inventory.Length || target == null
            || player.inventory[useSlot].pick <= 0 && player.inventory[useSlot].createTile < 0))
            throw new ArgumentException("unsupported_terrain_item_use");
        int originalPlayer = Main.myPlayer;
        int mouseX = Main.mouseX, mouseY = Main.mouseY;
        bool smartCursor = Main.SmartCursorWanted_Mouse;
        bool film = player.isControlledByFilm;
        bool focus = Main.hasFocus;
        try
        {
            Main.myPlayer = player.whoAmI;
            player.isControlledByFilm = false;
            Main.hasFocus = false;
            PendingKeys = keys;
            pendingUseSlot = useSlot;
            pendingTarget = target;
            if (target is Point aim && useSlot >= 0)
            {
                Main.mouseX = (int)(aim.X * 16 + 8 - Main.screenPosition.X);
                Main.mouseY = (int)(aim.Y * 16 + 8 - Main.screenPosition.Y);
                if (player.gravDir == -1) Main.mouseY = Main.screenHeight - Main.mouseY;
                Main.SmartCursorWanted_Mouse = false;
            }
            clock.SetValue(null, Convert.ChangeType(Main.GameUpdateCount + 1, clock.FieldType));
            UpdatingPlayer = true;
            player.Update(player.whoAmI);
        }
        finally
        {
            UpdatingPlayer = false;
            pendingUseSlot = -1;
            pendingTarget = null;
            Main.mouseX = mouseX; Main.mouseY = mouseY; Main.SmartCursorWanted_Mouse = smartCursor;
            Main.myPlayer = originalPlayer; player.isControlledByFilm = film; Main.hasFocus = focus;
        }
    }

    public static JsonObject Observe(Player player, bool detailed = true)
    {
        int available = 0;
        foreach (var jump in player.ExtraJumps) if (jump.Available) available++;
        var result = new JsonObject
        {
            ["x"] = player.position.X, ["y"] = player.position.Y, ["vx"] = player.velocity.X, ["vy"] = player.velocity.Y,
            ["hp"] = player.statLife, ["dead"] = player.dead, ["grav_dir"] = player.gravDir,
            ["wing_time"] = player.wingTime, ["jump"] = player.jump, ["dash_delay"] = player.dashDelay,
            ["available_jumps"] = available, ["wet"] = player.wet, ["honey_wet"] = player.honeyWet,
            ["jump_state"] = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(player.ExtraJumps)),
            ["frozen"] = player.frozen, ["webbed"] = player.webbed,
            ["run_speed"] = player.maxRunSpeed, ["run_accel"] = player.runAcceleration,
            ["run_slowdown"] = player.runSlowdown, ["move_speed"] = player.moveSpeed,
            ["gravity"] = player.gravity, ["fall_speed"] = player.maxFallSpeed, ["jump_boost"] = player.jumpSpeedBoost,
            ["no_fall_damage"] = player.noFallDmg, ["merman"] = player.merman,
            ["fall_start"] = player.fallStart,
            ["direction"] = player.direction, ["release_jump"] = player.releaseJump,
            ["item_animation"] = player.itemAnimation, ["item_time"] = player.itemTime,
            ["keys"] = (player.controlLeft ? 1 : 0) | (player.controlRight ? 2 : 0) | (player.controlJump ? 4 : 0)
                | (player.controlUp ? 8 : 0) | (player.controlDown ? 16 : 0), ["use_item"] = player.controlUseItem,
            ["control_right"] = player.controlRight, ["control_jump"] = player.controlJump,
            ["grounded"] = Collision.SolidCollision(player.position + new Vector2(0, player.gravDir), player.width, player.height),
        };
        if (!detailed) return result;
        result["buff_types"] = new JsonArray(player.buffType.Select(value => JsonValue.Create(value)).ToArray());
        result["buff_times"] = new JsonArray(player.buffTime.Select(value => JsonValue.Create(value)).ToArray());
        result["extra_jumps"] = new JsonArray(player.ExtraJumps.ToArray().Select(value => (JsonNode)VanillaFrame.CaptureFields(value)).ToArray());
        return result;
    }

    public JsonObject Predict(int[] controls, bool detailed = true, bool captureEnd = false, bool restoreAfter = true,
        System.Collections.Generic.List<MovementSample> samples = null, bool stopOnDamage = false)
    {
        CheckCancellation();
        VanillaFrame.RequireWorker();
        if (controls.Length == 0 || controls.Length > 240) throw new ArgumentException("horizon_must_be_1_to_240");
        var trace = new JsonArray();
        samples?.Clear();
        var timer = Stopwatch.StartNew();
        string outcome = "complete";
        bool navigation = checkpoint["navigation"]?.GetValue<bool>() == true;
        JsonObject spawned = null;
        double restore = 0, update = 0, observe = 0, cleanup = 0;
        JsonObject end = null;
        try
        {
            long tick = Stopwatch.GetTimestamp();
            var player = compiled.Restore();
            int initialHp = player.statLife;
            restore = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
            foreach (int control in controls)
            {
                tick = Stopwatch.GetTimestamp();
                Advance(player, (VanillaKeys)control);
                update += Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                tick = Stopwatch.GetTimestamp();
                if (samples == null) trace.Add(Observe(player, detailed));
                else
                {
                    int available = 0;
                    foreach (var jump in player.ExtraJumps) if (jump.Available) available++;
                    samples.Add(new MovementSample(player.position, player.velocity, player.statLife, player.dead,
                        player.wingTime, available, Collision.SolidCollision(player.position + new Vector2(0, player.gravDir), player.width, player.height), player.wet));
                }
                observe += Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                if (player.dead) { outcome = "dead"; break; }
                if (stopOnDamage && player.statLife < initialHp) { outcome = "damaged"; break; }
                bool activeActor = false;
                foreach (var projectile in Main.projectile)
                    if (projectile.active) { activeActor = true; break; }
                if (!activeActor)
                    foreach (var npc in Main.npc)
                        if (npc.active) { activeActor = true; break; }
                if (activeActor || !navigation && Main.item.Any(item => item.active))
                {
                    spawned = new JsonObject {
                        ["items"] = new JsonArray(Main.item.Where(item => item.active).Take(8).Select(item => JsonValue.Create(item.type)).ToArray()),
                        ["npcs"] = new JsonArray(Main.npc.Where(npc => npc.active).Take(8).Select(npc => JsonValue.Create(npc.type)).ToArray()),
                        ["projectiles"] = new JsonArray(Main.projectile.Where(projectile => projectile.active).Take(8).Select(projectile => JsonValue.Create(projectile.type)).ToArray()) };
                    outcome = "unsupported_dynamic_spawn"; break;
                }
                var interior = area; interior.Inflate(-32, -32);
                if (!interior.Contains(player.Hitbox)) { outcome = "outside_observed_world"; break; }
            }
            if (captureEnd && outcome == "complete")
                end = VanillaFrame.Capture(player, new Rectangle(area.X / 16, area.Y / 16, area.Width / 16, area.Height / 16),
                    checkpoint["navigation"]?.GetValue<bool>() == true);
        }
        finally
        {
            long tick = Stopwatch.GetTimestamp();
            if (restoreAfter) compiled.Restore();
            cleanup = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
        }
        return new JsonObject { ["outcome"] = outcome, ["frames"] = trace, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds,
            ["dynamic_spawn"] = spawned,
            ["end_snapshot"] = end,
            ["timing"] = new JsonObject { ["restore_ms"] = restore, ["update_ms"] = update, ["observe_ms"] = observe, ["cleanup_ms"] = cleanup },
            ["scope"] = "isolated vanilla player updates; world actors and wiring are unsupported" };
    }
}
