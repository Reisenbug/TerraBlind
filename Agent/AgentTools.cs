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

public static class AgentTools
{
    internal const int ApiRevision = 2;
    internal static readonly AgentTaskPlan Plan = new();
    internal static readonly Dictionary<string, ScriptProgress> Workflows = new();
    internal static readonly AgentWatch Watches = new();
    internal static readonly AgentRecovery Recovery = new();
    static (bool Attack, bool Dodge)? bossBefore;
    internal static bool HasBossControlOverride => bossBefore != null;
    internal static readonly ActionTasks Actions = new(Execute, ObserveAction,
        owns => NativeAgent.OnMain(() => { if (owns()) StopInputs(); return true; }, default));
    static readonly Dictionary<string, int> Tracking = new();
    static readonly HttpClient Wiki = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient HarvestHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    public static void NewTask() { Tracking.Clear(); Watches.Reset(); Watches.IgnorePriorActions(Actions.Snapshot()); Recovery.Reset(); }
    public static void Stop()
    {
        Actions.CancelAll();
        Watches.Reset();
        if (bossBefore is { } previous)
        {
            Combat.Enabled = previous.Attack; Dodge.Enabled = previous.Dodge; bossBefore = null;
        }
        StopInputs();
    }

    static void StopInputs()
    {
        Halt.All();
        StateSnapshotPlayer.StopRemoteTask();
        FightCoordinator.Stop(); ReplaySystem.Stop(); KeyPress.Clear();
        PlayerCombat.Stop();
        PvpDuel.Stop();
        StateSpacePlanner.PreserveTerrain = false;
        HttpServerSystem.PendingControl = null;
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

    public static Task<string> State(CancellationToken token) => NativeAgent.OnMain(() =>
    {
        Plan.Observe(AgentTaskEvidence.Measure);
        var state = JsonNode.Parse(StateSerializer.ToJson(HttpServerSystem.LatestSnapshot)) as JsonObject ?? new();
        state["agent"] = new JsonObject
        {
            ["status"] = NativeAgent.Status, ["auto_dodge"] = AgentPermissions.Dodge,
            ["auto_attack"] = AgentPermissions.Attack, ["player_id"] = Main.LocalPlayer.whoAmI,
            ["pvp"] = Main.LocalPlayer.hostile, ["team"] = Main.LocalPlayer.team,
            ["instruction_player_id"] = AgentNetwork.OwnerPlayer,
            ["api_revision"] = ApiRevision,
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
        state["task_plan"] = Plan.Snapshot();
        state["permanent_max_health"] = Main.LocalPlayer.statLifeMax;
        var workflows = new JsonObject();
        foreach (var workflow in Workflows) workflows[workflow.Key] = workflow.Value.Snapshot();
        state["script_workflows"] = workflows;
        state["action_tasks"] = Actions.Snapshot();
        state["watch"] = Watches.Snapshot();
        state["recovery"] = Recovery.Snapshot();
        state["codex_goal"] = NativeAgent.GoalState;
        state["boss_control"] = BossStatus();
        state["equipment_slots"] = Equipment.Snapshot();
        return state.ToJsonString();
    }, token);

    static JsonObject BossStatus() => new()
    {
        ["attack_enabled"] = Combat.Enabled, ["movement_enabled"] = Dodge.Enabled,
        ["attack_permitted"] = AgentPermissions.Attack, ["movement_permitted"] = AgentPermissions.Dodge,
        ["manual_control"] = Manual.On, ["pvp_active"] = PvpDuel.Active,
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
        return new JsonObject { ["hp"] = player.statLife, ["dead"] = player.dead,
            ["pos"] = new JsonObject { ["x"] = player.position.X, ["y"] = player.position.Y },
            ["inventory_slots"] = PvpInventory.Snapshot(), ["permanent_max_health"] = player.statLifeMax };
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
        result["task_plan"] = Plan.Snapshot();
        if (progress.Count > 0) result["进度"] = progress;
        return result.ToJsonString();
    }, token);

    internal static async Task<JsonObject> Wait(string path, string active, string stop, int seconds, CancellationToken token)
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
                await Task.Delay(150, token);
                if (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
                {
                    await StopAction();
                    return new JsonObject { ["outcome"] = "interrupted_by_death" };
                }
                var status = await Call(path, null, token);
                if (Rejected(status)) throw new InvalidOperationException($"{path} 状态查询被拒绝：{status}");
                if (active == "nav" ? Bool(status, "done") || Text(status, "status") == "failed" : !Bool(status, active))
                    return status;
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
        if (Recovery.Revision > 0 && await NativeAgent.OnMain(() => Recovery.Denied(name, args), token))
            return new JsonObject { ["error"] = "repeated_failure_blocked", ["detail"] = "相同参数已失败3次，必须修正方案。",
                ["recovery"] = await NativeAgent.OnMain(Recovery.Snapshot, token) }.ToJsonString();
        if (name is not ("say" or "ask" or "get_state" or "combat_observe" or "combat_stop")
            && await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
        {
            while (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
            return new JsonObject { ["outcome"] = "interrupted_by_death",
                ["detail"] = "Respawned; re-evaluate this action against fresh state instead of replaying its old arguments.",
                ["state_after_respawn"] = JsonNode.Parse(await State(token)) }.ToJsonString();
        }
        bool external = name is "nav_to" or "mine" or "mine_vein" or "chop_wood" or "use_item"
            or "place_at" or "act" or "interact" or "loot_all" or "craft" or "inventory_swap" or "combat_reposition"
            or "gather" or "collect_treasure" or "equip_item" or "npc_chat";
        bool action = external || name is "fight" or "fight_player";
        if ((action || name is "combat_start" or "combat_reposition" || name == "set_combat" && Bool(args, "boss_control")) && Actions.Conflicts)
            return new JsonObject { ["error"] = "action_busy", ["detail"] = "已有任务持有角色输入，请先action_control暂停或取消。",
                ["action_tasks"] = Actions.Snapshot() }.ToJsonString();
        long lease = external ? await NativeAgent.OnMain(() => PvpDuel.BeginAction(name), token) : 0;
        try
        {
            string output = await ExecuteCore(name, args, token);
            if (name is not ("get_state" or "say" or "ask" or "task_plan" or "task_step" or "action_query" or "run_script" or "watch"))
                output = await NativeAgent.OnMain(() =>
                {
                    Plan.Record(name, output, action);
                    Recovery.Record(name, args, output);
                    try
                    {
                        if (JsonNode.Parse(output) is JsonObject result && result.ContainsKey("task_plan"))
                        {
                            result["task_plan"] = Plan.Snapshot();
                            return result.ToJsonString();
                        }
                    }
                    catch (System.Text.Json.JsonException) { }
                    return output;
                }, token);
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
                Plan.Record(name, result.ToJsonString(), action);
                Recovery.Record(name, args, result.ToJsonString());
                result["task_plan"] = Plan.Snapshot();
                DiagLog.Write($"[agent] tool_exception {name}: {ex.Message}");
                NativeAgent.Progress("执行失败：" + name, "等待模型决策", false);
                return result.ToJsonString();
            }, token);
        }
        finally
        {
            if (lease != 0 && !token.IsCancellationRequested)
                await NativeAgent.OnMain(() => { PvpDuel.EndAction(lease); return true; }, token);
        }
    }

    static async Task<string> ExecuteScriptTool(string name, JsonObject args, CancellationToken root, CancellationToken token)
    {
        if (name is not ("nav_to" or "gather" or "fight" or "mine_vein" or "chop_wood"))
            return await Execute(name, args, name == "action_start" || name == "action_control" && Text(args, "command") == "resume" ? root : token);
        if (name == "fight" && !AgentPermissions.Attack) return await Execute(name, args, token);
        await NativeAgent.OnMain(() => { Watches.EnsureDefaults(); return true; }, token);
        string handle = Actions.Start(name, args, root, inline: true)["handle"].ToString();
        while (true)
        {
            var current = await Actions.Query(handle, 250, token);
            bool pending = await NativeAgent.OnMain(() => Watches.HasPending, token);
            if (current["status"]?.ToString() == "running" && pending)
            {
                current = await Actions.Control(handle, "pause", null, root);
                return new JsonObject { ["host_yield"] = true, ["handle"] = handle, ["action"] = current }.ToJsonString();
            }
            if (current["status"]?.ToString() == "running") continue;
            var result = current["result"] as JsonObject ?? new JsonObject { ["outcome"] = current["status"]?.DeepClone() };
            result["managed_handle"] = handle;
            if (current["status"]?.ToString() == "failed")
            {
                await NativeAgent.OnMain(() =>
                {
                    Watches.Observe(new JsonObject { ["hp"] = Main.LocalPlayer.statLife, ["dead"] = Main.LocalPlayer.dead,
                        ["actions"] = new JsonArray(current.DeepClone()) });
                    return true;
                }, token);
                result["host_yield"] = true;
            }
            return result.ToJsonString();
        }
    }

    static async Task<string> ExecuteCore(string name, JsonObject args, CancellationToken token)
    {
        await NativeAgent.OnMain(() => { DiagLog.Write($"[agent] tool {name} {args}"); return true; }, token);
        if (name is not ("say" or "ask"))
            await NativeAgent.OnMain(() => { NativeAgent.Progress("开始执行：" + name, "执行 " + name,
                !PvpDuel.Active && !name.StartsWith("combat_")); return true; }, token);
        if (name == "get_state") return await State(token);
        if (name == "say")
        {
            await NativeAgent.OnMain(() => { AgentChat.Say(AgentConversation.VisibleText(Text(args, "text"))); return true; }, token);
            return "ok";
        }
        if (name == "ask") return new JsonObject { ["player_answer"] = await NativeAgent.Ask(AgentConversation.VisibleText(Text(args, "question")), token) }.ToJsonString();
        if (name is "wiki_search" or "wiki_page") return await ToolFinished(name, await ReadWiki(name, args, token), token);
        var before = await NativeAgent.OnMain(Inventory, token);
        JsonObject result;
        switch (name)
        {
            case "run_script":
                var workflow = await NativeAgent.OnMain(() =>
                {
                    string id = Text(args, "workflow");
                    if (id == "") return null;
                    if (id.Length > 80) throw new ArgumentException("workflow需要1..80字符。");
                    if (!Workflows.TryGetValue(id, out var saved))
                    {
                        if (Workflows.Count >= 8) throw new InvalidOperationException("本世界最多8个workflow。");
                        Workflows[id] = saved = new ScriptProgress();
                    }
                    return saved;
                }, token);
                result = await AgentScript.Run(Text(args, "code"), Int(args, "seconds", 120), NativeAgent.ValidateTool,
                    (child, childArgs, childToken) => ExecuteScriptTool(child, childArgs, token, childToken),
                    async cleanupToken => await NativeAgent.OnMain(() => { Stop(); return true; }, cleanupToken), token, workflow,
                    checkToken => NativeAgent.OnMain(() => Watches.HasPending ? Watches.Pending() : null, checkToken));
                break;
            case "watch":
                return (await NativeAgent.OnMain(() => Watches.Configure(args), token)).ToJsonString();
            case "decision_point":
                return new JsonObject { ["status"] = "yield", ["event"] = await NativeAgent.OnMain(() => Watches.Request(Text(args, "reason")), token) }.ToJsonString();
            case "action_start":
                string actionTool = Text(args, "tool");
                var actionArgs = args["args"] as JsonObject ?? throw new ArgumentException("需要动作args对象。");
                NativeAgent.ValidateTool(actionTool, actionArgs);
                await NativeAgent.OnMain(() => { Watches.EnsureDefaults(); return true; }, token);
                result = Actions.Start(actionTool, actionArgs, token);
                return result.ToJsonString();
            case "action_query":
                result = await Actions.Query(Text(args, "handle"), Int(args, "wait_ms", 0), token);
                return result.ToJsonString();
            case "action_control":
                if (args["args"] is JsonObject resumeArgs)
                {
                    string resumeTool = Actions.Snapshot().FirstOrDefault(a => a["handle"]?.ToString() == Text(args, "handle"))?["tool"]?.ToString()
                        ?? throw new ArgumentException("未知动作句柄。");
                    NativeAgent.ValidateTool(resumeTool, resumeArgs);
                }
                result = await Actions.Control(Text(args, "handle"), Text(args, "command"), args["args"] as JsonObject, token);
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
                    result = savedProgress.Resolve(Text(args, "step"), Int(args, "expected_revision", -1), resolution == "complete",
                        args["value"], new JsonObject { ["inventory_slots"] = observed["inventory_slots"]?.DeepClone(),
                            ["permanent_max_health"] = observed["permanent_max_health"]?.DeepClone() });
                }
                else if (resolution is "" or "get") result = savedProgress.Snapshot();
                else throw new ArgumentException("action需要get/retry/complete。");
                break;
            case "task_plan_patch":
                result = await NativeAgent.OnMain(() => Plan.Patch(args, AgentTaskEvidence.Measure), token);
                break;
            case "gather":
                var targets = (args["targets"] as JsonArray ?? throw new ArgumentException("需要targets数组。"))
                    .Select(t => new GatherTarget(t["item_type"].GetValue<int>(), t["count"].GetValue<int>(),
                        t["tile_names"]?.AsArray().Select(n => n.GetValue<string>()).ToArray() ?? Array.Empty<string>(),
                        t["npc_types"]?.AsArray().Select(n => n.GetValue<int>()).ToArray() ?? Array.Empty<int>())).ToArray();
                result = await Gather.Run(targets, Int(args, "seconds", 120), new GameGather(targets), token);
                break;
            case "npc_find":
                result = await Call("/npc_find", args, token);
                break;
            case "collect_treasure":
                result = await Navigate(new JsonObject { ["x"] = Int(args, "x"), ["y"] = Int(args, "y"), ["reach"] = true }, token, 60);
                if (result["done"]?.GetValue<bool>() != true) break;
                result = await Call("/collect_treasure", args, token);
                if (!Rejected(result)) result = await Wait("/collect_treasure_status", "running", "/collect_treasure_stop", 35, token);
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
                result = await NativeAgent.OnMain(() => Plan.Create(args, AgentTaskEvidence.Measure), token);
                break;
            case "task_step":
                result = await NativeAgent.OnMain(() => Text(args, "action") switch
                {
                    "verify" => Plan.Advance(Text(args, "id"), AgentTaskEvidence.Measure),
                    "block" => Plan.Block(Text(args, "id"), Text(args, "reason")),
                    _ => throw new InvalidOperationException("action 必须是 verify 或 block。")
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
                var idleClock = Stopwatch.StartNew();
                while (idleClock.Elapsed.TotalSeconds < 2 && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                    || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                    await Task.Delay(50, token);
                result = await NativeAgent.OnMain(() => PvpInventory.Swap(args), token);
                break;
            case "equipment_list":
                return (await NativeAgent.OnMain(Equipment.Snapshot, token)).ToJsonString();
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
                    if (args["boss_control"] != null)
                    {
                        bossBefore ??= (Combat.Enabled, Dodge.Enabled);
                        Combat.Enabled = Dodge.Enabled = Bool(args, "boss_control");
                        if (!Combat.Enabled) Combat.Stop();
                    }
                    if (args["auto_dodge"] != null) AgentPermissions.Dodge = Bool(args, "auto_dodge");
                    if (args["auto_attack"] != null) AgentPermissions.Attack = Bool(args, "auto_attack");
                    if (!AgentPermissions.Attack) { Combat.Stop(); FightCoordinator.Stop(); PlayerCombat.Stop(); }
                    return new JsonObject { ["auto_dodge"] = AgentPermissions.Dodge, ["auto_attack"] = AgentPermissions.Attack,
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
            case "item_info": case "find_biome": case "find_descent": case "descent_route":
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
                result = await Call("/item_use", new JsonObject
                {
                    ["target_wx"] = Int(args, "x"), ["target_wy"] = Int(args, "y"), ["slot"] = Int(args, "slot", -1),
                    ["duration_ticks"] = Int(args, "duration_ticks", 30), ["strict"] = Bool(args, "strict")
                }, token);
                if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
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
                if (args["npc_types"] is JsonArray npcTypes)
                {
                    int[] types = npcTypes.Select(n => n.GetValue<int>()).ToArray();
                    if (types.Length == 0 || types.Any(t => t <= 0)) throw new ArgumentException("npc_types 不能为空且编号必须为正数。");
                    result = await NativeAgent.OnMain(() =>
                    { FightCoordinator.Start(Int(args, "max_dist", 40), types); return new JsonObject { ["ok"] = true }; }, token);
                }
                else result = await Call("/fight", args, token);
                if (!Rejected(result))
                {
                    result = await Wait("/fight_active", "active", "/fight", Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120), token);
                    await Call("/fight", new JsonObject { ["active"] = false }, token);
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

    internal static async Task<JsonObject> FindTiles(JsonObject args, CancellationToken token)
    {
        var catalog = await NativeAgent.OnMain(() => TileCatalog(Text(args, "name")), token);
        var exact = catalog["matches"].AsArray().Where(m => m["exact"].GetValue<bool>()).ToArray();
        int types = exact.Select(m => m["type"].GetValue<int>()).Distinct().Count();
        if (catalog["truncated"].GetValue<bool>() || types > 1)
            return new JsonObject { ["error"] = "ambiguous_tile_name", ["requested_name"] = Text(args, "name"),
                ["name_candidates"] = catalog["matches"].DeepClone() };
        if (types == 0)
        {
            var unresolved = await Call("/find_tiles", args, token);
            if (Text(unresolved, "error") is "unknown_tile_name" or "bad_params")
                unresolved["name_candidates"] = catalog["matches"].DeepClone();
            return unresolved;
        }
        var resolved = (JsonObject)args.DeepClone();
        resolved["name"] = exact[0]["internal"].GetValue<string>();
        var result = await Call("/find_tiles", resolved, token);
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

    internal static async Task<JsonObject> CollectTarget(int x, int y, CancellationToken token, bool nonSolidOnly = true)
    {
        var target = await NativeAgent.OnMain(() =>
        {
            if (!WorldGen.InWorld(x, y)) return null;
            var tile = Main.tile[x, y];
            if (!tile.HasUnactuatedTile || nonSolidOnly && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType]) return null;
            int Power(Item item) => Main.tileAxe[tile.TileType] ? item.axe : Main.tileHammer[tile.TileType] ? item.hammer : item.pick;
            int slot = Enumerable.Range(0, 50).OrderByDescending(i => Power(Main.LocalPlayer.inventory[i]))
                .FirstOrDefault(i => !Main.LocalPlayer.inventory[i].IsAir && Power(Main.LocalPlayer.inventory[i]) > 0, -1);
            bool canMine = false;
            if (slot >= 0)
            {
                int previous = Main.LocalPlayer.selectedItem;
                try { Main.LocalPlayer.selectedItem = slot; canMine = Reach.CanMine(Main.LocalPlayer, x, y); }
                finally { Main.LocalPlayer.selectedItem = previous; }
            }
            return new JsonObject { ["slot"] = slot, ["in_reach"] = canMine,
                ["solid"] = Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType] };
        }, token);
        if (target == null) return null;
        if (Int(target, "slot", -1) < 0) return new JsonObject { ["error"] = "no_collect_tool" };
        if (!Bool(target, "in_reach"))
        {
            var approach = await Navigate(new JsonObject { ["x"] = x, ["y"] = y, ["reach"] = Bool(target, "solid") }, token, 30);
            if (approach["done"]?.GetValue<bool>() != true)
                return new JsonObject { ["error"] = "cannot_reach_collect_target", ["navigation"] = approach };
        }
        var result = await Call("/item_use", new JsonObject { ["target_wx"] = x, ["target_wy"] = y,
            ["slot"] = target["slot"].DeepClone(), ["strict"] = true }, token);
        if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 15, token);
        if (Text(result, "outcome") == "removed") await Task.Delay(400, token);
        result["mode_adjustment"] = "target_use_collect_tool";
        return result;
    }

    internal static async Task<JsonObject> Navigate(JsonObject args, CancellationToken token, int seconds = 0)
    {
        var request = await NativeAgent.OnMain(() =>
        {
            int x = Int(args, "x"), y = Int(args, "y");
            bool reach = Bool(args, "reach"), exact = Bool(args, "exact");
            if (exact && WorldGen.InWorld(x, y))
            {
                var tile = Main.tile[x, y];
                if (tile.HasTile && (!tile.HasUnactuatedTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]))
                    reach = true;
            }
            return new JsonObject { ["gx"] = x, ["gy"] = y, ["exact"] = exact && !reach, ["reach"] = reach };
        }, token);
        var start = await Call("/nav_recede", request, token);
        if (Rejected(start)) return start;
        var result = await Wait("/nav_recede_done", "nav", "/nav_recede_stop", seconds, token);
        return await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            int distance = Math.Max(Math.Abs(ActExecutor.OriginCx(p) - Int(args, "x")), Math.Abs(ActExecutor.OriginCy(p) - Int(args, "y")));
            bool arrived = Bool(request, "reach") ? Reach.CanInteract(p, Int(args, "x"), Int(args, "y")) : distance <= 6;
            if (Bool(request, "reach") && !Bool(args, "reach"))
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
            if (!Rejected(result)) result = await Wait("/place_anywhere_status", "running", "/place_anywhere_stop", 120, token);
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
        string plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));
        return new JsonObject { ["title"] = Text(args, "title"), ["text"] = plain[..Math.Min(plain.Length, 6000)] }.ToJsonString();
    }
}
