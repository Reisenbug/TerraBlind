using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class CombatForecast : ModSystem
{
    sealed record Forecast(long Id, long Tick, JsonObject Snapshot, JsonObject Scene, int[] Prefix,
        JsonObject Destination, Task<JsonObject> Work);
    static readonly Dictionary<long, Forecast> retained = new();
    static CancellationTokenSource lifetime = new();
    static Forecast pending, ready;
    static Forecast executing;
    static JsonObject executingMovement;
    static string context;
    static long sequence, lastAttempt;
    static JsonObject unavailable;

    public override void OnWorldUnload() => Reset();
    public override void Unload() => Reset();
    public static void Reset()
    {
        if (executing != null && ActExecutor.IsActive && AgentTools.ControlSession.OwnsDirectMovement) ActExecutor.Stop();
        executing = null; executingMovement = null;
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new();
        pending = ready = null; retained.Clear(); context = null; unavailable = null; lastAttempt = 0;
    }

    public override void PostUpdateEverything()
    {
        if (executing == null) return;
        if (!ActExecutor.IsActive || !AgentTools.ControlSession.OwnsDirectMovement)
        { executing = null; executingMovement = null; return; }
        int index = checked((int)((long)Main.GameUpdateCount - executing.Tick)) - 1;
        var trace = executingMovement["trajectory"].AsArray();
        if (index < 0 || index >= trace.Count) return;
        if (VanillaAsyncMovement.StateMatches(Main.LocalPlayer, trace[index].AsObject(), true, out var reason, out var mismatch)
            && VanillaFrame.ContextMatches(executing.Snapshot, Main.LocalPlayer, out reason, trace, firstFrame: index, frameCount: 6)) return;
        DiagLog.Write("[combat_candidate_abort] " + new JsonObject { ["forecast_id"] = executing.Id,
            ["frame"] = index, ["reason"] = reason, ["difference"] = mismatch });
        ActExecutor.Stop(); executing = null; executingMovement = null;
    }

    public static JsonObject Poll(JsonObject args, JsonObject scene)
    {
        string nextContext = args["context"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(nextContext) || args["destination"] is not JsonObject destination)
            throw new ArgumentException("候选推演需要context和destination世界格坐标。");
        if (context != nextContext) { Reset(); context = nextContext; }
        if (pending?.Work.IsCompleted == true)
        {
            try
            {
                var result = pending.Work.GetAwaiter().GetResult();
                result["plans"] = Plans(pending, result["movements"].AsArray());
                retained[pending.Id] = pending;
                foreach (long id in retained.Keys.Order().SkipLast(2).ToArray()) retained.Remove(id);
                ready = pending; unavailable = null;
                DiagLog.Write("[combat_forecast] " + result);
            }
            catch (Exception error)
            {
                unavailable = new JsonObject { ["status"] = "unavailable", ["reason"] = error.GetBaseException().Message };
                DiagLog.Write("[combat_forecast] " + unavailable);
            }
            pending = null;
        }
        if (pending == null && Stopwatch.GetElapsedTime(lastAttempt).TotalMilliseconds >= 1000)
        {
            lastAttempt = Stopwatch.GetTimestamp();
            var player = Main.LocalPlayer;
            int[] prefix = StateSpacePlanner.PeekMovementControls(48);
            if (!VanillaNavigationDemo.Instance.WorkerReady)
                unavailable = new JsonObject { ["status"] = "unavailable", ["reason"] = "worker_warming" };
            else if (prefix.Length < 6)
                unavailable = new JsonObject { ["status"] = "unavailable", ["reason"] = "queued_movement_too_short", ["known_frames"] = prefix.Length };
            else
            {
                try
                {
                    long started = Stopwatch.GetTimestamp();
                    int x = (int)player.Center.X / 16, y = (int)player.Center.Y / 16;
                    var area = Rectangle.Intersect(new Rectangle(x - 48, y - 24, 96, 144),
                        new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
                    var snapshot = VanillaFrame.Capture(player, area, true);
                    long id = ++sequence, tick = (long)Main.GameUpdateCount;
                    var frozen = scene.DeepClone().AsObject();
                    var goal = destination.DeepClone().AsObject();
                    unavailable = null;
                    pending = new Forecast(id, tick, snapshot, frozen, prefix, goal,
                        Generate(id, tick, snapshot, frozen, prefix, goal,
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds, lifetime.Token));
                }
                catch (NotSupportedException error)
                { unavailable = new JsonObject { ["status"] = "unavailable", ["reason"] = error.Message }; }
            }
            if (unavailable != null) DiagLog.Write("[combat_forecast_unavailable] " + new JsonObject {
                ["tick"] = Main.GameUpdateCount, ["reason"] = unavailable["reason"]?.DeepClone(),
                ["queued_frames"] = prefix.Length, ["held_type"] = player.HeldItem.type,
                ["item_animation"] = player.itemAnimation, ["item_time"] = player.itemTime });
        }
        if (ready != null && (long)Main.GameUpdateCount - ready.Tick < 120)
        {
            var result = ready.Work.Result.DeepClone().AsObject();
            foreach (var movement in result["movements"].AsArray())
            {
                movement.AsObject().Remove("trajectory"); movement.AsObject().Remove("controls");
                if (movement["end"] is JsonObject end)
                    movement["end"] = new JsonObject { ["x"] = end["x"].DeepClone(), ["y"] = end["y"].DeepClone(),
                        ["vx"] = end["vx"].DeepClone(), ["vy"] = end["vy"].DeepClone(), ["grounded"] = end["grounded"].DeepClone() };
            }
            result["age_frames"] = (long)Main.GameUpdateCount - ready.Tick;
            result["in_flight"] = pending != null;
            return result;
        }
        return pending != null ? new JsonObject { ["status"] = "pending", ["in_flight"] = true }
            : unavailable?.DeepClone().AsObject() ?? new JsonObject { ["status"] = "unavailable", ["reason"] = "no_current_forecast" };
    }

    static async Task<JsonObject> Generate(long id, long tick, JsonObject snapshot, JsonObject scene, int[] prefix,
        JsonObject destination, double captureMs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var movements = new JsonArray();
        foreach (var (name, keys) in new[] { ("follow_route", -1), ("left", 1), ("right", 2), ("jump_left", 5), ("jump_right", 6) })
        {
            int[] controls = prefix.Concat(Enumerable.Repeat(keys < 0 ? 0 : keys, keys < 0 ? 0 : 24))
                .Concat(Enumerable.Repeat(0, 48)).ToArray();
            var response = await VanillaNavigationDemo.Instance.Observe(new JsonObject { ["command"] = "replay",
                ["snapshot"] = snapshot.DeepClone(), ["controls"] = new JsonArray(controls.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
                ["detailed"] = false, ["stop_on_damage"] = true }, token);
            var trace = response["frames"].AsArray();
            int minimum = prefix.Length + (keys < 0 ? 0 : 24);
            int landing = trace.Select((frame, index) => (frame, index))
                .Where(e => e.index + 1 >= minimum && e.frame["grounded"]?.GetValue<bool>() == true
                    && e.frame["dead"]?.GetValue<bool>() == false && e.frame["hp"].GetValue<int>() >= scene["player"]["hp"].GetValue<int>())
                .Select(e => e.index + 1).FirstOrDefault();
            int count = landing > 0 ? landing : trace.Count;
            var end = count > 0 ? trace[count - 1] : null;
            var player = scene["player"];
            double startDistance = Distance(Number(player["pos"]["x"]) / 16, Number(player["pos"]["y"]) / 16, destination);
            var movement = new JsonObject { ["id"] = name, ["safe_landing"] = landing > 0,
                ["outcome"] = response["outcome"]?.DeepClone(), ["landing_frame"] = landing > 0 ? landing : null,
                ["execution_frames"] = count, ["end"] = end?.DeepClone(),
                ["route_progress_tiles"] = end == null ? null : startDistance - Distance(Number(end["x"]) / 16, Number(end["y"]) / 16, destination),
                ["damage"] = trace.Count == 0 ? null : scene["player"]["hp"].GetValue<int>() - trace.Min(f => f["hp"].GetValue<int>()),
                ["engine_ms"] = response["elapsed_ms"]?.DeepClone(), ["roundtrip_ms"] = response["roundtrip_ms"]?.DeepClone(),
                ["queue_ms"] = response["queue_ms"]?.DeepClone(),
                ["controls"] = new JsonArray(controls.Take(count).Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
                ["trajectory"] = new JsonArray(trace.Take(count).Select(f => f.DeepClone()).ToArray()) };
            movements.Add(movement);
        }
        return new JsonObject { ["status"] = "ready", ["id"] = id, ["sample_tick"] = tick,
            ["sample_player"] = scene["player"].DeepClone(),
            ["known_route_frames"] = prefix.Length, ["capture_ms"] = captureMs, ["compute_ms"] = clock.Elapsed.TotalMilliseconds,
            ["movements"] = movements,
            ["scope"] = "Real vanilla player physics. All alternatives first replay the queued route prefix; movement lasts 24 frames, then releases keys until landing. Enemy and projectile AI are not simulated. Follow_route describes only the known prefix and braking continuation." };
    }

    static double Number(JsonNode value) => double.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    static double Distance(double x, double y, JsonObject goal)
        => Math.Sqrt(Math.Pow(x - Number(goal["x"]), 2) + Math.Pow(y - Number(goal["y"]), 2));

    static JsonArray Plans(Forecast forecast, JsonArray movements)
    {
        var player = Main.LocalPlayer;
        var weapons = player.inventory.Take(10).Where(CombatLoadout.Usable).Where(w =>
            (w.useAmmo <= 0 || player.HasAmmo(w)) && w.mana * player.manaCost <= player.statMana).GroupBy(w => w.type).Select(g => g.First()).ToArray();
        var plans = new JsonArray();
        foreach (var movement in movements)
        {
            foreach (int type in weapons.Select(w => w.type).DefaultIfEmpty(0))
            {
                var weapon = weapons.FirstOrDefault(w => w.type == type);
                var targetChecks = new JsonArray();
                foreach (var target in forecast.Scene["targets"].AsArray().Take(3))
                {
                    int frame = Math.Min(forecast.Prefix.Length + 12, movement["trajectory"].AsArray().Count) - 1;
                    if (frame < 0) continue;
                    var predicted = movement["trajectory"][frame];
                    var source = new Vector2(predicted["x"].GetValue<float>() + player.width / 2f,
                        predicted["y"].GetValue<float>() + player.height / 2f);
                    var position = new Vector2(target["x"].GetValue<float>() * 16, target["y"].GetValue<float>() * 16)
                        + new Vector2(target["vx_pixels_per_tick"].GetValue<float>(), target["vy_pixels_per_tick"].GetValue<float>()) * (frame + 1);
                    movement["target_sample_frame"] = frame + 1;
                    movement["target_distances_tiles"] ??= new JsonObject();
                    movement["target_distances_tiles"][target["who"].ToString()] = Vector2.Distance(source, position) / 16;
                    targetChecks.Add(new JsonObject { ["who"] = target["who"].DeepClone(),
                        ["line_or_reach_passed"] = weapon != null && FightCoordinator.CanHitTargetFrom(player, weapon, source, position, target["width_tiles"].GetValue<float>() * 8) });
                }
                plans.Add(new JsonObject { ["id"] = movement["id"] + "_weapon_" + type,
                    ["movement"] = movement["id"].DeepClone(), ["weapon_type"] = type, ["weapon_name"] = weapon?.Name,
                    ["movement_available"] = movement["id"].ToString() == "follow_route" || movement["safe_landing"].GetValue<bool>(),
                    ["target_checks"] = targetChecks, ["target_motion"] = "constant-velocity hypothesis" });
            }
        }
        return plans;
    }

    public static JsonObject Prepare(JsonObject args)
    {
        var reference = args["combat_candidate"].AsObject();
        if (reference["context"]?.ToString() != context || !retained.TryGetValue(reference["forecast_id"].GetValue<long>(), out var forecast))
            return new JsonObject { ["error"] = "combat_forecast_replaced" };
        var result = forecast.Work.Result;
        var plan = result["plans"].AsArray().FirstOrDefault(p => p["id"].ToString() == reference["plan_id"]?.ToString());
        if (plan == null || plan["movement"].ToString() == "follow_route") return new JsonObject { ["error"] = "combat_candidate_missing" };
        var movement = result["movements"].AsArray().First(m => m["id"].ToString() == plan["movement"].ToString());
        if (!movement["safe_landing"].GetValue<bool>()) return new JsonObject { ["error"] = "combat_candidate_landing_unresolved" };
        int offset = checked((int)((long)Main.GameUpdateCount - forecast.Tick));
        var trajectory = movement["trajectory"].AsArray();
        if (offset < 1 || offset >= trajectory.Count)
            return new JsonObject { ["error"] = "combat_candidate_expired", ["age_frames"] = offset };
        var player = Main.LocalPlayer;
        if (!VanillaAsyncMovement.StateMatches(player, trajectory[offset - 1].AsObject(), true, out var reason, out var mismatch)
            || !VanillaFrame.ContextMatches(forecast.Snapshot, player, out reason, trajectory, firstFrame: offset))
            return new JsonObject { ["error"] = "combat_candidate_changed", ["reason"] = reason, ["difference"] = mismatch };
        int type = plan["weapon_type"].GetValue<int>();
        if (type > 0 && !player.inventory.Take(10).Any(w => w.type == type && CombatLoadout.Usable(w)
            && (w.useAmmo <= 0 || player.HasAmmo(w)) && w.mana * player.manaCost <= player.statMana))
            return new JsonObject { ["error"] = "combat_candidate_weapon_unavailable" };
        var steps = new JsonArray();
        foreach (int keys in movement["controls"].AsArray().Skip(offset).Select(k => k.GetValue<int>()))
        {
            var names = new JsonArray();
            foreach (var (mask, name) in new[] { (1, "left"), (2, "right"), (4, "jump"), (8, "up"), (16, "down") })
                if ((keys & mask) != 0) names.Add(name);
            if (steps.Count > 0 && JsonNode.DeepEquals(steps[^1]["keys"], names))
                steps[^1]["until"]["frames"] = steps[^1]["until"]["frames"].GetValue<int>() + 1;
            else steps.Add(new JsonObject { ["keys"] = names, ["until"] = new JsonObject { ["frames"] = 1 } });
        }
        if (type > 0) CombatLoadout.PreferredType = type;
        executing = forecast; executingMovement = movement.AsObject();
        var prepared = args.DeepClone().AsObject(); prepared["steps"] = steps;
        DiagLog.Write("[combat_candidate_apply] " + new JsonObject { ["forecast_id"] = forecast.Id,
            ["plan_id"] = plan["id"].DeepClone(), ["age_frames"] = offset, ["remaining_frames"] = trajectory.Count - offset });
        return prepared;
    }
}
