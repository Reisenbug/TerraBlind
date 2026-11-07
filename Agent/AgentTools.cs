using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind;

public static partial class AgentTools
{
    internal static readonly System.Text.Json.JsonSerializerOptions ModelJson = new(System.Text.Json.JsonSerializerOptions.Default)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal const int ApiRevision = 2;
    internal static readonly AgentTaskPlan Plan = new();
    internal static readonly Dictionary<string, ScriptProgress> Workflows = new();
    static readonly HashSet<string> continuedActions = new();
    static Task continuation = Task.CompletedTask;
    static string pendingNode;
    internal static JsonObject LastContinuation;
    static readonly HashSet<(int, int)> routeChests = new();
    static bool routeLootEnabled;
    internal static JsonObject LastRouteLoot;

    internal static void LootPassingChest()
    {
        if (!Watches.LootChestsInReach)
        { routeLootEnabled = false; return; }
        if (!routeLootEnabled) { routeChests.Clear(); routeLootEnabled = true; }
        var p = Main.LocalPlayer;
        if (!Actions.HasPrimaryControl || !Plan.Incomplete || Main.netMode != Terraria.ID.NetmodeID.SinglePlayer
            || p.dead || Manual.On || p.chest >= 0 || TreasureGrab.IsRunning || ContactEscapeActive
            || !ControlAllows(ControlBehavior.Workflow, ControlChannels.ItemUse, delegated: true)) return;
        foreach (var chest in Main.chest)
        {
            if (chest == null || routeChests.Contains((chest.x, chest.y)) || !chest.item.Any(i => !i.IsAir)
                || !Reach.CanInteract(p, chest.x, chest.y)) continue;
            routeChests.Add((chest.x, chest.y));
            var before = AgentTaskEvidence.ObserveChest(chest.x, chest.y);
            bool inventoryOpen = Main.playerInventory;
            int opened = TreasureGrab.OpenAt(chest.x, chest.y, out string reason);
            bool verified = false;
            try
            {
                if (opened >= 0)
                {
                    Terraria.UI.ChestUI.LootAll();
                    verified = AgentTaskEvidence.VerifyChest(before);
                    reason = !verified ? "no_inventory_transfer" : chest.item.Any(i => !i.IsAir) ? "partial" : "looted";
                }
            }
            finally
            {
                if (opened >= 0) TreasureGrab.CloseChest();
                Main.playerInventory = inventoryOpen;
            }
            LastRouteLoot = new JsonObject { ["x"] = chest.x, ["y"] = chest.y,
                ["outcome"] = reason, ["verified_transfer"] = verified, ["tick"] = Main.GameUpdateCount,
                ["route_handle"] = Actions.ActiveHandle };
            DiagLog.Write("[route_loot] " + LastRouteLoot.ToJsonString());
            return;
        }
    }
    internal static JsonObject ExecutionContinuity;
    static ulong? gapStarted;
    static bool gapReported;

    internal static void ObserveContinuity()
    {
        var last = Actions.LatestPrimary();
        if (last == null) return;
        bool scriptOwnsInput = Actions.HasPrimaryControl && last["tool"]?.ToString() == "run_script";
        if (Actions.HasPrimaryControl && (!scriptOwnsInput || ControlSession.Active) || !Plan.Incomplete || Main.LocalPlayer.dead)
        {
            if (gapStarted != null)
                DiagLog.Write($"[execution_gap_end] frames={Main.GameUpdateCount - gapStarted.Value} handle={Actions.ActiveHandle}");
            gapStarted = null;
            gapReported = false;
            ExecutionContinuity = new JsonObject { ["status"] = Main.LocalPlayer.dead ? "waiting_respawn"
                : !Plan.Incomplete ? "completed" : "executing", ["active_handle"] = Actions.ActiveHandle };
            return;
        }
        gapStarted ??= Main.GameUpdateCount;
        bool dispatching = !continuation.IsCompleted || pendingNode != null;
        bool scriptRunning = Workflows.Values.Any(w => w.Running);
        string reason = dispatching ? "dispatching_prepared_script" : scriptRunning ? "script_running_without_background_action"
            : Watches.HasInterrupting ? "event_review_without_action" : "no_executable_action";
        ExecutionContinuity = new JsonObject { ["status"] = "execution_gap", ["reason"] = reason,
            ["started_tick"] = gapStarted.Value, ["gap_frames"] = Main.GameUpdateCount - gapStarted.Value,
            ["last_handle"] = last["handle"]?.DeepClone(), ["last_tool"] = last["tool"]?.DeepClone(),
            ["last_status"] = last["status"]?.DeepClone(), ["task_node"] = last["task_node"]?.DeepClone(),
            ["current_node"] = Plan.Snapshot()["current_step"]?.DeepClone() };
        if (!gapReported && !dispatching && !scriptRunning)
        {
            gapReported = true;
            DiagLog.Write("[execution_gap] " + ExecutionContinuity.ToJsonString());
            if (!Watches.HasInterrupting) Watches.ExecutionFault(ExecutionContinuity);
        }
    }
    internal static void ContinuePreparedNode(CancellationToken token)
    {
        if (token.IsCancellationRequested || !continuation.IsCompleted || Actions.HasPrimaryControl
            || !Plan.RequiresNodeExecution || Main.LocalPlayer.dead || ContactEscapeActive) return;
        if (pendingNode != null)
        {
            if (Watches.HasInterrupting) return;
            string ready = pendingNode;
            pendingNode = null;
            if (Plan.Snapshot()["ready_steps"].AsArray().Any(n => n?.ToString() == ready))
                StartPreparedNode(ready, LastContinuation?["after_handle"]?.ToString(), token);
            return;
        }
        var action = Actions.LatestFinishedPrimary();
        string handle = action?["handle"]?.ToString(), owner = action?["task_node"]?.ToString();
        string attempt = handle + ":" + action?["attempt"];
        if (handle == null || string.IsNullOrEmpty(owner) || continuedActions.Contains(attempt)) return;
        var plan = Plan.Snapshot();
        var previous = plan["steps"].AsArray().FirstOrDefault(s => s["id"]?.ToString() == owner);
        if (previous == null) return;
        Workflows.TryGetValue(previous["workflow"]?.ToString() ?? "", out var workflow);
        if (workflow?.Running == true) return;
        if (previous["status"]?.ToString() != "completed" && !plan["ready_steps"].AsArray().Any(n => n?.ToString() == owner)) return;
        bool verified = previous["status"]?.ToString() == "completed"
            || Plan.Advance(owner, AgentTaskEvidence.Measure)["verified"]?.GetValue<bool>() == true;
        continuedActions.Add(attempt);
        if (!verified && (workflow == null || workflow.Yielded || workflow.ReviewRequested
            || action["status"]?.ToString() != "completed")) return;
        Watches.AcknowledgeCompletion(action, nodeVerified: verified);
        if (Watches.HasInterrupting) return;
        plan = Plan.Snapshot();
        string next = verified ? plan["current_step"]?.ToString() : owner;
        if (next == null || !plan["ready_steps"].AsArray().Any(n => n?.ToString() == next)) return;
        StartPreparedNode(next, handle, token);
    }

