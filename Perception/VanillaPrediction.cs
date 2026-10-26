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

    internal static void ApplyKeys(Player player)
    {
        var keys = PendingKeys;
        player.controlLeft = keys.HasFlag(VanillaKeys.Left); player.controlRight = keys.HasFlag(VanillaKeys.Right);
        player.controlJump = keys.HasFlag(VanillaKeys.Jump); player.controlUp = keys.HasFlag(VanillaKeys.Up);
        player.controlDown = keys.HasFlag(VanillaKeys.Down);
        player.controlUseItem = player.controlUseTile = player.controlHook = player.controlMount = false;
    }

    public VanillaPrediction(JsonObject state)
    {
        VanillaFrame.RequireWorker();
        checkpoint = (JsonObject)state.DeepClone();
        compiled = new VanillaFrame.WorkerState(checkpoint);
        area = new Rectangle(state["area"][0].GetValue<int>() * 16, state["area"][1].GetValue<int>() * 16,
            state["area"][2].GetValue<int>() * 16, state["area"][3].GetValue<int>() * 16);
    }

    public static void Advance(Player player, VanillaKeys keys)
    {
        CheckCancellation();
        VanillaFrame.RequireWorker();
        if (((int)keys & ~31) != 0 || keys.HasFlag(VanillaKeys.Left) && keys.HasFlag(VanillaKeys.Right))
            throw new ArgumentException("invalid_keys");
        int originalPlayer = Main.myPlayer;
        bool film = player.isControlledByFilm;
        bool focus = Main.hasFocus;
        try
        {
            Main.myPlayer = player.whoAmI;
            player.isControlledByFilm = false;
            Main.hasFocus = false;
            PendingKeys = keys;
            clock.SetValue(null, Convert.ChangeType(Main.GameUpdateCount + 1, clock.FieldType));
            player.Update(player.whoAmI);
        }
        finally { Main.myPlayer = originalPlayer; player.isControlledByFilm = film; Main.hasFocus = focus; }
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
                if (Main.projectile.Any(projectile => projectile.active) || Main.npc.Any(npc => npc.active)
                    || !navigation && Main.item.Any(item => item.active))
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
