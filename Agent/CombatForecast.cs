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
        JsonObject Destination, Task<JsonObject> Work, JsonObject Anchor = null, int HandoffFrames = 0);
    static readonly Dictionary<long, Forecast> retained = new();
    static CancellationTokenSource lifetime = new();
    static Forecast pending, ready;
    static Forecast prepared;
    static Forecast executing;
    static JsonObject executingMovement;
    static JsonObject executionAbort;
    static string context;
    static long sequence, lastAttempt;
    static long completionVersion;
    internal static long CompletionVersion => Interlocked.Read(ref completionVersion);
    static int refreshing;
    internal static bool ReservesRoutePrefix => Volatile.Read(ref refreshing) > 0
        || prepared != null && (long)Main.GameUpdateCount - prepared.Tick
            < (prepared.HandoffFrames > 0 ? prepared.HandoffFrames : prepared.Prefix.Length);
    static JsonObject unavailable;
    static JsonObject deferredArgs;
    static long deferredTick;

    public override void OnWorldUnload() => Close();
    public override void Unload() => Close();
    static void Close()
    {
        if (executing != null && ActExecutor.IsActive && AgentTools.ControlSession.OwnsDirectMovement) ActExecutor.Stop();
        executing = null; executingMovement = null; executionAbort = null;
        Reset();
    }
    public static void Reset()
    {
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new();
        pending = ready = prepared = null; retained.Clear(); context = null; unavailable = deferredArgs = null; lastAttempt = 0;
    }

    public override void PostUpdateEverything()
    {
        if (deferredArgs != null && pending == null && Volatile.Read(ref refreshing) == 0 && !ActExecutor.IsActive
            && Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0 && !Main.LocalPlayer.dead)
        {
            var args = deferredArgs; deferredArgs = null; lastAttempt = 0;
            var result = Poll(args, AgentTools.CombatAssessment(args["radius"]?.GetValue<int>() ?? 25));
            DiagLog.Write("[combat_capture_deferred] wait_frames=" + ((long)Main.GameUpdateCount - deferredTick)
                + " status=" + result["status"]);
        }
        if (executing == null) return;
        if (!AgentTools.ControlSession.OwnsDirectMovement)
        { executing = null; executingMovement = null; return; }
        int index = checked((int)((long)Main.GameUpdateCount - executing.Tick)) - 1;
        var trace = executingMovement["trajectory"].AsArray();
        if (index < 0 || index >= trace.Count) return;
        if (VanillaAsyncMovement.StateMatches(Main.LocalPlayer, trace[index].AsObject(), true, out var reason, out var mismatch)
            && VanillaFrame.ContextMatches(executing.Snapshot, Main.LocalPlayer, out reason, trace, firstFrame: index, frameCount: 6))
        {
            if (!ActExecutor.IsActive) { executing = null; executingMovement = null; }
            return;
        }
        executionAbort = new JsonObject { ["forecast_id"] = executing.Id,
            ["frame"] = index, ["reason"] = reason, ["difference"] = mismatch,
            ["observed"] = VanillaPrediction.Observe(Main.LocalPlayer, false), ["expected"] = trace[index].DeepClone() };
        DiagLog.Write("[combat_candidate_abort] " + executionAbort.ToJsonString());
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
                DiagLog.Write("[combat_forecast] " + result.ToJsonString());
            }
            catch (Exception error)
            {
                bool expired = error is OperationCanceledException && pending.HandoffFrames > 0 && !lifetime.IsCancellationRequested;
                unavailable = new JsonObject { ["status"] = "unavailable", ["reason"] = expired ? "handoff_deadline_elapsed" : error.GetBaseException().Message };
                if (expired) lastAttempt = 0;
                DiagLog.Write("[combat_forecast] " + unavailable.ToJsonString());
            }
            pending = null;
        }
        if (pending == null && Volatile.Read(ref refreshing) == 0 && Stopwatch.GetElapsedTime(lastAttempt).TotalMilliseconds >= 1000)
        {
            lastAttempt = Stopwatch.GetTimestamp();
            var player = Main.LocalPlayer;
            int requestedPrefix = args["route_handoff"]?.GetValue<bool>() == true ? 48 : 24;
            int[] prefix = StateSpacePlanner.PeekMovementControls(requestedPrefix);
            var walk = StateSpacePlanner.PeekWalkIntent();
            if (prefix.Length < 6 && walk != null)
                prefix = Enumerable.Repeat(walk.Value.Direction > 0 ? 2 : 1, requestedPrefix).ToArray();
            if (prefix.Length == 0 && !RecedingNav.Active && !ActExecutor.IsActive && !AgentPermissions.Dodge)
                prefix = new int[24];
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
                    var area = Rectangle.Intersect(new Rectangle(x - 32, y - 16, 64, 96),
                        new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY));
                    var snapshot = VanillaFrame.Capture(player, area, true);
                    long id = ++sequence, tick = (long)Main.GameUpdateCount;
                    var frozen = scene.DeepClone().AsObject();
                    var goal = destination.DeepClone().AsObject();
                    int handoff = args["route_handoff"]?.GetValue<bool>() == true ? Math.Min(16, prefix.Length) : 0;
                    unavailable = null;
                    pending = new Forecast(id, tick, snapshot, frozen, prefix, goal,
                        Generate(id, tick, snapshot, frozen, prefix, goal, walk,
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds, lifetime.Token, handoffFrames: handoff),
                        VanillaPrediction.Observe(player, false), handoff);
                    var submitted = pending;
                    _ = submitted.Work.ContinueWith(_ =>
                    {
                        if (ReferenceEquals(Volatile.Read(ref pending), submitted)) Interlocked.Increment(ref completionVersion);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    deferredArgs = null;
                }
                catch (NotSupportedException error)
                {
                    unavailable = new JsonObject { ["status"] = "unavailable", ["reason"] = error.Message };
                    if (error.Message == "active_item_use")
                    {
                        deferredArgs = args.DeepClone().AsObject(); deferredTick = (long)Main.GameUpdateCount;
                        unavailable["status"] = "pending"; unavailable["deferred"] = true;
                    }
                }
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
        JsonObject destination, (int Direction, float TargetX, float ArrivalPixels)? walk, double captureMs, CancellationToken token,
        string executionName = null, int handoffFrames = 0)
    {
        var clock = Stopwatch.StartNew();
        var movements = new JsonArray();
        bool execution = executionName != null;
        int horizon = execution ? prefix.Length + 24 : Math.Min(handoffFrames + 24, prefix.Length);
        var definitions = new[] { ("follow_route", -1), ("left", 1), ("right", 2), ("jump_left", 5), ("jump_right", 6) }
            .Where(d => !execution || d.Item1 == executionName).ToArray();
        int[] lead = execution ? prefix : prefix.Take(handoffFrames).ToArray();
        var inputs = definitions.ToDictionary(d => d.Item1, d => d.Item2 < 0 ? prefix.Take(horizon).ToArray()
            : lead.Concat(Enumerable.Repeat(d.Item2, 24)).Concat(Enumerable.Repeat(0, 48)).ToArray());
        using var deadline = handoffFrames > 0 ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        if (deadline != null) { deadline.CancelAfter(TimeSpan.FromSeconds(handoffFrames / 60d)); token = deadline.Token; }
        var batch = await VanillaNavigationDemo.Instance.Observe(new JsonObject { ["command"] = "combat_replays",
            ["snapshot"] = snapshot.DeepClone(), ["trace_damage"] = true,
            ["branches"] = new JsonArray(inputs.Select(pair => (JsonNode)new JsonObject {
                ["id"] = pair.Key, ["controls"] = new JsonArray(pair.Value.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()) }).ToArray()) }, token);
        if (!execution && walk != null)
        {
            double center = Number(scene["player"]["pos"]["x"]) + Number(scene["player"]["size_pixels"]["width"]) / 2;
            bool Arrived(double x) => walk.Value.Direction > 0 ? walk.Value.TargetX - x <= walk.Value.ArrivalPixels
                : walk.Value.TargetX - x >= -walk.Value.ArrivalPixels;
            if (Arrived(center)) throw new InvalidOperationException("walk_intent_already_arrived");
            var baseline = batch["branches"].AsArray().First(branch => branch["id"].ToString() == "follow_route")["frames"].AsArray();
            int reached = baseline.Select((f, i) => (f, i)).Where(e => Arrived(Number(e.f["x"])
                + Number(scene["player"]["size_pixels"]["width"]) / 2)).Select(e => e.i + 1).FirstOrDefault();
            if (reached > 0) horizon = Math.Min(horizon, reached);
        }
        foreach (var (name, keys) in definitions)
        {
            int[] controls = inputs[name];
            var response = batch["branches"].AsArray().First(branch => branch["id"].ToString() == name);
            var trace = response["frames"].AsArray();
            int minimum = lead.Length + 24;
            var landingCheck = keys < 0 ? (Frame: 0, Reason: (string)null)
                : EvaluateLanding(response.AsObject(), scene["player"]["hp"].GetValue<int>(), minimum);
            int landing = landingCheck.Frame;
            int count = landing > 0 ? landing : trace.Count;
            int compared = Math.Min(horizon, trace.Count);
            var end = compared > 0 ? trace[compared - 1] : null;
            var player = scene["player"];
            double startDistance = Distance(Number(player["pos"]["x"]) / 16, Number(player["pos"]["y"]) / 16, destination);
            var movement = new JsonObject { ["id"] = name, ["safe_landing"] = keys < 0 ? (bool?)null : landing > 0,
                ["landing_reason"] = landingCheck.Reason,
                ["outcome"] = response["outcome"]?.DeepClone(), ["landing_frame"] = landing > 0 ? landing : null,
                ["execution_frames"] = keys < 0 ? (int?)null : count, ["end"] = end?.DeepClone(),
                ["comparison_frames"] = horizon, ["observed_comparison_frames"] = compared, ["comparison_complete"] = compared == horizon,
                ["target_sample_frame"] = Math.Min(12, compared),
                ["route_progress_tiles"] = end == null ? null : startDistance - Distance(Number(end["x"]) / 16, Number(end["y"]) / 16, destination),
                ["damage"] = compared == 0 ? null : scene["player"]["hp"].GetValue<int>() - trace.Take(compared).Min(f => f["hp"].GetValue<int>()),
                ["hurt_events"] = new JsonArray(response["hurt_events"].AsArray()
                    .Where(e => e["frame"].GetValue<int>() <= compared).Select(e => e.DeepClone()).ToArray()),
                ["engine_ms"] = response["elapsed_ms"]?.DeepClone(),
                ["controls"] = new JsonArray(controls.Take(count).Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
                ["trajectory"] = new JsonArray(trace.Take(count).Select(f => f.DeepClone()).ToArray()) };
            movement["linear_threat_overlap"] = ThreatOverlap(scene, trace, compared);
            movement["path_samples_pixels"] = new JsonArray(trace.Take(compared).Select((f, i) => (f, i))
                .Where(e => e.i % 6 == 0 || e.i + 1 == compared).Select(e => (JsonNode)new JsonArray(e.i + 1,
                    Number(e.f["x"]), Number(e.f["y"]))).ToArray());
            movements.Add(movement);
        }
        return new JsonObject { ["status"] = "ready", ["id"] = id, ["sample_tick"] = tick,
            ["sample_player"] = scene["player"].DeepClone(),
            ["sample_targets"] = new JsonArray(scene["targets"].AsArray().Select(n => {
                var target = n.DeepClone().AsObject(); target.Remove("weapons"); return (JsonNode)target; }).ToArray()),
            ["sample_projectiles"] = scene["projectiles"].DeepClone(),
            ["snapshot_area"] = snapshot["area"].DeepClone(),
            ["prefix_source"] = walk != null ? "closed_loop_walk_intent" : prefix.All(k => k == 0) ? "idle_inputs" : "queued_controls",
            ["walk_intent"] = walk == null ? null : new JsonObject { ["direction"] = walk.Value.Direction,
                ["target_x_pixels"] = walk.Value.TargetX, ["arrival_pixels"] = walk.Value.ArrivalPixels },
            ["known_route_frames"] = prefix.Length, ["capture_ms"] = captureMs, ["compute_ms"] = clock.Elapsed.TotalMilliseconds,
            ["handoff_frames"] = handoffFrames,
            ["known_route_controls"] = new JsonArray(prefix.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
            ["destination"] = destination.DeepClone(), ["progress_metric"] = "straight_line_distance_to_goal",
            ["comparison_frames"] = horizon, ["mode"] = execution ? "execution" : "comparison",
            ["batch_prepare_ms"] = batch["prepare_ms"]?.DeepClone(), ["batch_engine_ms"] = batch["total_ms"]?.DeepClone(),
            ["batch_roundtrip_ms"] = batch["roundtrip_ms"]?.DeepClone(), ["batch_queue_ms"] = batch["queue_ms"]?.DeepClone(),
            ["movements"] = movements,
            ["scope"] = execution ? "Fresh execution proof: observed movement prefix, then selected input for 24 frames, then release until landing."
                : "All comparison metrics cover the same comparison_frames starting now, bounded by known route inputs and handoff_frames+24. Baseline replays queued keys or the current walk intent until its arrival threshold; later navigation is unknown. Alternatives first replay handoff_frames route inputs, then their input for 24 frames; safe_landing checks the longer landing continuation. damage includes ongoing health decay; hurt_events records new native hurts. Entity motion is a constant-velocity hypothesis." };
    }

    internal static (int Frame, string Reason) EvaluateLanding(JsonObject branch, int initialHp, int minimum)
    {
        var trace = branch["frames"].AsArray();
        var health = branch["health_frames"]?.AsArray() ?? throw new InvalidOperationException("combat_health_trace_missing");
        var hurts = branch["hurt_events"]?.AsArray() ?? throw new InvalidOperationException("combat_hurt_trace_missing");
        int firstHurt = hurts.Where(e => e["damage"].GetValue<int>() > 0).Select(e => e["frame"].GetValue<int>())
            .DefaultIfEmpty(int.MaxValue).Min();
        int previousHp = initialHp;
        for (int i = 0; i < trace.Count; i++)
        {
            int hp = trace[i]["hp"].GetValue<int>();
            if (trace[i]["dead"].GetValue<bool>() || hp <= 0) return (0, "predicted_death");
            if (i + 1 >= firstHurt) return (0, "new_hurt");
            if (i >= health.Count || health[i]["frame"].GetValue<int>() != i + 1 || health[i]["hp"].GetValue<int>() != hp)
                throw new InvalidOperationException("combat_health_trace_mismatch: frame=" + (i + 1));
            if (hp < previousHp)
            {
                if (health[i]["suffocating"].GetValue<bool>()) return (0, "suffocation");
                if (health[i]["breath"].GetValue<int>() <= 0) return (0, "drowning");
                if (health[i]["life_regen"].GetValue<int>() >= 0) return (0, "unattributed_health_loss");
            }
            previousHp = hp;
            if (i + 1 >= minimum && trace[i]["grounded"].GetValue<bool>()) return (i + 1, null);
        }
        return (0, "no_ground_contact_in_horizon");
    }

    static double Number(JsonNode value) => double.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    static JsonObject ThreatOverlap(JsonObject scene, JsonArray trace, int count)
    {
        int contacts = 0, shots = 0, first = 0;
        double halfWidth = Number(scene["player"]["size_pixels"]["width"]) / 2,
            halfHeight = Number(scene["player"]["size_pixels"]["height"]) / 2;
        void Compare(JsonNode enemy, bool projectile)
        {
            double x = Number(enemy["x"]) * 16, y = Number(enemy["y"]) * 16,
                vx = Number(enemy["vx_pixels_per_tick"]), vy = Number(enemy["vy_pixels_per_tick"]),
                width = halfWidth + Number(enemy["width_tiles"]) * 8,
                height = halfHeight + Number(enemy["height_tiles"]) * 8;
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(Number(trace[i]["x"]) + halfWidth - x - vx * (i + 1)) >= width
                    || Math.Abs(Number(trace[i]["y"]) + halfHeight - y - vy * (i + 1)) >= height) continue;
                if (projectile) shots++; else contacts++;
                if (first == 0 || i + 1 < first) first = i + 1;
            }
        }
        foreach (var target in scene["targets"].AsArray().Where(n => Number(n["contact_damage"]) > 0)) Compare(target, false);
        foreach (var shot in scene["projectiles"].AsArray()) Compare(shot, true);
        return new JsonObject { ["npc_object_frames"] = contacts, ["projectile_object_frames"] = shots,
            ["evaluated_frames"] = count,
            ["first_frame"] = first > 0 ? first : null, ["assumption"] = "constant entity velocity; overlaps are not confirmed damage" };
    }

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
            movement["exposure"] = TerrainInspection.MovementExposure(forecast.Scene, movement.AsObject());
            foreach (int type in weapons.Select(w => w.type).DefaultIfEmpty(0))
            {
                var weapon = weapons.FirstOrDefault(w => w.type == type);
                var targetChecks = new JsonArray();
                foreach (var target in forecast.Scene["targets"].AsArray().Take(3))
                {
                    int frame = movement["target_sample_frame"].GetValue<int>() - 1;
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

    public static async Task<JsonObject> Refresh(JsonObject args, Func<JsonObject> observe, CancellationToken token)
    {
        Interlocked.Increment(ref refreshing);
        try
        {
            var reused = await NativeAgent.OnMain(() => PrepareHandoff(args), token);
            if (reused != null) return reused;
            var queued = await NativeAgent.OnMain(() =>
                prepared?.Id == args["combat_candidate"]?["forecast_id"]?.GetValue<long>() ? null : pending?.Work, token);
            if (queued != null && !queued.IsCompleted) await Task.WhenAny(queued).WaitAsync(token);
            return await RefreshReady(args, observe, token);
        }
        finally { Interlocked.Decrement(ref refreshing); }
    }

    static JsonObject PrepareHandoff(JsonObject args)
    {
        var reference = args["combat_candidate"].AsObject();
        if (reference["context"]?.ToString() != context || !retained.TryGetValue(reference["forecast_id"].GetValue<long>(), out var forecast)
            || forecast.HandoffFrames == 0) return null;
        if (prepared?.Id == forecast.Id) return args;
        int age = checked((int)((long)Main.GameUpdateCount - forecast.Tick));
        if (age < 0 || age >= forecast.HandoffFrames)
            return new JsonObject { ["error"] = "combat_handoff_window_closed", ["age_frames"] = age, ["handoff_frames"] = forecast.HandoffFrames };
        var result = forecast.Work.Result;
        var plan = result["plans"].AsArray().FirstOrDefault(p => p["id"].ToString() == reference["plan_id"]?.ToString());
        if (plan == null || plan["movement"].ToString() == "follow_route") return new JsonObject { ["error"] = "combat_candidate_missing" };
        var movement = result["movements"].AsArray().First(m => m["id"].ToString() == plan["movement"].ToString()).AsObject();
        if (movement["safe_landing"]?.GetValue<bool>() != true) return new JsonObject { ["error"] = "combat_candidate_landing_unresolved" };
        var invalid = CheckState(forecast, movement, out _);
        if (invalid != null) return invalid;
        prepared = forecast;
        DiagLog.Write("[combat_candidate_reuse] " + new JsonObject { ["forecast_id"] = forecast.Id, ["age_frames"] = age,
            ["handoff_frames"] = forecast.HandoffFrames, ["plan_id"] = reference["plan_id"].DeepClone() });
        return args;
    }

    static async Task<JsonObject> RefreshReady(JsonObject args, Func<JsonObject> observe, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        string requestedContext = args["combat_candidate"]?["context"]?.ToString();
        CancellationTokenSource refreshLifetime = null;
        bool reserved = await NativeAgent.OnMain(() =>
        {
            if (requestedContext == context && prepared?.Id == args["combat_candidate"]?["forecast_id"]?.GetValue<long>()) return false;
            var player = Main.LocalPlayer;
            if (!(FightCoordinator.IsActive || FightCoordinator.IsIdleDefending)
                || FightCoordinator.NavigationSnapshotPending) return false;
            FightCoordinator.NavigationSnapshotPending = true;
            DiagLog.Write("[combat_capture_boundary] waiting tick=" + Main.GameUpdateCount + " animation=" + player.itemAnimation
                + " item_time=" + player.itemTime + " weapon=" + player.HeldItem.type);
            return true;
        }, token);
        (Forecast Job, JsonObject Error) request;
        try
        {
            while (reserved && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                await NativeAgent.NextUpdate(token);
            request = await NativeAgent.OnMain(() =>
            {
                var reference = args["combat_candidate"].AsObject();
                long id = reference["forecast_id"].GetValue<long>();
                if (requestedContext == context && prepared?.Id == id) return (Job: (Forecast)null, Error: (JsonObject)null);
                if (requestedContext != context || !retained.TryGetValue(id, out var original))
                    return (Job: (Forecast)null, Error: new JsonObject { ["error"] = "combat_forecast_replaced" });
                var plan = original.Work.Result["plans"].AsArray().FirstOrDefault(p => p["id"].ToString() == reference["plan_id"]?.ToString());
                if (plan == null || plan["movement"].ToString() == "follow_route")
                    return (Job: (Forecast)null, Error: new JsonObject { ["error"] = "combat_candidate_missing" });
                var player = Main.LocalPlayer;
                if (player.dead) return (Job: (Forecast)null, Error: new JsonObject { ["error"] = "combat_candidate_player_dead" });
                var walk = StateSpacePlanner.PeekWalkIntent();
                int[] prefix = StateSpacePlanner.PeekMovementControls(12);
                if (prefix.Length < 6 && walk != null) prefix = Enumerable.Repeat(walk.Value.Direction > 0 ? 2 : 1, 12).ToArray();
                if (prefix.Length == 0 && !RecedingNav.Active) prefix = new int[12];
                if (prefix.Length == 0) return (Job: (Forecast)null,
                    Error: new JsonObject { ["error"] = "combat_candidate_no_handoff_prefix",
                        ["observed"] = VanillaPrediction.Observe(player, false) });
                try
                {
                    long captureStarted = Stopwatch.GetTimestamp();
                    int x = (int)player.Center.X / 16, y = (int)player.Center.Y / 16;
                    var snapshot = VanillaFrame.Capture(player, Rectangle.Intersect(new Rectangle(x - 32, y - 16, 64, 96),
                        new Rectangle(0, 0, Main.maxTilesX, Main.maxTilesY)), true);
                    var anchor = VanillaPrediction.Observe(player, false);
                    long freshId = ++sequence, tick = (long)Main.GameUpdateCount;
                    var scene = observe();
                    refreshLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                    var job = new Forecast(freshId, tick, snapshot, scene, prefix, original.Destination,
                        Generate(freshId, tick, snapshot, scene, prefix, original.Destination, walk,
                            Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds, refreshLifetime.Token, plan["movement"].ToString()), anchor);
                    return (Job: job, Error: (JsonObject)null);
                }
                catch (NotSupportedException error)
                { return (Job: (Forecast)null, Error: new JsonObject { ["error"] = "combat_candidate_capture_unavailable", ["reason"] = error.Message }); }
            }, token);
        }
        finally
        {
            if (reserved) await NativeAgent.OnMain(() =>
            {
                FightCoordinator.NavigationSnapshotPending = false;
                DiagLog.Write("[combat_capture_boundary] released tick=" + Main.GameUpdateCount
                    + " elapsed_ms=" + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return true;
            }, default);
        }
        if (request.Error != null) return request.Error;
        if (request.Job == null) return args;
        try
        {
            var result = await request.Job.Work;
            return await NativeAgent.OnMain(() =>
            {
                if (requestedContext != context) return new JsonObject { ["error"] = "combat_forecast_replaced" };
                var movement = result["movements"][0].AsObject();
                if (movement["safe_landing"]?.GetValue<bool>() != true)
                {
                    var trace = movement["trajectory"].AsArray();
                    int grounded = trace.Skip(request.Job.Prefix.Length + 23).Count(f => f["grounded"]?.GetValue<bool>() == true);
                    var rejection = new JsonObject { ["error"] = "combat_candidate_landing_unresolved",
                        ["reason"] = movement["landing_reason"]?.DeepClone(),
                        ["execution_forecast_id"] = request.Job.Id, ["observed_frames"] = trace.Count,
                        ["requested_frames"] = request.Job.Prefix.Length + 72, ["grounded_frames_after_input"] = grounded,
                        ["start_hp"] = request.Job.Scene["player"]["hp"].DeepClone(),
                        ["min_hp"] = trace.Count == 0 ? null : trace.Min(f => f["hp"].GetValue<int>()) };
                    DiagLog.Write("[combat_candidate_landing_rejected] " + rejection.ToJsonString());
                    return rejection;
                }
                var invalid = CheckState(request.Job, movement, out int offset);
                if (invalid != null)
                {
                    invalid["execution_forecast_id"] = request.Job.Id;
                    invalid["age_frames"] = offset; invalid["lead_frames"] = request.Job.Prefix.Length;
                    invalid["elapsed_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    invalid["batch_queue_ms"] = result["batch_queue_ms"]?.DeepClone();
                    invalid["batch_engine_ms"] = result["batch_engine_ms"]?.DeepClone();
                    DiagLog.Write("[combat_candidate_refresh_rejected] " + invalid.ToJsonString());
                    return invalid;
                }
                var reference = args["combat_candidate"].AsObject();
                if (!retained.TryGetValue(reference["forecast_id"].GetValue<long>(), out var original))
                    return new JsonObject { ["error"] = "combat_forecast_replaced" };
                var plan = original.Work.Result["plans"].AsArray().First(p => p["id"].ToString() == reference["plan_id"].ToString());
                result["plans"] = new JsonArray(plan.DeepClone());
                prepared = request.Job;
                var refreshed = args.DeepClone().AsObject();
                refreshed["combat_candidate"]["decision_forecast_id"] = reference["forecast_id"].DeepClone();
                refreshed["combat_candidate"]["forecast_id"] = prepared.Id;
                refreshed["combat_candidate"]["lead_frames"] = prepared.Prefix.Length;
                DiagLog.Write("[combat_candidate_refresh] " + new JsonObject { ["decision_forecast_id"] = reference["forecast_id"].DeepClone(),
                    ["execution_forecast_id"] = prepared.Id, ["lead_frames"] = prepared.Prefix.Length,
                    ["age_frames"] = offset, ["elapsed_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds }.ToJsonString());
                return refreshed;
            }, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new JsonObject { ["error"] = "combat_forecast_replaced" }; }
        finally { refreshLifetime?.Dispose(); }
    }

    static JsonObject CheckState(Forecast forecast, JsonObject movement, out int offset)
    {
        offset = checked((int)((long)Main.GameUpdateCount - forecast.Tick));
        var trajectory = movement["trajectory"].AsArray();
        if (offset < 0 || offset >= Math.Min(trajectory.Count, forecast.Prefix.Length + 24))
            return new JsonObject { ["error"] = "combat_candidate_expired", ["age_frames"] = offset };
        var expected = offset == 0 ? forecast.Anchor : trajectory[offset - 1].AsObject();
        var player = Main.LocalPlayer;
        string reason = null, mismatch = null;
        if (expected == null || !VanillaAsyncMovement.StateMatches(player, expected, true, out reason, out mismatch))
            return new JsonObject { ["error"] = "combat_candidate_changed", ["reason"] = expected == null ? "missing_anchor" : reason,
                ["difference"] = expected == null ? null : mismatch,
                ["observed"] = VanillaPrediction.Observe(player, false), ["expected"] = expected?.DeepClone() };
        if (!VanillaFrame.ContextMatches(forecast.Snapshot, player, out reason, trajectory, firstFrame: offset))
            return new JsonObject { ["error"] = "combat_candidate_changed", ["reason"] = reason };
        return null;
    }

    public static JsonObject Prepare(JsonObject args)
    {
        var reference = args["combat_candidate"].AsObject();
        if (reference["context"]?.ToString() != context || prepared?.Id != reference["forecast_id"].GetValue<long>())
            return new JsonObject { ["error"] = "combat_forecast_replaced" };
        var forecast = prepared;
        var result = forecast.Work.Result;
        var plan = result["plans"].AsArray().FirstOrDefault(p => p["id"].ToString() == reference["plan_id"]?.ToString());
        if (plan == null || plan["movement"].ToString() == "follow_route") return new JsonObject { ["error"] = "combat_candidate_missing" };
        var movement = result["movements"].AsArray().First(m => m["id"].ToString() == plan["movement"].ToString());
        if (!movement["safe_landing"].GetValue<bool>()) return new JsonObject { ["error"] = "combat_candidate_landing_unresolved" };
        var invalid = CheckState(forecast, movement.AsObject(), out int offset);
        if (invalid != null) return invalid;
        if (forecast.HandoffFrames > 0 && offset >= forecast.HandoffFrames)
            return new JsonObject { ["error"] = "combat_handoff_window_closed", ["age_frames"] = offset, ["handoff_frames"] = forecast.HandoffFrames };
        var trajectory = movement["trajectory"].AsArray();
        var player = Main.LocalPlayer;
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
        executing = forecast; executingMovement = movement.AsObject(); executionAbort = null;
        prepared = null;
        var payload = args.DeepClone().AsObject(); payload["steps"] = steps;
        DiagLog.Write("[combat_candidate_apply] " + new JsonObject { ["forecast_id"] = forecast.Id,
            ["plan_id"] = plan["id"].DeepClone(), ["age_frames"] = offset, ["remaining_frames"] = trajectory.Count - offset,
            ["start_hp"] = forecast.Scene["player"]["hp"].DeepClone(),
            ["life_regen_raw"] = forecast.Scene["player"]["life_regen_raw"]?.DeepClone(),
            ["predicted_end_hp"] = trajectory[^1]["hp"].DeepClone() });
        return payload;
    }

    public static JsonObject Receipt(JsonObject args, JsonObject result)
    {
        result["decision_forecast_id"] = args["combat_candidate"]?["decision_forecast_id"]?.DeepClone();
        result["execution_forecast_id"] = args["combat_candidate"]?["forecast_id"]?.DeepClone();
        if (executionAbort?["forecast_id"]?.ToString() != args["combat_candidate"]?["forecast_id"]?.ToString()) return result;
        result["error"] = "combat_candidate_changed_during_execution";
        result["forecast_abort"] = executionAbort.DeepClone();
        return result;
    }
}