    static void StartPreparedNode(string next, string handle, CancellationToken token)
    {
        LastContinuation = new JsonObject { ["after_handle"] = handle, ["task_node"] = next,
            ["status"] = "starting", ["tick"] = Main.GameUpdateCount };
        var report = LastContinuation;
        continuation = Task.Run(async () =>
        {
            try
            {
                string result = await Execute("run_script", new JsonObject { ["task_node"] = next }, token);
                await NativeAgent.OnMain(() =>
                {
                    var returned = JsonNode.Parse(result);
                    report["status"] = "returned"; report["ok"] = returned?["ok"]?.DeepClone();
                    report["error"] = returned?["error"]?.DeepClone();
                    if (returned?["ok"]?.ToString() == "false" || returned?["value"] is JsonObject value
                        && (value["status"]?.ToString() == "yield" || value["wake"] != null || value["error"] != null))
                        ReportExecutionGap(report, "script_requires_review");
                    else if (!Actions.HasPrimaryControl)
                    {
                        var plan = Plan.Snapshot();
                        if (plan["ready_steps"].AsArray().Any(n => n?.ToString() == next))
                            plan = Plan.Advance(next, AgentTaskEvidence.Measure);
                        if (plan["steps"].AsArray().Any(n => n["id"]?.ToString() == next && n["status"]?.ToString() == "completed"))
                            pendingNode = plan["current_step"]?.ToString();
                        else ReportExecutionGap(report, "script_returned_without_action_or_completion");
                    }
                    DiagLog.Write($"[dag-continuation] after={handle} node={next} result={result}");
                    return true;
                }, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                await NativeAgent.OnMain(() =>
                {
                    report["status"] = "failed"; report["error"] = error.Message;
                    ReportExecutionGap(report, "script_failed");
                    return true;
                }, default);
            }
        }, token);
    }

    static void ReportExecutionGap(JsonObject report, string reason)
    {
        report["status"] = "execution_gap";
        report["reason"] = reason;
        report["detected_tick"] = Main.GameUpdateCount;
        report["active_handle"] = Actions.ActiveHandle;
        report["plan_revision"] = Plan.Snapshot()["revision"]?.DeepClone();
        Watches.ExecutionFault(report);
        DiagLog.Write("[execution_gap] " + report.ToJsonString());
    }
    internal static readonly AgentWatch Watches = new(AssessNotice);
    static JsonObject AssessNotice(JsonObject notice)
    {
        if (notice["evidence"]?["x"] is not JsonValue xv || !xv.TryGetValue<int>(out int x)
            || notice["evidence"]?["y"] is not JsonValue yv || !yv.TryGetValue<int>(out int y)
            || x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1)
            return new JsonObject { ["ok"] = false, ["error"] = "invalid_opportunity_coordinates", ["reachable"] = null };
        var destination = notice["navigation"]?["destination"] as JsonObject;
        var assessment = TerrainInspection.Opportunity(x, y, destination);
        foreach (string field in new[] { "hazards", "liquids", "direct_corridor_solid_cells" })
            if (assessment[field] is JsonArray facts)
            {
                assessment[field + "_count"] = facts.Count;
                if (facts.Count <= 8) continue;
                assessment[field] = new JsonArray(facts.Take(8).Select(f => f.DeepClone()).ToArray());
                assessment[field + "_truncated"] = true;
                assessment["detail_request"] = new JsonObject { ["tool"] = "opportunity_assess",
                    ["args"] = new JsonObject { ["x"] = x, ["y"] = y, ["destination"] = destination?.DeepClone() } };
            }
        return assessment;
    }
    internal static readonly AgentRecovery Recovery = new();
    internal static readonly AgentControlPolicy Control = new();
    internal static readonly AgentControlSession ControlSession = new(Control);
    internal static JsonObject LastDamage, LastDeath, LastContactEscape, LastAutomaticHeal;
    internal static bool ContactEscapeActive;
    internal static JsonObject RecentContactDamage(long tick)
    {
        var damage = LastDamage;
        if (damage?["kind"]?.ToString() != "hurt" || damage["other_source"]?.GetValue<int>() != 3
            || (damage["damage"]?.GetValue<double>() ?? 0) <= 0) return null;
        long age = tick - damage["tick"].GetValue<long>();
        return age is >= 0 and <= 2 ? damage : null;
    }
    static (bool Attack, bool Dodge, float Reserve)? bossBefore;
    internal static bool HasBossControlOverride => bossBefore != null;
    internal static readonly ActionTasks Actions = new(Execute, ObserveAction,
        owns => NativeAgent.OnMain(() => { if (owns()) StopInputs(); return true; }, default),
        (handle, owns) => NativeAgent.OnMain(() => { if (owns()) Control.EndAction(handle); return true; }, default),
        (tool, owns) => NativeAgent.OnMain(() =>
        {
            if (owns())
            {
                if (tool == "fight") FightCoordinator.Stop();
                else StopInputs(preserveAttack: Actions.HasAttack);
            }
            return true;
        }, default),
        (previous, next, token) => NativeAgent.OnMain(() =>
        {
            if (!Plan.RequiresNodeExecution) return true;
            if (string.IsNullOrEmpty(next)) throw new InvalidOperationException("待执行动作需要所属DAG节点。");
            if (previous != next && !string.IsNullOrEmpty(previous))
            {
                var predecessor = Plan.Snapshot()["steps"].AsArray().First(s => s["id"]?.ToString() == previous);
                if (predecessor["status"]?.ToString() != "completed"
                    && Plan.Advance(previous, AgentTaskEvidence.Measure)["verified"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("前置动作结束，但DAG节点的实际完成条件尚未满足。");
            }
            Plan.Script(next);
            Plan.Select(next);
            return true;
        }, token));
    static readonly Dictionary<string, int> Tracking = new();
    static readonly HashSet<string> Discoveries = new();
    internal static int DiscoveryRevision => Discoveries.Count;
    static readonly HttpClient Wiki = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient HarvestHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    public static void NewTask() { Tracking.Clear(); Discoveries.Clear(); continuedActions.Clear(); pendingNode = null; LastContinuation = ExecutionContinuity = LastRouteLoot = null; routeChests.Clear(); routeLootEnabled = false; gapStarted = null; gapReported = false; Watches.Reset(); Watches.EnsureDefaults(); Watches.IgnorePriorActions(Actions.Summary()); Recovery.Reset(); Actions.BeginTask(); Control.BeginTask(); }
    public static void Stop()
    {
        Actions.CancelAll();
        Watches.Reset();
        if (bossBefore is { } previous)
        {
            Combat.Enabled = previous.Attack; Dodge.Enabled = previous.Dodge; Dodge.MovementReserveScale = previous.Reserve; bossBefore = null;
        }
        StopInputs();
    }

    internal static void StopInputs(bool preserveAttack = false)
    {
        Halt.All();
        StateSnapshotPlayer.StopRemoteTask();
        if (!preserveAttack) FightCoordinator.Stop();
        ReplaySystem.Stop(); KeyPress.Clear();
        Combat.Stop(); Dodge.Stop();
        PlayerCombat.Stop();
        PvpDuel.Stop();
        StateSpacePlanner.PreserveTerrain = false;
        HttpServerSystem.PendingControl = null;
        if (ContactEscapeActive && LastContactEscape != null) LastContactEscape["status"] = "cancelled";
        ContactEscapeActive = false;
        var player = Main.LocalPlayer;
        player.controlLeft = player.controlRight = player.controlUp = player.controlDown = false;
        player.controlJump = player.controlHook = player.controlMount = false;
        player.controlUseItem = player.controlUseTile = player.controlThrow = false;
        player.releaseUseItem = true;
    }

    static int Int(JsonObject args, string key, int fallback = 0) => args[key]?.GetValue<int>() ?? fallback;
    static string Text(JsonObject args, string key) => args[key]?.GetValue<string>() ?? "";
    static bool Bool(JsonObject args, string key) => args[key]?.GetValue<bool>() ?? false;
    internal static bool Rejected(JsonObject result) => result != null && (result["error"] != null ||
        result["ok"]?.ToString() == "false" || result["accepted"]?.ToString() == "false");

    internal static Task<JsonObject> Call(string path, JsonObject args, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        token.ThrowIfCancellationRequested();
        var response = HttpServerSystem.Dispatch(path, args?.ToJsonString(new System.Text.Json.JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}");
        var result = JsonNode.Parse(response.Body) as JsonObject
            ?? throw new InvalidOperationException($"{path} 未返回 JSON 对象。");
        if (response.Status >= 400 && result["error"] == null) result["error"] = $"status {response.Status}";
        return result;
    }, token);

    public static Task<string> State(CancellationToken token, bool includeTiles = false, bool includeWatchHistory = false) => NativeAgent.OnMain(() =>
    {
        Plan.Observe(AgentTaskEvidence.Measure);
        var state = JsonNode.Parse(StateSerializer.ToJson(HttpServerSystem.LatestSnapshot)) as JsonObject ?? new();
        if (!includeTiles && state["tiles"] is JsonObject tiles)
        {
            tiles.Remove("rows");
            tiles["deferred"] = true;
            tiles["detail_request"] = new JsonObject { ["tool"] = "get_state", ["args"] = new JsonObject { ["include_tiles"] = true } };
        }
        state["terrain"] = TerrainInspection.Read(6);
        var healthPlayer = Main.LocalPlayer;
        state["movement_capabilities"] = new JsonObject
        {
            ["no_fall_damage"] = healthPlayer.noFallDmg, ["fall_start_tile_y"] = healthPlayer.fallStart,
            ["extra_fall_tiles"] = healthPlayer.extraFall, ["extra_jump_usable"] = healthPlayer.AnyExtraJumpUsable(),
            ["wing_time_frames"] = healthPlayer.wingTime
        };
        state["health_dynamics"] = new JsonObject
        {
            ["tick"] = Main.GameUpdateCount, ["hp"] = healthPlayer.statLife, ["dead"] = healthPlayer.dead,
            ["life_regen_raw"] = healthPlayer.lifeRegen, ["life_regen_count_raw"] = healthPlayer.lifeRegenCount,
            ["regen_direction"] = healthPlayer.lifeRegen < 0 ? "damage" : healthPlayer.lifeRegen > 0 ? "healing" : "neutral"
        };
        if (state["player"] is JsonObject observedPlayer)
        {
            observedPlayer["tick"] = Main.GameUpdateCount;
            observedPlayer["hp"] = healthPlayer.statLife;
            observedPlayer["max_hp"] = healthPlayer.statLifeMax2;
            observedPlayer["pos"] = new JsonObject { ["x"] = healthPlayer.position.X, ["y"] = healthPlayer.position.Y };
            observedPlayer["vel"] = new JsonObject { ["x"] = healthPlayer.velocity.X, ["y"] = healthPlayer.velocity.Y };
            observedPlayer["dead"] = Main.LocalPlayer.dead;
            observedPlayer["suffocating"] = Main.LocalPlayer.suffocating;
            observedPlayer["potion_delay_ticks"] = Main.LocalPlayer.potionDelay;
        }
        state["depth"] = new JsonObject
        {
            ["tick"] = Main.GameUpdateCount, ["player_center_y_tiles"] = healthPlayer.Center.Y / 16,
            ["world_surface_y_tiles"] = Main.worldSurface, ["rock_layer_y_tiles"] = Main.rockLayer,
            ["sky"] = healthPlayer.ZoneSkyHeight, ["surface"] = healthPlayer.ZoneOverworldHeight,
            ["underground"] = healthPlayer.ZoneDirtLayerHeight, ["cavern"] = healthPlayer.ZoneRockLayerHeight,
            ["underworld"] = healthPlayer.ZoneUnderworldHeight
        };
        state["agent"] = new JsonObject
        {
            ["status"] = NativeAgent.Status, ["auto_dodge"] = AgentPermissions.Dodge,
            ["auto_attack"] = AgentPermissions.Attack, ["player_id"] = Main.LocalPlayer.whoAmI,
            ["pvp"] = Main.LocalPlayer.hostile, ["team"] = Main.LocalPlayer.team,
            ["instruction_player_id"] = AgentNetwork.OwnerPlayer,
            ["api_revision"] = ApiRevision,
            ["combat_weapon_type"] = CombatLoadout.PreferredType,
            ["navigation_arrivals"] = Actions.NavigationArrivals,
            ["contact_escape_active"] = ContactEscapeActive,
            ["pvp_target"] = PvpDuel.Active ? PvpDuel.Target : PlayerCombat.Target
        };
        var players = new JsonArray();
        for (int id = 0; id < Main.maxPlayers; id++)
        {
            var other = Main.player[id];
            if (other == null || !other.active || id == Main.LocalPlayer.whoAmI) continue;
            players.Add(new JsonObject
            {
                ["id"] = id, ["name"] = other.name, ["hp"] = other.statLife, ["dead"] = other.dead,
                ["pvp"] = other.hostile, ["team"] = other.team,
                ["pos"] = new JsonObject { ["x"] = other.position.X, ["y"] = other.position.Y },
                ["pvp_block_reason"] = PlayerCombat.TargetReason(Main.LocalPlayer, id)
            });
        }
        state["other_players"] = players;
        state["combat"] = PvpDuel.Report();
        state["inventory_slots"] = PvpInventory.Snapshot();
        state["healing_items"] = new JsonArray(Main.LocalPlayer.inventory.Take(50)
            .Select((item, slot) => (item, slot)).Where(entry => !entry.item.IsAir && entry.item.healLife > 0)
            .Select(entry => (JsonNode)new JsonObject { ["slot"] = entry.slot, ["type"] = entry.item.type,
                ["stack"] = entry.item.stack, ["base_heal_life"] = entry.item.healLife,
                ["consumable"] = entry.item.consumable }).ToArray());
        state["task_plan"] = Plan.Summary();
        state["permanent_max_health"] = Main.LocalPlayer.statLifeMax;
        state["last_damage"] = LastDamage?.DeepClone();
        state["last_death"] = LastDeath?.DeepClone();
        state["last_automatic_heal"] = LastAutomaticHeal?.DeepClone();
        state["contact_escape"] = LastContactEscape?.DeepClone();
        var workflows = new JsonObject();
        foreach (var workflow in Workflows) workflows[workflow.Key] = workflow.Value.Summary();
        state["script_workflows"] = workflows;
        state["action_tasks"] = Actions.Summary();
        state["dag_continuation"] = LastContinuation?.DeepClone();
        state["execution_continuity"] = ExecutionContinuity?.DeepClone();
        state["last_route_loot"] = LastRouteLoot?.DeepClone();
        state["control_overrides"] = Control.Snapshot();
        state["control_execution"] = ControlSession.Snapshot();
        state["idle_defense_active"] = FightCoordinator.IsIdleDefending;
        state["watch"] = includeWatchHistory ? Watches.Snapshot() : Watches.Summary();
        state["recovery"] = Recovery.Snapshot();
        state["codex_goal"] = NativeAgent.GoalState;
        state["boss_control"] = BossStatus();
        state["equipment_slots"] = Equipment.Snapshot();
        return state.ToJsonString(ModelJson);
    }, token);

    static JsonObject BossStatus() => new()
    {
        ["attack_enabled"] = Combat.Enabled, ["movement_enabled"] = Dodge.Enabled,
        ["attack_permitted"] = AgentPermissions.Attack, ["movement_permitted"] = AgentPermissions.Dodge,
        ["manual_control"] = Manual.On, ["pvp_active"] = PvpDuel.Active,
        ["input_gate"] = Manual.On ? "manual" : Main.gameMenu ? "game_menu" : Main.playerInventory ? "inventory_open" : null,
        ["movement_reserve_scale"] = Dodge.MovementReserveScale,
        ["movement_snapshot"] = Dodge.Snapshot is { } movement ? new JsonObject
        {
            ["captured_tick"] = movement.Tick, ["reserve_scale"] = movement.MovementReserveScale,
            ["solid_room_tiles"] = new JsonObject { ["left"] = movement.SolidDistances.Left / 16,
                ["right"] = movement.SolidDistances.Right / 16, ["up"] = movement.SolidDistances.Up / 16, ["down"] = movement.SolidDistances.Down / 16 },
            ["options"] = new JsonArray(movement.AvailableActions.Select(option => (JsonNode)new JsonObject
                { ["direction"] = option.Intent.Direction.ToString(), ["horizontal"] = option.Intent.HorizontalSize.ToString(),
                    ["vertical"] = option.Intent.VerticalSize.ToString() }).ToArray())
        } : null,
        ["attack_state"] = Combat.Last, ["movement_state"] = Dodge.Last,
        ["requires_typesafe_key"] = true, ["config_key_present"] = !string.IsNullOrWhiteSpace(Config.I.TypeSafeKey),
        ["other_key_sources"] = "not_checked",
        ["active_bosses"] = new JsonArray(Main.npc.Where(n => n != null && n.active && n.boss && n.life > 0)
            .Select(n => (JsonNode)new JsonObject { ["type"] = n.type, ["hp"] = n.life,
                ["x"] = n.Center.X, ["y"] = n.Center.Y }).ToArray())
    };

    static Task<JsonObject> ObserveAction(CancellationToken token) => NativeAgent.OnMain(() =>
    {
        var player = Main.LocalPlayer;
        return new JsonObject { ["hp"] = player.statLife, ["max_hp"] = player.statLifeMax2, ["dead"] = player.dead,
            ["pos"] = new JsonObject { ["x"] = player.position.X, ["y"] = player.position.Y },
            ["inventory_slots"] = new JsonArray(PvpInventory.Snapshot().Where(item => item["type"].GetValue<int>() > 0 && item["stack"].GetValue<int>() > 0)
                .Select(item => item.DeepClone()).ToArray()), ["inventory_empty_slots_omitted"] = true,
            ["permanent_max_health"] = player.statLifeMax };
    }, token);

    static Dictionary<string, int> Inventory()
    {
        var items = new Dictionary<string, int>();
        foreach (var item in Main.LocalPlayer.inventory)
            if (item != null && !item.IsAir) items[item.Name] = items.GetValueOrDefault(item.Name) + item.stack;
        return items;
    }

    static Task<string> Result(JsonObject result, Dictionary<string, int> before, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        Plan.Observe(AgentTaskEvidence.Measure);
        var player = Main.LocalPlayer;
        var now = Inventory();
        var got = new JsonObject(); var lost = new JsonObject(); var progress = new JsonObject();
        foreach (var item in now)
            if (item.Value > before.GetValueOrDefault(item.Key)) got[item.Key] = item.Value - before.GetValueOrDefault(item.Key);
        foreach (var item in before)
            if (item.Value > now.GetValueOrDefault(item.Key)) lost[item.Key] = item.Value - now.GetValueOrDefault(item.Key);
        foreach (var item in Tracking) progress[item.Key] = $"{now.GetValueOrDefault(item.Key)}/{item.Value}";
        result["state"] = new JsonObject
        {
            ["pos"] = new JsonObject { ["x"] = ActExecutor.OriginCx(player), ["y"] = ActExecutor.OriginCy(player) },
            ["hp"] = player.statLife, ["on_ground"] = player.velocity.Y == 0f
        };
        result["got"] = got; result["lost"] = lost;
        result["task_plan"] = Plan.Summary();
        if (progress.Count > 0) result["进度"] = progress;
        return result.ToJsonString();
    }, token);

    internal static async Task<JsonObject> Wait(string path, string active, string stop, int seconds, CancellationToken token,
        Func<double, Task<JsonObject>> navigationProgress = null)
    {
        var stopArgs = stop == "/fight" ? new JsonObject { ["active"] = false } : null;
        async Task StopAction()
        {
            var result = await Call(stop, stopArgs, token);
            if (Rejected(result)) throw new InvalidOperationException($"{stop} 停止动作被拒绝：{result}");
        }
        try
        {
            var timer = Stopwatch.StartNew();
            while (seconds <= 0 || timer.Elapsed.TotalSeconds < seconds)
            {
                await NativeAgent.NextUpdate(token);
                if (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
                {
                    await StopAction();
                    return new JsonObject { ["outcome"] = "interrupted_by_death" };
                }
                var status = await Call(path, null, token);
                if (Rejected(status)) throw new InvalidOperationException($"{path} 状态查询被拒绝：{status}");
                if (active == "nav" ? Bool(status, "done") || Text(status, "status") == "failed" : !Bool(status, active))
                    return status;
                if (navigationProgress != null)
                {
                    var stalled = await navigationProgress(timer.Elapsed.TotalSeconds);
                    if (stalled != null)
                    {
                        await StopAction();
                        await NativeAgent.OnMain(() => { DiagLog.Write("[agent_navigation_interrupted] " + stalled.ToJsonString()); return true; }, token);
                        return stalled;
                    }
                }
            }
            await StopAction();
            var final = await Call(path, null, token);
            final["outcome"] = "timeout";
            return final;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            try { await StopAction(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception cleanup) { throw new AggregateException("读取执行状态失败，停止动作也失败。", failure, cleanup); }
            throw;
        }
    }

    public static async Task<string> Execute(string name, JsonObject args, CancellationToken token)
    {
        string taskNode = ActionTasks.NodeContext.Value;
        if (Recovery.Revision > 0 && await NativeAgent.OnMain(() => Recovery.Denied(name, args), token))
            return new JsonObject { ["error"] = "repeated_failure_blocked", ["detail"] = "相同参数已失败3次，必须修正方案。",
                ["recovery"] = await NativeAgent.OnMain(Recovery.Snapshot, token) }.ToJsonString();
        if (name is not ("say" or "ask" or "get_state" or "game_api" or "combat_observe" or "combat_stop")
            && await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
        {
            while (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
            return new JsonObject { ["outcome"] = "interrupted_by_death",
                ["detail"] = "Respawned; re-evaluate this action against fresh state instead of replaying its old arguments.",
                ["state_after_respawn"] = JsonNode.Parse(await State(token)) }.ToJsonString();
        }
        bool external = name is "nav_to" or "mine" or "mine_vein" or "chop_wood" or "use_item"
            or "place_at" or "act" or "interact" or "loot_all" or "craft" or "inventory_swap" or "combat_reposition"
            or "gather" or "collect_treasure" or "equip_item" or "inventory_drop" or "inventory_trash" or "npc_chat";
        bool action = external || name is "fight" or "fight_player";
        bool startsPrimary = name == "action_start" && Text(args, "tool") != "fight";
        if ((external || startsPrimary) && string.IsNullOrEmpty(taskNode)
            && await NativeAgent.OnMain(() => Plan.RequiresNodeExecution, token))
            return new JsonObject { ["error"] = "dag_node_required",
                ["detail"] = "已有DAG。请把本次任务动作写入所属节点script，用run_script({task_node:id})执行；需要修改脚本用task_plan_patch nodes。图外只读查询、暂停动作和独立fight防身仍可用。",
                ["task_plan"] = await NativeAgent.OnMain(Plan.Summary, token) }.ToJsonString();
        if (action && ContactEscapeActive)
        {
            while (ContactEscapeActive) await Task.Delay(33, token);
            return new JsonObject { ["error"] = "survival_escape_reobserve", ["outcome"] = "unsafe",
                ["detail"] = "短程脱离接触伤害已结束，原动作尚未执行；依据最新位置和危险状态重新决策。",
                ["state"] = JsonNode.Parse(await State(token)) }.ToJsonString();
        }
        string requestedTool = name == "action_start" ? Text(args, "tool")
            : name == "action_control" && Text(args, "command") == "resume"
                ? Actions.Summary().FirstOrDefault(a => a["handle"]?.ToString() == Text(args, "handle"))?["tool"]?.ToString() : name;
        if ((action || name is "action_start" or "combat_start" or "combat_reposition"
            || name == "action_control" && Text(args, "command") == "resume"
            || name == "set_combat" && Bool(args, "boss_control")) && Actions.ConflictsWith(requestedTool))
            return new JsonObject { ["error"] = "action_busy", ["detail"] = "已有任务持有角色输入，请先action_control暂停或取消。",
                ["active_handle"] = Actions.ActiveHandle,
                ["action_tasks"] = Actions.Summary() }.ToJsonString();
        long lease = external ? await NativeAgent.OnMain(() => PvpDuel.BeginAction(name), token) : 0;
        long controlTicket = 0;
        try
        {
            if (action)
            {
                var denied = await NativeAgent.OnMain(() =>
                {
                    FightCoordinator.StopIdleDefense();
                    return ControlSession.Begin(name, args["control_claim"]?.ToString(), out controlTicket,
                        AgentControlSession.RequiredChannels(name, args));
                }, token);
                if (denied != null) return denied.ToJsonString();
            }
            string output = await ExecuteCore(name, args, token);
            if (name is "task_plan" or "task_step" or "task_plan_patch")
                output = await NativeAgent.OnMain(() =>
                {
                    var result = JsonNode.Parse(output).AsObject();
                    var summary = Plan.Summary();
                    if (name == "task_step")
                        foreach (var step in summary["steps"].AsArray().OfType<JsonObject>()) step.Remove("script");
                    foreach (string field in new[] { "verified", "corrected" })
                        if (result[field] != null) summary[field] = result[field].DeepClone();
                    return summary.ToJsonString();
                }, token);
            if (name is "run_script" or "game_api" && !Actions.RunningScript)
                output = await NativeAgent.OnMain(() =>
                {
                    var notices = Watches.Advisories();
                    if (notices.Count == 0 || JsonNode.Parse(output) is not JsonObject response) return output;
                    response["along_route_opportunities"] = notices;
                    response["opportunity_delivery"] = "沿途新发现，当前动作未暂停。本轮评估并落实值得处理的机会；通知不代表已处理，完整事件仍可用script_events读取。";
                    return response.ToJsonString();
                }, token);
            if (name is not ("get_state" or "say" or "ask" or "task_plan" or "task_step" or "action_query" or "run_script" or "watch"))
                output = await NativeAgent.OnMain(() =>
                {
                    Plan.Record(name, output, action, taskNode);
                    Recovery.Record(name, args, output);
                    try
                    {
                        var facts = JsonNode.Parse(output);
                        if (name is "game_api" or "game_catalog" or "item_info" or "recipe" or "wiki_search" or "wiki_page" or "find_biome"
                            or "find_tiles" or "tile_names" or "find_descent" or "descent_route" or "equipment_list" or "inventory_review" or "inspect_terrain")
                            if (!Rejected(facts as JsonObject)) Discoveries.Add(name + ":" + args.ToJsonString());
                        if (facts is JsonObject result && result.ContainsKey("task_plan"))
                        {
                            result["task_plan"] = Plan.Summary();
                            return result.ToJsonString();
                        }
                    }
                    catch (System.Text.Json.JsonException) { }
                    return output;
                }, token);
            if (name is not ("get_state" or "say") && JsonNode.Parse(output) is JsonObject response
                && response["error"] == null && response["ok"]?.ToString() != "false"
                && response["task_plan"] is JsonObject plan)
            {
                var brief = new JsonObject { ["detail_tool"] = "get_state" };
                foreach (string field in new[] { "status", "cursor", "revision", "progress_revision", "current_step", "ready_steps", "mode" })
                    brief[field] = plan[field]?.DeepClone();
                response["task_plan"] = brief;
                output = response.ToJsonString();
            }
            if (lease != 0)
                return await NativeAgent.OnMain(() =>
                {
                    PvpDuel.EndAction(lease);
                    var result = JsonNode.Parse(output) as JsonObject ?? new JsonObject { ["action_result"] = output };
                    result["combat_after_action"] = PvpDuel.Report();
                    return result.ToJsonString();
                }, token);
            return output;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await NativeAgent.OnMain(() =>
            {
                var result = new JsonObject { ["error"] = "tool_exception", ["tool"] = name, ["detail"] = ex.Message };
                Plan.Record(name, result.ToJsonString(), action, taskNode);
                Recovery.Record(name, args, result.ToJsonString());
                result["task_plan"] = Plan.Summary();
                DiagLog.Write($"[agent] tool_exception {name}: {ex.Message}");
                NativeAgent.Progress("执行失败：" + name, "等待模型决策", false);
                return result.ToJsonString();
            }, token);
        }
        finally
        {
            if (controlTicket != 0)
                await NativeAgent.OnMain(() => { ControlSession.End(controlTicket); return true; }, default);
            if (lease != 0 && !token.IsCancellationRequested)
                await NativeAgent.OnMain(() => { PvpDuel.EndAction(lease); return true; }, token);
        }
    }

    static async Task<string> ExecuteScriptTool(string name, JsonObject args, CancellationToken root, CancellationToken token, bool scriptEvents = false)
    {
        if (!ActionTasks.Supports(name))
            return await Execute(name, args, name is "action_start" or "action_queue" || name == "action_control" && Text(args, "command") == "resume" ? root : token);
        if (name != "fight" && string.IsNullOrEmpty(ActionTasks.NodeContext.Value)
            && await NativeAgent.OnMain(() => Plan.RequiresNodeExecution, token))
            return await Execute(name, args, token);
        if (name == "fight" && !AgentPermissions.Attack) return await Execute(name, args, token);
        if (Actions.RunningScript) return await Execute(name, args, token);
        await NativeAgent.OnMain(() => { Watches.EnsureDefaults(); return true; }, token);
        string handle = Actions.Start(name, args, root, inline: true)["handle"].ToString();
        while (true)
        {
            var current = await Actions.Query(handle, 250, token);
            bool pending = await NativeAgent.OnMain(() => Watches.HasInterrupting, token);
            if (current["status"]?.ToString() == "running" && pending)
            {
                current = await Actions.Control(handle, "pause", null, root);
                if (scriptEvents)
                    return new JsonObject { ["outcome"] = "interrupted", ["handle"] = handle, ["action"] = current,
                        ["events"] = await NativeAgent.OnMain(Watches.Take, token) }.ToJsonString();
                return new JsonObject { ["host_yield"] = true, ["handle"] = handle, ["action"] = current }.ToJsonString();
            }
            if (current["status"]?.ToString() == "running") continue;
            var result = current["result"] as JsonObject ?? new JsonObject { ["outcome"] = current["status"]?.DeepClone() };
            result["managed_handle"] = handle;
            if (current["status"]?.ToString() == "failed")
            {
                await NativeAgent.OnMain(() =>
                {
                    Watches.Observe(new JsonObject { ["hp"] = Main.LocalPlayer.statLife, ["max_hp"] = Main.LocalPlayer.statLifeMax2,
                        ["dead"] = Main.LocalPlayer.dead,
                        ["actions"] = new JsonArray(current.DeepClone()) });
                    return true;
                }, token);
                if (!scriptEvents) result["host_yield"] = true;
                else result["events"] = await NativeAgent.OnMain(Watches.Take, token);
            }
            return result.ToJsonString();
        }
    }

    static async Task<string> ExecuteCore(string name, JsonObject args, CancellationToken token)
    {
        var before = await NativeAgent.OnMain(() =>
        {
            DiagLog.Write($"[agent] tool {name} {args}");
            if (name is not ("say" or "ask" or "get_state"))
                NativeAgent.Progress("开始执行：" + name, "执行 " + name,
                    !PvpDuel.Active && !name.StartsWith("combat_"));
            return name is "get_state" or "say" or "ask" or "wiki_search" or "wiki_page" ? null : Inventory();
        }, token);
        if (name == "get_state") return await State(token, Bool(args, "include_tiles"), Bool(args, "include_watch_history"));
        if (name == "say")
        {
            await NativeAgent.OnMain(() => { AgentChat.Say(AgentConversation.VisibleText(Text(args, "text"))); return true; }, token);
            return "ok";
        }
        if (name == "ask") return new JsonObject { ["player_answer"] = await NativeAgent.Ask(AgentConversation.VisibleText(Text(args, "question")), token) }.ToJsonString();
        if (name is "wiki_search" or "wiki_page") return await ToolFinished(name, await ReadWiki(name, args, token), token);
        JsonObject result;
        switch (name)
        {
            case "game_api":
                return NativeAgent.DescribeApi(args).ToJsonString(ModelJson);
            case "nearby_drops":
                return new JsonObject { ["drops"] = await NativeAgent.OnMain(() => NearbyDrops.Read(Int(args, "radius", 25)), token) }.ToJsonString();
            case "jev_judge":
                string decisionKey = await NativeAgent.OnMain(() => Config.I.TypeSafeKey?.Trim(), token);
                return (await ScriptDecision.Judge(HarvestHttp, decisionKey, args, token)).ToJsonString();
            case "script_program":
                var library = new ScriptLibrary(System.IO.Path.Combine(Main.SavePath, "TerraBlindAgent", "programs"));
                return (Text(args, "operation") switch
                {
                    "list" => (JsonNode)library.List(),
                    "example" => ScriptLibrary.Example(Text(args, "name")),
                    "get" => library.Get(Text(args, "name")),
                    "save" => library.Save(Text(args, "name"), Text(args, "code"), Int(args, "expected_revision", -1)),
                    "patch" => library.Patch(Text(args, "name"), Text(args, "old_text"), args["new_text"]?.GetValue<string>(), Int(args, "expected_revision", -1)),
                    _ => throw new ArgumentException("operation需要list/get/save/patch/example。")
                }).ToJsonString();
            case "control":
                return (await ManageControl(args, token)).ToJsonString();
            case "inspect_terrain":
                return (await NativeAgent.OnMain(() => TerrainInspection.Read(Int(args, "radius", 6)), token)).ToJsonString();
            case "terrain_map":
                return (await NativeAgent.OnMain(() => TerrainMap.Read(args), token)).ToJsonString(ModelJson);
            case "opportunity_assess":
                return (await NativeAgent.OnMain(() => TerrainInspection.Opportunity(Int(args, "x", -1), Int(args, "y", -1),
                    args["destination"] as JsonObject), token)).ToJsonString();
            case "run_script":
                if (Bool(args, "background"))
                {
                    string node = Text(args, "task_node");
                    if (node == "" || args.Any(p => p.Key is not ("task_node" or "background" or "event_delivery")))
                        throw new ArgumentException("后台脚本需要task_node，仅可另传event_delivery。");
                    if (Actions.RunningScript) throw new InvalidOperationException("后台脚本不能嵌套启动后台脚本。");
                    return (await NativeAgent.OnMain(() =>
                    {
                        Plan.Script(node);
                        if (Actions.HasPrimaryControl) throw new InvalidOperationException("已有主动作，先排队后继或暂停原句柄。");
                        Plan.Select(node);
                        Watches.EnsureDefaults();
                        string previous = ActionTasks.NodeContext.Value;
                        ActionTasks.NodeContext.Value = node;
                        try
                        {
                            var scriptArgs = new JsonObject { ["task_node"] = node };
                            if (args["event_delivery"] != null) scriptArgs["event_delivery"] = args["event_delivery"].DeepClone();
                            return Actions.Start("run_script", scriptArgs, token);
                        }
                        finally { ActionTasks.NodeContext.Value = previous; }
                    }, token)).ToJsonString();
                }
                if (Text(args, "task_node") != "")
                {
                    if (args["code"] != null || args["program"] != null || args["tool"] != null || args["workflow"] != null)
                        throw new ArgumentException("task_node使用节点保存的脚本和workflow，不能另传code/program/tool/workflow。");
                    string nodeId = Text(args, "task_node");
                    var nodeScript = await NativeAgent.OnMain(() =>
                    {
                        var script = Plan.Script(nodeId);
                        if (Plan.Snapshot()["current_step"]?.ToString() != nodeId
                            && Actions.Summary().Any(a => a["tool"]?.ToString() != "fight" && a["status"]?.ToString() == "running"))
                            throw new InvalidOperationException("切换节点前须暂停原动作并保留句柄；独立防身攻击不阻止切换。");
                        Plan.Select(nodeId);
                        return script;
                    }, token);
                    args = (JsonObject)args.DeepClone();
                    args["code"] = nodeScript["code"].DeepClone();
                    args["workflow"] = nodeScript["workflow"].DeepClone();
                }
                string eventDelivery = Text(args, "event_delivery");
                if (eventDelivery is not ("" or "agent" or "script")) throw new ArgumentException("event_delivery需要agent/script。");
                bool localEvents = eventDelivery == "script";
                bool queuedScript = Actions.RunningScript;
                string source = Text(args, "code");
                if (Text(args, "tool") != "")
                {
                    if (args["code"] != null || args["program"] != null)
                        throw new ArgumentException("tool与code/program不能同时提供。");
                    if (args["args"] != null && args["args"] is not JsonObject)
                        throw new ArgumentException("args必须为JSON对象。");
                    source = "return tool(" + JsonValue.Create(Text(args, "tool")).ToJsonString()
                        + "," + (args["args"] ?? new JsonObject()).ToJsonString() + ");";
                }
                JsonObject program = null;
                if (Text(args, "program") != "")
                {
                    if (source != "") throw new ArgumentException("code与program不能同时提供。");
                    program = new ScriptLibrary(System.IO.Path.Combine(Main.SavePath, "TerraBlindAgent", "programs")).Get(Text(args, "program"));
                    if (program["found"]?.GetValue<bool>() != true) throw new ArgumentException("未知program。");
                    if (program["revision"].GetValue<int>() != Int(args, "expected_revision", -1))
                        return new JsonObject { ["error"] = "program_revision_conflict", ["revision"] = program["revision"].DeepClone() }.ToJsonString();
                    source = program["code"].GetValue<string>();
                }
                var workflow = await NativeAgent.OnMain(() =>
                {
                    string id = Text(args, "workflow");
                    if (id == "") return null;
                    if (id.Length > 80) throw new ArgumentException("workflow需要1..80字符。");
                    if (!Workflows.TryGetValue(id, out var saved))
                    {
                        if (Workflows.Count >= 32) throw new InvalidOperationException("本世界最多32个workflow。");
                        Workflows[id] = saved = new ScriptProgress();
                    }
                    return saved;
                }, token);
                Guid eventReader = await NativeAgent.OnMain(() => localEvents ? Watches.BeginScriptDelivery() : Guid.Empty, token);
                try
                {
                    result = await AgentScript.Run(source, Int(args, "seconds", 120), NativeAgent.ValidateTool,
                        async (child, childArgs, childToken) =>
                        {
                            string previousNode = ActionTasks.NodeContext.Value;
                            ActionTasks.NodeContext.Value = Text(args, "task_node") is string owner && owner.Length > 0 ? owner : previousNode;
                            try { return await ExecuteScriptTool(child, childArgs, token, childToken, localEvents); }
                            finally { ActionTasks.NodeContext.Value = previousNode; }
                        },
                        async cleanupToken => await NativeAgent.OnMain(() =>
                        {
                            if (queuedScript) StopInputs(preserveAttack: Actions.HasAttack);
                            else Stop();
                            return true;
                        }, cleanupToken), token, workflow,
                        localEvents ? null : checkToken => NativeAgent.OnMain(() => Watches.HasInterrupting ? Watches.Pending() : null, checkToken));
                }
                finally
                {
                    await NativeAgent.OnMain(() => { if (eventReader != Guid.Empty) Watches.EndScriptDelivery(eventReader); return true; }, default);
                }
                if (program != null)
                    result["program"] = new JsonObject { ["name"] = program["name"].DeepClone(), ["revision"] = program["revision"].DeepClone() };
                if (result["value"] is JsonObject returnedValue && returnedValue["wake"] is JsonObject deliveredWake)
                {
                    string activeHandle = Actions.ActiveHandle;
                    if (activeHandle != null && deliveredWake["continue_action"]?.ToString() != activeHandle)
                        await Actions.Control(activeHandle, "pause", null, token);
                    var deliveredEvents = await NativeAgent.OnMain(() => Watches.Take(8), token);
                    if (deliveredEvents.Count > 0) deliveredWake["events"] = deliveredEvents;
                }
                break;
            case "watch":
                return (await NativeAgent.OnMain(() =>
                {
                    Watches.Configure(args);
                    if (Actions.HasActive) Watches.EnsureDefaults();
                    return Watches.Snapshot();
                }, token)).ToJsonString();
            case "opportunity_reconsider":
                return (await NativeAgent.OnMain(() => Watches.Reconsider(Text(args, "kind"), Text(args, "key"), Text(args, "reason")), token)).ToJsonString();
            case "script_events":
                int eventWait = Int(args, "wait_ms", 1000);
                int eventLimit = Int(args, "max_events", 4);
                if (eventLimit is < 1 or > 16) throw new ArgumentException("max_events需要1..16。");
                if (eventWait < 0 || eventWait > 1000) throw new ArgumentException("wait_ms需要0..1000。");
                var eventClock = Stopwatch.StartNew();
                while (!await NativeAgent.OnMain(() => Watches.HasPending, token) && eventClock.ElapsedMilliseconds < eventWait)
                    await Task.Delay((int)Math.Clamp(eventWait - eventClock.ElapsedMilliseconds, 1, 25), token);
                if (!await NativeAgent.OnMain(() => Watches.HasPending, token))
                    return new JsonObject { ["events"] = new JsonArray(), ["remaining_events"] = 0, ["paused_action"] = null, ["action"] = null }.ToJsonString();
                string eventHandle = await NativeAgent.OnMain(() => Watches.HasInterrupting ? Actions.ActiveHandle : null, token);
                var pausedAction = eventHandle != null ? await Actions.Control(eventHandle, "pause", null, token) : null;
                return (await NativeAgent.OnMain(() => new JsonObject { ["events"] = Watches.Take(eventLimit),
                    ["remaining_events"] = Watches.PendingCount,
                    ["paused_action"] = eventHandle, ["action"] = pausedAction }, token)).ToJsonString();
            case "decision_point":
                string continueAction = Text(args, "continue_action");
                if (continueAction.Length > 0 && Actions.ActiveHandle != continueAction)
                    return new JsonObject { ["error"] = "continue_action_not_active", ["handle"] = continueAction }.ToJsonString();
                return new JsonObject { ["status"] = "yield", ["continue_action"] = continueAction.Length > 0 ? continueAction : null,
                    ["event"] = await NativeAgent.OnMain(() => Watches.Request(Text(args, "reason"), args["evidence"] as JsonObject), token) }.ToJsonString();
            case "action_queue":
                string queuedTool = Text(args, "tool");
                var queuedArgs = args["args"] as JsonObject ?? throw new ArgumentException("需要动作args对象。");
                if (queuedTool == "run_script" && (Text(queuedArgs, "task_node") != Text(args, "task_node")
                    || Text(args, "task_node") == "" || queuedArgs.Any(p => p.Key is not ("task_node" or "seconds" or "event_delivery"))))
                    throw new ArgumentException("排队脚本须引用同一task_node保存的源码；args仅支持task_node、seconds、event_delivery。");
                NativeAgent.ValidateTool(queuedTool, queuedArgs);
                return (await NativeAgent.OnMain(() =>
                {
                    string node = Text(args, "task_node");
                    if (Plan.RequiresNodeExecution && !Plan.Snapshot()["steps"].AsArray().Any(s =>
                        s["id"]?.ToString() == node && s["status"]?.ToString() != "completed"))
                        throw new InvalidOperationException("排队需要现有的未完成DAG节点。");
                    Watches.EnsureDefaults();
                    return Actions.Enqueue(Text(args, "after_handle"), queuedTool, queuedArgs, node, token);
                }, token)).ToJsonString();
            case "action_start":
                if (Actions.RunningScript)
                    throw new InvalidOperationException("排队节点脚本已持有后台动作句柄；脚本内直接tool('nav_to',args)等顺序执行，不再嵌套action_start。");
                string actionTool = Text(args, "tool");
                var actionArgs = args["args"] as JsonObject ?? throw new ArgumentException("需要动作args对象。");
                NativeAgent.ValidateTool(actionTool, actionArgs);
                await NativeAgent.OnMain(() => { Watches.EnsureDefaults(); return true; }, token);
                result = Actions.Start(actionTool, actionArgs, token);
                return result.ToJsonString();
            case "action_query":
                result = await Actions.Query(Text(args, "handle"), Int(args, "wait_ms", 0), token);
                await NativeAgent.OnMain(() => { Watches.AcknowledgeCompletion(result); return true; }, token);
                return result.ToJsonString();
            case "action_control":
                if (Text(args, "command") == "resume")
                {
                    string ownerNode = Actions.Summary().FirstOrDefault(a => a["handle"]?.ToString() == Text(args, "handle"))?["task_node"]?.ToString();
                    if (!string.IsNullOrEmpty(ownerNode))
                    {
                        string callerNode = ActionTasks.NodeContext.Value;
                        if (!string.IsNullOrEmpty(callerNode) && callerNode != ownerNode)
                            throw new InvalidOperationException("不能在其他DAG节点的脚本内恢复此句柄；回到所属节点续接。");
                        await NativeAgent.OnMain(() =>
                        {
                            Plan.Script(ownerNode);
                            Plan.Select(ownerNode);
                            return true;
                        }, token);
                    }
                }
                if (args["args"] is JsonObject resumeArgs)
                {
                    string resumeTool = Actions.Snapshot().FirstOrDefault(a => a["handle"]?.ToString() == Text(args, "handle"))?["tool"]?.ToString()
                        ?? throw new ArgumentException("未知动作句柄。");
                    NativeAgent.ValidateTool(resumeTool, resumeArgs);
                }
                if (Text(args, "command") == "resume")
                    await NativeAgent.OnMain(() => { Watches.EnsureDefaults(); return true; }, token);
                result = await Actions.Control(Text(args, "handle"), Text(args, "command"), args["args"] as JsonObject, token);
                if (Text(args, "command") == "resume" && result["status"]?.ToString() == "running")
                    await NativeAgent.OnMain(() =>
                    { Watches.Resumed(Text(args, "handle"), Int(result, "attempt")); return true; }, token);
                return result.ToJsonString();
            case "script_progress":
                var savedProgress = await NativeAgent.OnMain(() => Workflows.TryGetValue(Text(args, "workflow"), out var saved)
                    ? saved : null, token);
                string resolution = Text(args, "action");
                if (savedProgress == null && resolution is "" or "get")
                    return new JsonObject { ["found"] = false, ["workflow"] = Text(args, "workflow"), ["steps"] = new JsonArray(),
                        ["detail"] = "没有已保存的workflow；首次执行请在run_script指定workflow，勿假定存在进度。" }.ToJsonString();
                if (savedProgress == null) throw new ArgumentException("未知workflow，不能核对不存在的步骤。");
                if (resolution is "retry" or "complete")
                {
                    var observed = JsonNode.Parse(await State(token)).AsObject();
                    var evidence = new JsonObject { ["inventory_slots"] = observed["inventory_slots"]?.DeepClone(),
                        ["permanent_max_health"] = observed["permanent_max_health"]?.DeepClone() };
                    var resolved = savedProgress.Resolve(Text(args, "step"), Int(args, "expected_revision", -1), resolution == "complete",
                        args["value"], evidence);
                    result = new JsonObject { ["workflow"] = Text(args, "workflow"), ["revision"] = resolved["revision"]?.DeepClone(),
                        ["step"] = Text(args, "step"), ["status"] = resolution == "complete" ? "completed" : "ready_to_retry",
                        ["observed"] = evidence };
                }
                else if (resolution is "" or "get") result = savedProgress.Snapshot();
                else throw new ArgumentException("action需要get/retry/complete。");
                break;
            case "task_plan_patch":
                if (args["nodes"] != null || args["add_nodes"] != null)
                {
                    if (args["changes"] != null) throw new ArgumentException("nodes和changes不能同时提供。");
                    result = await NativeAgent.OnMain(() =>
                    {
                        var edited = (args["nodes"] as JsonArray ?? new JsonArray()).Select(n => n["id"]?.ToString()).ToHashSet();
                        if (Actions.Summary().Any(a => a["tool"]?.ToString() != "fight"
                            && a["status"]?.ToString() is "running" or "queued" && edited.Contains(a["task_node"]?.ToString())))
                            throw new InvalidOperationException("修改执行中节点须暂停其动作；修改已排队节点须先取消待执行句柄再重新排队。其他节点可在导航途中修改。");
                        Plan.Revise(AgentCatalog.ResolvePlanTypes(args), AgentTaskEvidence.Measure);
                        return Plan.Summary();
                    }, token);
                    break;
                }
                result = await NativeAgent.OnMain(() => Plan.Patch(args, AgentTaskEvidence.Measure), token);
                break;
            case "gather_observe":
            case "gather":
                foreach (string field in new[] { "item_type", "count", "tile_names", "npc_types", "tile_frame_x", "tile_frame_y" })
                    if (args[field] != null)
                        throw new ArgumentException(field + "必须放在targets数组的对应目标对象内；放在顶层不会配置该目标来源，请按工具定义修正。");
                var targets = (args["targets"] as JsonArray ?? throw new ArgumentException("需要targets数组。"))
                    .Select(t => new GatherTarget(t["item_type"].GetValue<int>(), t["count"].GetValue<int>(),
                        t["tile_names"]?.AsArray().Select(n => n.GetValue<string>()).ToArray() ?? Array.Empty<string>(),
                        t["npc_types"]?.AsArray().Select(n => n.GetValue<int>()).ToArray() ?? Array.Empty<int>(),
                        t["tile_frame_x"]?.GetValue<int>(), t["tile_frame_y"]?.GetValue<int>())).ToArray();
                var sourceArea = GatherArea.Parse(args["source_area"] as JsonObject);
                if (sourceArea != null && ((long)sourceArea.X + sourceArea.Width > Main.maxTilesX
                    || (long)sourceArea.Y + sourceArea.Height > Main.maxTilesY)) throw new ArgumentException("source_area超出世界边界。");
                Gather.Validate(targets, sourceArea);
                var gatherWorld = new GameGather(targets, sourceArea);
                if (name == "gather_observe")
                {
                    result = await gatherWorld.Observe(token);
                    if (sourceArea != null)
                        result["candidates"] = new JsonArray(result["candidates"].AsArray().OfType<JsonObject>()
                            .Where(sourceArea.Contains).Select(c => c.DeepClone()).ToArray());
                    result["source_area"] = sourceArea?.Snapshot();
                }
                else result = await Gather.Run(targets, Int(args, "seconds", 120), gatherWorld, token, sourceArea);
                break;
            case "npc_find":
                result = await Call("/npc_find", args, token);
                break;
            case "collect_treasure":
                result = await Navigate(new JsonObject { ["x"] = Int(args, "x"), ["y"] = Int(args, "y"), ["reach"] = true }, token);
                if (result["done"]?.GetValue<bool>() != true) break;
                var chestBefore = await NativeAgent.OnMain(() => AgentTaskEvidence.ObserveChest(Int(args, "x"), Int(args, "y")), token);
                bool inventoryWasOpen = await NativeAgent.OnMain(() => Main.playerInventory, token);
                try
                {
                    result = await Call("/collect_treasure", args, token);
                    if (!Rejected(result)) result = await Wait("/collect_treasure_status", "running", "/collect_treasure_stop", 35, token);
                    result["new_chest_loot_verified"] = await NativeAgent.OnMain(() => AgentTaskEvidence.VerifyChest(chestBefore), token);
                }
                finally
                {
                    await NativeAgent.OnMain(() =>
                    { TreasureGrab.CloseChest(); Main.playerInventory = inventoryWasOpen; return true; }, CancellationToken.None);
                }
                break;
            case "npc_drop_sources":
                result = await NativeAgent.OnMain(() => AgentCatalog.NpcDropSources(Int(args, "item_type"), Int(args, "limit", 40)), token);
                break;
            case "game_catalog":
                string kind = Text(args, "kind");
                Type constants = kind switch
                {
                    "item" => typeof(Terraria.ID.ItemID), "npc" => typeof(Terraria.ID.NPCID), "tile" => typeof(Terraria.ID.TileID),
                    _ => throw new ArgumentException("kind 必须是 item、npc 或 tile。")
                };
                result = kind == "tile" ? await NativeAgent.OnMain(() => TileCatalog(Text(args, "name")), token)
                    : AgentCatalog.Query(constants, Text(args, "name"));
                result["kind"] = kind;
                if (kind == "tile" && result["found"]?.GetValue<bool>() == false)
                {
                    result["source_guidance"] = "方块目录没有匹配名称，不代表该物品无来源。物品名可能不同于来源方块名；不要猜相似方块名或frame。先查物品编号，NPC来源用npc_drop_sources，方块来源与变体用wiki_search/wiki_page查证。以下只是查询建议，不是来源证据。";
                    result["suggested_queries"] = new JsonArray(
                        new JsonObject { ["tool"] = "game_catalog", ["args"] = new JsonObject { ["kind"] = "item", ["name"] = Text(args, "name") } },
                        new JsonObject { ["tool"] = "wiki_search", ["args"] = new JsonObject { ["query"] = Text(args, "name") } });
                }
                if (kind == "item") await NativeAgent.OnMain(() =>
                {
                    foreach (var match in result["matches"].AsArray())
                    {
                        var item = new Item();
                        item.SetDefaults(match["type"].GetValue<int>());
                        match["places_tile"] = item.createTile < 0 ? null : new JsonObject
                        {
                            ["type"] = item.createTile,
                            ["names"] = AgentCatalog.Query(typeof(Terraria.ID.TileID), item.createTile.ToString())["matches"].DeepClone(),
                            ["detail"] = "Item placement definition only; not proof of harvesting drops or an existing world tile."
                        };
                    }
                    return true;
                }, token);
                break;
            case "task_plan":
                result = await NativeAgent.OnMain(() => Plan.Create(AgentCatalog.ResolvePlanTypes(args), AgentTaskEvidence.Measure), token);
                break;
            case "task_step":
                result = await NativeAgent.OnMain(() => Text(args, "action") switch
                {
                    "verify" => Plan.Advance(Text(args, "id"), AgentTaskEvidence.Measure),
                    "block" => Plan.Block(Text(args, "id"), Text(args, "reason")),
                    _ => throw new InvalidOperationException("action 必须是 verify 或 block。")
                }, token);
                if (Text(args, "action") == "verify" && result["verified"]?.GetValue<bool>() == true)
                    await NativeAgent.OnMain(() =>
                    {
                        foreach (var actionState in Actions.Summary().OfType<JsonObject>())
                            if (actionState["task_node"]?.ToString() == Text(args, "id")) Watches.AcknowledgeCompletion(actionState);
                        return true;
                    }, token);
                break;
            case "combat_reposition":
                var destination = await NativeAgent.OnMain(() =>
                {
                    if (!PvpDuel.Active || args["session"]?.GetValue<long>() != PvpDuel.Session)
                        return new JsonObject { ["error"] = "session_mismatch" };
                    var report = PvpDuel.Report();
                    float x = args["x"].GetValue<float>(), y = args["y"].GetValue<float>();
                    if (!PvpTerrain.ValidStance(Main.LocalPlayer, x, y))
                        return new JsonObject { ["error"] = "stance_no_longer_valid", ["current_probes"] = report["perception"]["terrain"]["stance_probes"].DeepClone() };
                    return new JsonObject { ["x"] = (int)(x / 16),
                        ["y"] = (int)(y / 16),
                        ["before_x"] = Main.LocalPlayer.Center.X, ["before_y"] = Main.LocalPlayer.Center.Y,
                        ["before_line_clear"] = report["perception"]["terrain"]["direct_line"]["clear"].DeepClone() };
                }, token);
                if (destination["error"] != null) { result = destination; break; }
                result = await Navigate(destination, token, 8);
                result["requested_destination"] = destination;
                result["displacement_pixels"] = await NativeAgent.OnMain(() => Microsoft.Xna.Framework.Vector2.Distance(
                    Main.LocalPlayer.Center, new Microsoft.Xna.Framework.Vector2(destination["before_x"].GetValue<float>(),
                        destination["before_y"].GetValue<float>())), token);
                break;
            case "inventory_swap":
            case "inventory_drop":
            case "inventory_trash":
                var idleClock = Stopwatch.StartNew();
                while (idleClock.Elapsed.TotalSeconds < 2 && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                    || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                    await Task.Delay(50, token);
                result = await NativeAgent.OnMain(() => name switch
                {
                    "inventory_drop" => PvpInventory.Drop(args),
                    "inventory_trash" => PvpInventory.Trash(args),
                    _ => PvpInventory.Swap(args)
                }, token);
                break;
            case "equipment_list":
                return (await NativeAgent.OnMain(Equipment.Snapshot, token)).ToJsonString();
            case "inventory_review":
                return (await NativeAgent.OnMain(PvpInventory.Review, token)).ToJsonString();
            case "equip_item":
                result = await NativeAgent.OnMain(() => Equipment.Swap(args), token);
                break;
            case "npc_dialogue_status":
                return (await NativeAgent.OnMain(NpcDialogue.Snapshot, token)).ToJsonString();
            case "npc_chat":
                string chatAction = Text(args, "action");
                if (chatAction == "close")
                    result = await NativeAgent.OnMain(() => { NpcDialogue.Close(); return NpcDialogue.Snapshot(); }, token);
                else if (chatAction == "choose") result = await NpcDialogue.Choose(Int(args, "npc", -1), Int(args, "npc_type", -1),
                    Int(args, "button"), Text(args, "expected_text"), token);
                else if (chatAction == "open")
                {
                    result = await NativeAgent.OnMain(() => NpcDialogue.Open(Int(args, "npc", -1), Int(args, "npc_type", -1)), token);
                    var chatClock = Stopwatch.StartNew();
                    while (result["error"] == null && result["open"]?.GetValue<bool>() == true
                        && result["buttons_ready"]?.GetValue<bool>() != true && chatClock.Elapsed.TotalSeconds < 3)
                    {
                        await Task.Delay(50, token);
                        result = await NativeAgent.OnMain(NpcDialogue.Snapshot, token);
                    }
                    if (result["open"]?.GetValue<bool>() == true && result["buttons_ready"]?.GetValue<bool>() != true)
                        result["error"] = "dialogue_ui_not_ready";
                }
                else throw new ArgumentException("npc_chat.action需要open/choose/close。");
                break;
            case "combat_observe":
                result = new JsonObject { ["error"] = "polling_disabled",
                    ["detail"] = "End this turn. Jev continues combat; code waits for player instructions or a bounded recovery request." };
                break;
            case "npc_combat_observe":
                result = await NativeAgent.OnMain(() =>
                {
                    int who = Int(args, "npc", -1), expected = Int(args, "npc_type", -1);
                    if (who < 0 || who >= Main.maxNPCs || !Main.npc[who].active || Main.npc[who].type != expected
                        || Main.npc[who].friendly || Main.npc[who].townNPC)
                        return new JsonObject { ["error"] = "target_changed" };
                    var npc = Main.npc[who]; var player = Main.LocalPlayer;
                    if (Microsoft.Xna.Framework.Vector2.Distance(player.Center, npc.Center) > 1280)
                        return new JsonObject { ["error"] = "target_out_of_observation_range" };
                    var requestedStances = new JsonArray();
                    if (args["stance_cells"] is JsonArray cells)
                    {
                        if (cells.Count is < 1 or > 12) throw new ArgumentException("stance_cells需要1..12个支撑格坐标。");
                        foreach (var cell in cells)
                            requestedStances.Add(PvpTerrain.ProbeStance(player, npc.Center,
                                cell["x"].GetValue<int>(), cell["y"].GetValue<int>()));
                    }
                    var terrain = PvpTerrain.Observe(player, npc.Center);
                    return new JsonObject { ["npc"] = who, ["npc_type"] = npc.type, ["hp"] = npc.life,
                        ["clear_line"] = terrain["direct_line"]["clear"].DeepClone(),
                        ["requested_stances"] = requestedStances,
                        ["target_pos"] = new JsonObject { ["x"] = npc.Center.X, ["y"] = npc.Center.Y },
                        ["terrain"] = terrain,
                        ["limitations"] = "只读瞬时快照；NPC槽位可能复用。站位仅检查支撑和身体空间，未验证路径、落差、液体、陷阱或弹幕。选择后需检查危险，移动后刷新射界。" };
                }, token);
                break;
            case "combat_update":
                result = await NativeAgent.OnMain(() => PvpDuel.UpdateDirective(args), token);
                break;
            case "combat_stop":
                result = await NativeAgent.OnMain(() =>
                {
                    if (args["session"]?.GetValue<long>() != PvpDuel.Session) return new JsonObject { ["error"] = "session_mismatch" };
                    PvpDuel.Stop("agent_stopped"); return PvpDuel.Report();
                }, token);
                break;
            case "set_combat":
                result = await NativeAgent.OnMain(() =>
                {
                    double? reserve = args["movement_reserve_scale"]?.GetValue<double>();
                    if (reserve is double scale && (!double.IsFinite(scale) || scale < .05 || scale > 1))
                        return new JsonObject { ["error"] = "movement_reserve_scale_needs_0_05_to_1" };
                    if (args["weapon_type"] != null)
                    {
                        int weaponType = Int(args, "weapon_type");
                        if (weaponType < 0 || weaponType > 0 && !Main.LocalPlayer.inventory.Take(10).Any(item =>
                            item.type == weaponType && CombatLoadout.Usable(item)))
                            return new JsonObject { ["error"] = "weapon_type_must_be_usable_in_hotbar" };
                        var weapon = weaponType > 0 ? Main.LocalPlayer.inventory.Take(10).First(item =>
                            item.type == weaponType && CombatLoadout.Usable(item)) : null;
                        if (weapon != null && weapon.useAmmo > 0 && !Main.LocalPlayer.HasAmmo(weapon))
                            return new JsonObject { ["error"] = "weapon_missing_ammo", ["requested_weapon_type"] = weaponType,
                                ["use_ammo"] = weapon.useAmmo, ["weapon_type"] = CombatLoadout.PreferredType,
                                ["detail"] = "未修改战斗设置；补充匹配弹药或选择当前可用武器。" };
                        CombatLoadout.PreferredType = weaponType;
                    }
                    if (args["boss_control"] != null)
                    {
                        bossBefore ??= (Combat.Enabled, Dodge.Enabled, Dodge.MovementReserveScale);
                        Combat.Enabled = Dodge.Enabled = Bool(args, "boss_control");
                        if (!Combat.Enabled) Combat.Stop();
                    }
                    if (reserve is double requestedReserve)
                    {
                        bossBefore ??= (Combat.Enabled, Dodge.Enabled, Dodge.MovementReserveScale);
                        Dodge.MovementReserveScale = (float)requestedReserve;
                    }
                    if (args["auto_dodge"] != null) AgentPermissions.Dodge = Bool(args, "auto_dodge");
                    if (args["auto_attack"] != null) AgentPermissions.Attack = Bool(args, "auto_attack");
                    if (!AgentPermissions.Attack) { Combat.Stop(); FightCoordinator.Stop(); PlayerCombat.Stop(); }
                    return new JsonObject { ["auto_dodge"] = AgentPermissions.Dodge, ["auto_attack"] = AgentPermissions.Attack,
                        ["weapon_type"] = CombatLoadout.PreferredType,
                        ["boss_control"] = BossStatus() };
                }, token);
                break;
            case "boss_status":
                return (await NativeAgent.OnMain(BossStatus, token)).ToJsonString();
            case "track":
                result = await NativeAgent.OnMain(() =>
                {
                    Tracking.Clear();
                    foreach (var item in args["items"].AsObject()) Tracking[item.Key] = item.Value.GetValue<int>();
                    return new JsonObject { ["outcome"] = "tracking" };
                }, token);
                break;
            case "descent_route":
                result = await DescentRoute(args, token);
                break;
            case "item_info": case "find_biome": case "find_descent":
            case "tile_names": case "recipe":
                result = await Call("/" + name, args, token);
                break;
            case "find_tiles":
                result = await FindTiles(args, token);
                break;
            case "nav_to":
                result = await Navigate(args, token);
                break;
            case "mine_vein":
                result = await MineVein(args, token);
                break;
            case "chop_wood":
                int wantedWood = Int(args, "count");
                if (wantedWood < 1 || wantedWood > 9999) return "{\"error\":\"count 必须在 1 到 9999 之间。\"}";
                string key = await NativeAgent.OnMain(() => Config.I.TypeSafeKey?.Trim(), token);
                if (string.IsNullOrEmpty(key)) return "{\"error\":\"请先配置 Jev 的 TypeSafe Key。\"}";
                await NativeAgent.OnMain(() => { StateSpacePlanner.PreserveTerrain = true; return true; }, token);
                try
                {
                    var decision = new WoodHarvestDecision(HarvestHttp, key);
                    result = await WoodHarvest.Run(wantedWood, Text(args, "constraints"), new GameWoodHarvest(), async (scene, ct) =>
                    {
                        var clock = Stopwatch.StartNew();
                        string choice = await decision.Choose(scene, ct);
                        await NativeAgent.OnMain(() => { DiagLog.Write($"[harvest] jev choice={choice} ms={clock.ElapsedMilliseconds} scene={scene}"); return true; }, ct);
                        return choice;
                    }, token);
                }
                finally
                {
                    await NativeAgent.OnMain(() => { StateSpacePlanner.PreserveTerrain = false; return true; }, token);
                }
                break;
            case "mine":
                result = await CollectTarget(Int(args, "target_x"), Int(args, "target_y"), token);
                if (result != null) break;
                result = await Call("/mine", new JsonObject { ["dir"] = Text(args, "dir"),
                    ["target_wx"] = Int(args, "target_x"), ["target_wy"] = Int(args, "target_y") }, token);
                if (!Rejected(result)) result = await Wait("/mine_status", "running", "/mine_stop", 120, token);
                break;
            case "use_item":
                var usedItem = await NativeAgent.OnMain(() =>
                {
                    var player = Main.LocalPlayer;
                    int slot = Int(args, "slot", -1);
                    if (slot < 0 || slot >= player.inventory.Length) return null;
                    var item = player.inventory[slot];
                    return new JsonObject { ["type"] = item.type, ["consumable"] = item.consumable, ["heal_life"] = item.healLife,
                        ["collect_tool"] = item.pick > 0 || item.axe > 0 || item.hammer > 0,
                        ["count"] = player.inventory.Where(i => i.type == item.type).Sum(i => i.stack) };
                }, token);
                if (usedItem?["collect_tool"]?.GetValue<bool>() == true)
                {
                    var collection = await CollectTarget(Int(args, "x"), Int(args, "y"), token, requestedSlot: Int(args, "slot", -1));
                    if (collection != null) { result = collection; break; }
                }
                result = await Call("/item_use", new JsonObject
                {
                    ["target_wx"] = Int(args, "x"), ["target_wy"] = Int(args, "y"), ["slot"] = Int(args, "slot", -1),
                    ["duration_ticks"] = Int(args, "duration_ticks", 30), ["strict"] = Bool(args, "strict")
                }, token);
                if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
                if (Text(result, "outcome") == "n/a" && usedItem?["consumable"]?.GetValue<bool>() == true)
                    result = await NativeAgent.OnMain(() =>
                    {
                        var player = Main.LocalPlayer;
                        int remaining = player.inventory.Where(i => i.type == usedItem["type"].GetValue<int>()).Sum(i => i.stack);
                        int consumed = usedItem["count"].GetValue<int>() - remaining;
                        result["consumed"] = Math.Max(0, consumed); result["verified"] = consumed > 0;
                        if (consumed > 0) result["outcome"] = "consumed";
                        else
                        {
                            result["error"] = "item_use_not_observed"; result["outcome"] = "no_progress";
                            result["reason"] = usedItem["heal_life"].GetValue<int>() > 0 && player.potionDelay > 0
                                ? "potion_sickness" : "consumable_count_unchanged";
                        }
                        result["potion_delay_ticks"] = player.potionDelay;
                        return result;
                    }, token);
                break;
            case "place_at":
                result = await Place(args, token);
                break;
            case "interact":
                result = await Call("/interact", new JsonObject { ["tile_x"] = Int(args, "x"), ["tile_y"] = Int(args, "y") }, token);
                if (!Rejected(result))
                {
                    await Task.Delay(200, token);
                    result = await NativeAgent.OnMain(() => new JsonObject { ["outcome"] = HttpServerSystem.LastInteract }, token);
                }
                break;
            case "loot_all":
                result = await Call("/loot_all", args, token);
                await Task.Delay(300, token);
                break;
            case "craft":
                result = await Call("/craft", new JsonObject { ["item_name"] = Text(args, "name"), ["amount"] = Int(args, "amount", 1) }, token);
                break;
            case "act":
                result = await Call("/act", args, token);
                if (!Rejected(result)) result = await Wait("/act_status", "active", "/act_stop", 120, token);
                break;
            case "fight":
                if (!await NativeAgent.OnMain(() => AgentPermissions.Attack, token))
                    return "{\"error\":\"玩家禁止自动攻击，请先确认是否修改许可。\"}";
                if (args["npc_types"] is JsonArray || args.ContainsKey("movement"))
                {
                    int[] types = (args["npc_types"] as JsonArray)?.Select(n => n.GetValue<int>()).ToArray();
                    if (types != null && (types.Length == 0 || types.Any(t => t <= 0))) throw new ArgumentException("npc_types 不能为空且编号必须为正数。");
                    string movement = args["movement"]?.GetValue<string>() ?? "auto";
                    result = await NativeAgent.OnMain(() =>
                    { FightCoordinator.Start(Int(args, "max_dist", 40), types, movement); return new JsonObject { ["ok"] = true }; }, token);
                }
                else result = await Call("/fight", args, token);
                if (!Rejected(result))
                {
                    result = await Wait("/fight_active", "active", "/fight", Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120), token);
                    await Call("/fight", new JsonObject { ["active"] = false }, token);
                    if (Text(result, "outcome") == "timeout")
                        result = new JsonObject { ["outcome"] = "duration_elapsed",
                            ["detail"] = "本次攻击时段结束，不代表敌怪已击杀；导航继续，按实际需要决定是否再次攻击。" };
                }
                break;
            case "combat_start":
            case "duel_player":
                int duelTarget = Int(args, "player_id", -1);
                int duelSeconds = Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 30), 1, 120);
                result = await NativeAgent.OnMain(() =>
                {
                    int weaponSlot = Int(args, "slot", Main.LocalPlayer.selectedItem);
                    if (!PvpDuel.Start(duelTarget, duelSeconds, weaponSlot, Math.Clamp(Int(args, "max_dist", 300), 1, 600), Text(args, "strategy"), out string reason,
                        untilStopped: Bool(args, "until_stopped")))
                        return new JsonObject { ["error"] = reason };
                    if (args["constraints"] != null || args["instructions"] != null)
                    {
                        var patch = new JsonObject { ["session"] = PvpDuel.Session, ["expected_revision"] = PvpDuel.Directive.Revision,
                            ["constraints"] = args["constraints"]?.DeepClone(), ["instructions"] = args["instructions"]?.DeepClone() };
                        var updated = PvpDuel.UpdateDirective(patch);
                        if (updated["error"] != null) { PvpDuel.Stop("invalid_directive"); return updated; }
                    }
                    return PvpDuel.Report();
                }, token);
                break;
            case "fight_player":
                int target = Int(args, "player_id", -1);
                int seconds = Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120);
                float range = Math.Clamp(Int(args, "max_dist", 80), 1, 200);
                result = await NativeAgent.OnMain(() =>
                {
                    if (!PlayerCombat.Start(target, seconds, range, out string reason))
                        return new JsonObject { ["error"] = reason };
                    return new JsonObject { ["target_player"] = target, ["target_hp_before"] = Main.player[target].statLife,
                        ["self_pvp_enabled"] = Main.LocalPlayer.hostile };
                }, token);
                if (!Rejected(result))
                {
                    var timer = Stopwatch.StartNew();
                    while (await NativeAgent.OnMain(() => PlayerCombat.Active, token) && timer.Elapsed.TotalSeconds < seconds + 3)
                        await Task.Delay(150, token);
                    await NativeAgent.OnMain(() =>
                    {
                        if (PlayerCombat.Active) PlayerCombat.Stop("timeout");
                        result["outcome"] = PlayerCombat.Outcome;
                        result["target_hp_after"] = Main.player[target].statLife;
                        return true;
                    }, token);
                }
                break;
            default: throw new InvalidOperationException($"未知工具: {name}");
        }
        string output = await Result(result, before, token);
        return await ToolFinished(name, output, token);
    }

    static JsonObject TileCatalog(string name) => AgentCatalog.Query(typeof(Terraria.ID.TileID), name, type =>
        type < 0 || type >= Terraria.Map.MapHelper.tileOptionCounts.Length ? Array.Empty<string>() :
        Enumerable.Range(0, Terraria.Map.MapHelper.tileOptionCounts[type])
            .Select(option => Lang.GetMapObjectName(Terraria.Map.MapHelper.TileToLookup(type, option)))
            .Where(label => !string.IsNullOrWhiteSpace(label)).Distinct().ToArray());

    delegate void TileLight(Terraria.Tile tile, int x, int y, ref Terraria.Utilities.FastRandom random,
        ref Microsoft.Xna.Framework.Vector3 color);
    static readonly TileLight ReadTileLight = typeof(Terraria.Graphics.Light.TileLightScanner)
        .GetMethod("ApplyTileLight", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .CreateDelegate<TileLight>(new Terraria.Graphics.Light.TileLightScanner());

    static JsonObject TileEmission(int x, int y)
    {
        var random = new Terraria.Utilities.FastRandom(0);
        var color = Microsoft.Xna.Framework.Vector3.Zero;
        ReadTileLight(Main.tile[x, y], x, y, ref random, ref color);
        return new JsonObject { ["r"] = color.X, ["g"] = color.Y, ["b"] = color.Z,
            ["emits_light"] = color.X > 0 || color.Y > 0 || color.Z > 0 };
    }

    internal static async Task<JsonObject> FindTiles(JsonObject args, CancellationToken token)
    {
        var catalog = await NativeAgent.OnMain(() => TileCatalog(Text(args, "name")), token);
        var exact = catalog["matches"].AsArray().Where(m => m["exact"].GetValue<bool>()).ToArray();
        int types = exact.Select(m => m["type"].GetValue<int>()).Distinct().Count();
        var area = GatherArea.Parse(args["source_area"] as JsonObject);
        const string unknownSourceDetail = "find_tiles查询方块类型，不是物品获取来源。若输入的是材料名，先game_catalog(kind:item)查物品编号，再用npc_drop_sources查NPC掉落或wiki_search/wiki_page查方块来源与变体；不要猜方块、NPC或frame编号。";
        if (area == null && args["emits_light"] != null)
            throw new ArgumentException("按emits_light筛选find_tiles需要source_area，不能忽略筛选条件。");
        if (area != null && types == 0)
            return new JsonObject { ["error"] = "unknown_tile_name", ["name_candidates"] = catalog["matches"].DeepClone(),
                ["detail"] = unknownSourceDetail };
        if (catalog["truncated"].GetValue<bool>() || types > 1)
            return new JsonObject { ["error"] = "ambiguous_tile_name", ["requested_name"] = Text(args, "name"),
                ["name_candidates"] = catalog["matches"].DeepClone() };
        if (types == 0)
        {
            var unresolved = await Call("/find_tiles", args, token);
            if (Text(unresolved, "error") is "unknown_tile_name" or "bad_params")
            {
                unresolved["name_candidates"] = catalog["matches"].DeepClone();
                unresolved["detail"] = unknownSourceDetail;
            }
            return unresolved;
        }
        var resolved = (JsonObject)args.DeepClone();
        resolved["name"] = exact[0]["internal"].GetValue<string>();
        if (area != null)
            return await NativeAgent.OnMain(() =>
            {
                if ((long)area.X + area.Width > Main.maxTilesX || (long)area.Y + area.Height > Main.maxTilesY)
                    throw new ArgumentException("source_area超出世界边界。");
                int type = exact[0]["type"].GetValue<int>();
                var matches = new List<JsonObject>();
                for (int x = area.X; x < area.X + area.Width; x++)
                    for (int y = area.Y; y < area.Y + area.Height; y++)
                        if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type
                            && (args["frame_x"] == null || Main.tile[x, y].TileFrameX == Int(args, "frame_x"))
                            && (args["frame_y"] == null || Main.tile[x, y].TileFrameY == Int(args, "frame_y")))
                        {
                            var emission = TileEmission(x, y);
                            if (args["emits_light"] != null && Bool(args, "emits_light") != emission["emits_light"].GetValue<bool>()) continue;
                            double dx = x - Main.LocalPlayer.Center.X / 16, dy = y - Main.LocalPlayer.Center.Y / 16;
                            matches.Add(new JsonObject { ["x"] = x, ["y"] = y, ["dist"] = (float)Math.Sqrt(dx * dx + dy * dy),
                                ["emission"] = emission,
                                ["tile_type"] = type, ["frame_x"] = (int)Main.tile[x, y].TileFrameX, ["frame_y"] = (int)Main.tile[x, y].TileFrameY });
                        }
                int limit = Math.Clamp(Int(args, "n", 12), 1, 100);
                var ordered = matches.OrderBy(m => m["dist"].GetValue<float>()).ToArray();
                var variants = ordered.GroupBy(m => (X: m["frame_x"].GetValue<int>(), Y: m["frame_y"].GetValue<int>())).ToArray();
                return new JsonObject { ["tiles"] = new JsonArray(ordered
                    .Take(limit).Select(m => (JsonNode)m).ToArray()),
                    ["variants"] = new JsonArray(variants.Take(32).Select(group => (JsonNode)new JsonObject
                    { ["frame_x"] = group.Key.X, ["frame_y"] = group.Key.Y, ["count"] = group.Count(),
                        ["nearest"] = group.First().DeepClone() }).ToArray()),
                    ["variants_truncated"] = variants.Length > 32,
                    ["variants_detail"] = "汇总本次source_area和frame筛选下全部匹配格，独立于n截取；最多32种，按最近样本距离排序。frame不是Wiki样式号，不证明掉落物。由Agent选择进一步观察或试采，并核实所得。",
                    ["total_matches"] = matches.Count, ["truncated"] = matches.Count > limit,
                    ["source_area"] = area.Snapshot(), ["resolved_name"] = resolved["name"].DeepClone() };
            }, token);
        var result = await Call("/find_tiles", resolved, token);
        if (result["tiles"] is JsonArray tiles)
            await NativeAgent.OnMain(() =>
            {
                int stale = 0;
                for (int i = tiles.Count - 1; i >= 0; i--)
                {
                    int x = tiles[i]["x"].GetValue<int>(), y = tiles[i]["y"].GetValue<int>();
                    if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY
                        || !Main.tile[x, y].HasTile || Main.tile[x, y].TileType != exact[0]["type"].GetValue<int>()
                        || args["frame_x"] != null && Main.tile[x, y].TileFrameX != Int(args, "frame_x")
                        || args["frame_y"] != null && Main.tile[x, y].TileFrameY != Int(args, "frame_y"))
                    { tiles.RemoveAt(i); stale++; continue; }
                    tiles[i]["tile_type"] = (int)Main.tile[x, y].TileType;
                    tiles[i]["frame_x"] = (int)Main.tile[x, y].TileFrameX;
                    tiles[i]["frame_y"] = (int)Main.tile[x, y].TileFrameY;
                    tiles[i]["emission"] = TileEmission(x, y);
                }
                result["stale_candidates_removed"] = stale;
                return true;
            }, token);
        result["requested_name"] = Text(args, "name");
        result["resolved_name"] = resolved["name"].DeepClone();
        return result;
    }

    static async Task<string> ToolFinished(string name, string output, CancellationToken token)
    {
        await NativeAgent.OnMain(() => { DiagLog.Write($"[agent] result {name} {output}"); return true; }, token);
        await NativeAgent.OnMain(() => { NativeAgent.Progress("执行结束：" + name + "，等待下一步决策。", "等待模型决策",
            !PvpDuel.Active && !name.StartsWith("combat_")); return true; }, token);
        return output;
    }

    internal static async Task<JsonObject> CollectTarget(int x, int y, CancellationToken token, bool nonSolidOnly = true, int requestedSlot = -1)
    {
        var target = await NativeAgent.OnMain(() =>
        {
            if (!WorldGen.InWorld(x, y)) return null;
            var tile = Main.tile[x, y];
            if (!tile.HasUnactuatedTile || nonSolidOnly && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType]) return null;
            int Power(Item item) => Main.tileAxe[tile.TileType] ? item.axe : Main.tileHammer[tile.TileType] ? item.hammer : item.pick;
            int slot = requestedSlot >= 0 ? requestedSlot : Enumerable.Range(0, 50).OrderByDescending(i => Power(Main.LocalPlayer.inventory[i]))
                .FirstOrDefault(i => !Main.LocalPlayer.inventory[i].IsAir && Power(Main.LocalPlayer.inventory[i]) > 0, -1);
            bool canMine = false;
            if (slot >= 0)
            {
                int previous = Main.LocalPlayer.selectedItem;
                try { Main.LocalPlayer.selectedItem = slot; canMine = Reach.CanMine(Main.LocalPlayer, x, y); }
                finally { Main.LocalPlayer.selectedItem = previous; }
            }
            return new JsonObject { ["slot"] = slot, ["in_reach"] = canMine };
        }, token);
        if (target == null) return null;
        if (Int(target, "slot", -1) < 0) return new JsonObject { ["error"] = "no_collect_tool" };
        if (!Bool(target, "in_reach"))
        {
            var approach = await Navigate(new JsonObject { ["x"] = x, ["y"] = y, ["mode"] = "interact" }, token);
            if (approach["done"]?.GetValue<bool>() != true)
                return new JsonObject { ["error"] = "cannot_reach_collect_target", ["navigation"] = approach };
            bool reachable = await NativeAgent.OnMain(() =>
            {
                var player = Main.LocalPlayer;
                int previous = player.selectedItem;
                try { player.selectedItem = Int(target, "slot", -1); return Reach.CanMine(player, x, y); }
                finally { player.selectedItem = previous; }
            }, token);
            if (!reachable) return new JsonObject { ["error"] = "collect_target_out_of_mining_range",
                ["x"] = x, ["y"] = y, ["navigation"] = approach };
        }
        var result = await Call("/item_use", new JsonObject { ["target_wx"] = x, ["target_wy"] = y,
            ["slot"] = target["slot"].DeepClone(), ["strict"] = true }, token);
        if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 15, token);
        if (Text(result, "outcome") == "removed") await Task.Delay(400, token);
        result["mode_adjustment"] = "target_use_collect_tool";
        return result;
    }

    static async Task<JsonObject> DescentRoute(JsonObject args, CancellationToken token)
    {
        int treasureOffset = Int(args, "treasure_offset", 0), treasureLimit = Int(args, "treasure_limit", 16);
        if (treasureOffset < 0 || treasureLimit is < 1 or > 32) throw new ArgumentException("宝藏分页需要非负offset和1..32的limit。");
        var route = await Call("/descent_main_route", new JsonObject { ["name"] = Text(args, "name"),
            ["offset"] = Int(args, "offset", 0), ["limit"] = Int(args, "limit", 256) }, token);
        if (Rejected(route) || route["found"]?.GetValue<bool>() != true) return route;
        var query = new JsonObject { ["name"] = Text(args, "name") };
        foreach (string field in new[] { "dig_max", "walk_max", "dig_max2", "walk_max2" })
            if (args[field] != null) query[field] = args[field].DeepClone();
        var background = await Call("/descent_route_status", query, token);
        string status = background["status"]?.ToString();
        if (status == "not_started")
        {
            var started = await Call("/descent_route_async", query, token);
            status = Rejected(started) ? "failed" : "running";
            if (Rejected(started)) route["treasures_error"] = started;
        }
        route["treasures_status"] = status;
        route["treasure_offset"] = treasureOffset;
        if (status == "ready" && background["result"]?["treasures"] is JsonArray treasures)
        {
            route["treasures"] = new JsonArray(treasures.Skip(treasureOffset).Take(treasureLimit).Select(t => t.DeepClone()).ToArray());
            route["treasures_total"] = treasures.Count;
            route["treasures_next_offset"] = treasureOffset + treasureLimit < treasures.Count
                ? JsonValue.Create(treasureOffset + treasureLimit) : null;
        }
        return route;
    }

    internal static async Task<JsonObject> Navigate(JsonObject args, CancellationToken token, int seconds = 0)
    {
        var region = await NativeAgent.OnMain(() => Control.CheckAt(ControlBehavior.Navigation, Int(args, "x"), Int(args, "y")), token);
        if (region != null) return region;
        JsonObject escapeBefore = null;
        var acceptedTraps = args["accepted_traps"]?.AsArray();
        var hazard = await NativeAgent.OnMain(() =>
        { escapeBefore = LastContactEscape; return TerrainInspection.NavigationHazard(acceptedTraps); }, token);
        if (hazard != null) return hazard;
        var request = await NativeAgent.OnMain(() =>
        {
            int x = Int(args, "x"), y = Int(args, "y");
            bool reach = Bool(args, "reach"), exact = Bool(args, "exact"), stand = Bool(args, "stand");
            string mode = Text(args, "mode");
            if (mode.Length > 0)
            {
                if (mode is not ("travel" or "mine" or "interact" or "stand")) throw new ArgumentException("mode需要travel/mine/interact/stand。");
                reach = mode is "travel" or "interact"; exact = mode == "mine"; stand = mode == "stand";
            }
            else if (!exact && !stand) reach = true;
            if (exact && WorldGen.InWorld(x, y))
            {
                var tile = Main.tile[x, y];
                if (tile.HasTile && (!tile.HasUnactuatedTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]))
                    reach = true;
            }
            return new JsonObject { ["gx"] = x, ["gy"] = y, ["exact"] = exact && !reach, ["reach"] = reach,
                ["stand"] = stand };
        }, token);
        var start = await Call("/nav_recede", request, token);
        if (Rejected(start)) return start;
        var progress = new NavigationProgress();
        bool? lastPlanning = null;
        var result = await Wait("/nav_recede_done", "nav", "/nav_recede_stop", seconds, token,
            elapsed => NativeAgent.OnMain(() =>
            {
                var player = Main.LocalPlayer;
                bool planning = !RecedingNav.FieldReady;
                if (lastPlanning != planning)
                {
                    lastPlanning = planning;
                    DiagLog.Write($"[agent_navigation_phase] phase={(planning ? "building_field" : "executing")} elapsed_seconds={elapsed:0.###}");
                }
                var trap = TerrainInspection.NavigationHazard(acceptedTraps);
                if (trap != null) return trap;
                if (player.suffocating)
                    return new JsonObject { ["error"] = "navigation_suffocating", ["outcome"] = "unsafe",
                        ["hp"] = player.statLife, ["pos"] = new JsonObject { ["x"] = player.position.X, ["y"] = player.position.Y },
                        ["detail"] = "原生人物已窒息；停止路线，先脱困再恢复。" };
                int px = (int)player.Center.X / 16, py = (int)player.Center.Y / 16;
                var terrain = new Dictionary<int, long>();
                for (int x = px - 4; x <= px + 4; x++)
                for (int y = py - 4; y <= py + 4; y++)
                    if (WorldGen.InWorld(x, y))
                    {
                        var tile = Main.tile[x, y];
                        terrain[x * Main.maxTilesY + y] = tile.HasTile
                            ? 1L + ((long)tile.TileType << 8) + ((long)tile.Slope << 4) + (tile.IsHalfBlock ? 2 : 0) : 0;
                    }
                double distance = Math.Abs(player.Center.X - Int(args, "x") * 16)
                    + Math.Abs(player.Center.Y - Int(args, "y") * 16);
                return progress.Observe(elapsed, player.position.X, player.position.Y, distance, terrain, planning);
            }, token));
        return await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            if (!Bool(result, "done") && LastContactEscape != null && !ReferenceEquals(escapeBefore, LastContactEscape))
            {
                result["outcome"] = "unsafe"; result["error"] = "navigation_contact_escape";
                result["contact_escape"] = LastContactEscape.DeepClone();
            }
            int distance = Math.Max(Math.Abs(ActExecutor.OriginCx(p) - Int(args, "x")), Math.Abs(ActExecutor.OriginCy(p) - Int(args, "y")));
            bool arrived = Bool(request, "reach") ? Reach.CanInteract(p, Int(args, "x"), Int(args, "y")) : distance <= 6;
            if (Bool(request, "reach") && (Bool(args, "exact") || Text(args, "mode") == "mine"))
                result["mode_adjustment"] = "non_solid_target_use_reach_then_use_tool";
            if (Bool(result, "done") && !arrived)
            { result["done"] = false; result["status"] = "stopped_short"; result["dist"] = distance; }
            return result;
        }, token);
    }

    static async Task<JsonObject> Place(JsonObject args, CancellationToken token)
    {
        int count = Math.Clamp(Int(args, "n", 1), 1, 200);
        int placed = 0;
        var cells = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            var payload = new JsonObject { ["item"] = Text(args, "item") };
            if (args["x"] != null && args["y"] != null)
                payload["world"] = new JsonArray(Int(args, "x") + i * Int(args, "step_x"), Int(args, "y") + i * Int(args, "step_y"));
            var result = await Call("/place_anywhere", payload, token);
            var placement = (JsonObject)result.DeepClone();
            if (!Rejected(result)) result = await Wait("/place_anywhere_status", "running", "/place_anywhere_stop", 120, token);
            if (Text(result, "outcome") == "done")
                result = await NativeAgent.OnMain(() =>
                {
                    int x = placement["world"]?[0]?.GetValue<int>() ?? -1, y = placement["world"]?[1]?.GetValue<int>() ?? -1;
                    int expectedTile = Int(placement, "expected_tile", -1), expectedWall = Int(placement, "expected_wall", -1);
                    bool inWorld = x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
                    bool verified = inWorld && (expectedTile >= 0 && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == expectedTile
                        || expectedWall > 0 && Main.tile[x, y].WallType == expectedWall);
                    result["world"] = placement["world"]?.DeepClone(); result["verified"] = verified;
                    if (!verified) { result["outcome"] = "stuck"; result["error"] = "placement_not_verified"; }
                    return result;
                }, token);
            cells.Add(result);
            if (Text(result, "outcome") != "done") break;
            placed++;
        }
        return new JsonObject { ["outcome"] = placed == count ? "done" : placed > 0 ? "partial" : "stuck", ["placed"] = placed, ["wanted"] = count, ["cells"] = cells };
    }

    static async Task<JsonObject> MineVein(JsonObject args, CancellationToken token)
    {
        string name = Text(args, "name");
        int wanted = Math.Clamp(Int(args, "count", 1), 1, 1000), removed = 0;
        var skipped = new HashSet<(int, int)>();
        string outcome = "exhausted";
        for (int attempt = 0; attempt < wanted + 8 && removed < wanted; attempt++)
        {
            var found = await Call("/find_tiles", new JsonObject { ["name"] = name, ["n"] = 30, ["max_dist"] = 2000 }, token);
            var target = found["tiles"]?.AsArray().OfType<JsonObject>().FirstOrDefault(t => !skipped.Contains((Int(t, "x"), Int(t, "y"))));
            if (target == null) break;
            int x = Int(target, "x"), y = Int(target, "y");
            skipped.Add((x, y));
            await Navigate(new JsonObject { ["x"] = x, ["y"] = y, ["exact"] = true }, token);
            var reach = await Call("/mine_reach", null, token);
            if (Rejected(reach)) continue;
            int slot = await NativeAgent.OnMain(() => Enumerable.Range(0, 10)
                .OrderByDescending(i => Main.LocalPlayer.inventory[i].pick)
                .FirstOrDefault(i => Main.LocalPlayer.inventory[i].pick > 0, -1), token);
            if (slot < 0) return new JsonObject { ["outcome"] = "no_pickaxe", ["tiles_removed"] = removed };
            var nearby = await Call("/find_tiles", new JsonObject { ["name"] = name, ["n"] = 40, ["max_dist"] = 60 }, token);
            foreach (var tile in nearby["tiles"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (removed >= wanted) break;
                int tx = Int(tile, "x"), ty = Int(tile, "y");
                if (tx < Int(reach, "lx") || tx > Int(reach, "hx") || ty < Int(reach, "ly") || ty > Int(reach, "hy")) continue;
                var started = await Call("/item_use", new JsonObject { ["target_wx"] = tx, ["target_wy"] = ty, ["slot"] = slot, ["strict"] = true, ["duration_ticks"] = 0 }, token);
                if (Rejected(started)) continue;
                var result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
                if (Text(result, "outcome") == "removed") removed++;
                if (Text(result, "reason") == "tool_weak") return new JsonObject { ["outcome"] = "tool_weak", ["tiles_removed"] = removed };
            }
        }
        if (removed >= wanted) outcome = "enough";
        return new JsonObject { ["outcome"] = outcome, ["tiles_removed"] = removed, ["wanted"] = wanted,
            ["note"] = "清除方块数不代表获得物品数，以 got 和背包实际数量为准。" };
    }

    static async Task<string> ReadWiki(string name, JsonObject args, CancellationToken token)
    {
        string query = name == "wiki_search"
            ? "action=query&list=search&srsearch=" + Uri.EscapeDataString(Text(args, "query")) + "&srlimit=5"
            : "action=parse&prop=text&disablelimitreport=1&redirects=1&page=" + Uri.EscapeDataString(Text(args, "title"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://terraria.wiki.gg/api.php?format=json&" + query);
        request.Headers.UserAgent.ParseAdd("TerraBlind/1.0");
        using var response = await Wiki.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(token);
        var data = JsonNode.Parse(text);
        if (name == "wiki_search") return (data?["query"]?["search"] ?? data).ToJsonString();
        string html = data?["parse"]?["text"]?["*"]?.GetValue<string>();
        if (html == null) return new JsonObject { ["error"] = "wiki_not_found", ["title"] = Text(args, "title") }.ToJsonString();
        var page = WikiPage(Text(args, "title"), html);
        await ObserveWikiTiles(page, token);
        return page.ToJsonString();
    }

    internal static async Task<JsonObject> ObserveWikiTiles(JsonObject page, CancellationToken token)
    {
        var listed = page["listed_ids"].AsArray().Where(id => Text(id.AsObject(), "kind") == "tile")
            .GroupBy(id => id["type"].GetValue<int>()).Select(group => group.First()).ToArray();
        if (listed.Length == 0) return page;
        var area = await NativeAgent.OnMain(() =>
        {
            int x = Math.Max(0, (int)Main.LocalPlayer.Center.X / 16 - 60);
            int y = Math.Max(0, (int)Main.LocalPlayer.Center.Y / 16 - 60);
            return new GatherArea(x, y, Math.Min(120, Main.maxTilesX - x), Math.Min(120, Main.maxTilesY - y));
        }, token);
        var observations = new JsonArray();
        foreach (var id in listed.Take(4))
        {
            var match = id["catalog_matches"].AsArray().FirstOrDefault();
            if (match == null) continue;
            var found = await FindTiles(new JsonObject { ["name"] = match["internal"].DeepClone(),
                ["n"] = 3, ["source_area"] = area.Snapshot() }, token);
            observations.Add(new JsonObject { ["wiki_tile_id"] = id["type"].DeepClone(),
                ["wiki_style"] = id["wiki_style"]?.DeepClone(), ["observed"] = found });
        }
        page["nearby_listed_tiles"] = observations;
        page["nearby_listed_tiles_truncated"] = listed.Length > 4;
        page["nearby_listed_tiles_detail"] = "按本页明确列出的Tile ID查询玩家附近120×120格，不从正文中的生长基底猜来源。variants包含每种现场帧的最近样本及自身发光emission，可结合正文描述选择；wiki_style不直接转成frame。空结果仅表示这个局部没有匹配。仅观察，未采集，不证明掉落或安全可达。";
        var lines = new List<string> { "【当前游戏现场，非Wiki原文】以下仅为本页明确列出类型的附近候选，不证明掉落或可达；格坐标。每类最多展示8种变体，发光样本优先；完整列表见nearby_listed_tiles。" };
        foreach (var observation in observations)
        {
            var found = observation["observed"];
            lines.Add($"TileID={observation["wiki_tile_id"]} {found["resolved_name"]}，本区域匹配={found["total_matches"]}。");
            foreach (var variant in (found["variants"] as JsonArray ?? new JsonArray())
                .OrderByDescending(v => v["nearest"]?["emission"]?["emits_light"]?.GetValue<bool>() == true).Take(8))
            {
                var cell = variant["nearest"];
                lines.Add($"({cell["x"]},{cell["y"]}) frame=({cell["frame_x"]},{cell["frame_y"]}) emission={cell["emission"]?.ToJsonString()}。");
            }
        }
        page["text"] = string.Join("\n", lines) + "\n【Wiki正文】\n" + page["text"]?.GetValue<string>();
        return page;
    }

    internal static JsonObject WikiPage(string title, string html)
    {
        string Plain(string value) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " "));
        var ids = new JsonArray();
        int total = 0;
        foreach (System.Text.RegularExpressions.Match entry in System.Text.RegularExpressions.Regex.Matches(html, @"<li\b[^>]*>(.*?)</li>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            string label = System.Text.RegularExpressions.Regex.Replace(Plain(entry.Groups[1].Value), @"\s+", " ").Trim();
            var match = System.Text.RegularExpressions.Regex.Match(label, @"^Internal (Item|Tile|NPC|Projectile) ID\s*:\s*(\d+)(?:\s*\((\d+)\))?$");
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out int type)) continue;
            int? style = null;
            if (match.Groups[3].Success)
            {
                if (!int.TryParse(match.Groups[3].Value, out int parsedStyle)) continue;
                style = parsedStyle;
            }
            if (++total > 32) continue;
            string kind = match.Groups[1].Value.ToLowerInvariant();
            Type constants = kind switch { "item" => typeof(Terraria.ID.ItemID), "tile" => typeof(Terraria.ID.TileID),
                "npc" => typeof(Terraria.ID.NPCID), _ => typeof(Terraria.ID.ProjectileID) };
            ids.Add(new JsonObject { ["kind"] = kind, ["type"] = type,
                ["catalog_matches"] = AgentCatalog.Query(constants, type.ToString())["matches"].DeepClone(),
                ["wiki_style"] = style, ["label"] = label });
        }
        string plain = Plain(html);
        return new JsonObject { ["title"] = title, ["listed_ids"] = ids, ["listed_ids_truncated"] = total > 32,
            ["listed_ids_detail"] = "仅提取页面明确列出的Internal ID，catalog_matches是本机原版编号目录对应名称，不是运行时掉落验证；wiki_style不是frame_x/frame_y。来源条件仍需阅读正文，变体需现场查证。未匹配的格式不会猜测，空列表不证明没有编号。",
            ["text"] = plain[..Math.Min(plain.Length, 6000)], ["text_truncated"] = plain.Length > 6000 };
    }
}
