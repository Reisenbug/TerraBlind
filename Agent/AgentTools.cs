using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerraBlind;

public static partial class AgentTools
{
    internal static bool AutonomyActive => NativeAgent.Processing || Actions.HasPrimaryControl || ControlSession.Active;
    internal static readonly System.Text.Json.JsonSerializerOptions ModelJson = new(System.Text.Json.JsonSerializerOptions.Default)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal const int ApiRevision = 2;
    internal static Func<JsonObject> BeginActObservation, EndActObservation;
    sealed class TaskPolicyInstance
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly CancellationTokenSource Lifetime = new();
        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Code;
        public AgentScript.Reactions Reactions;
        public JsonObject Watch, Report, LastReaction;
        public Task Worker;
        int retired;
        public void Retire()
        {
            if (Interlocked.Exchange(ref retired, 1) != 0) return;
            void Finish()
            {
                Lifetime.Cancel();
                _ = Worker.ContinueWith(_ => Lifetime.Dispose(), TaskScheduler.Default);
            }
            if (Reactions == null) Finish(); else Reactions.Retire(Finish);
        }
    }
    static volatile TaskPolicyInstance taskPolicy;
    sealed record CombatPreparation(string Key, CancellationTokenSource Lifetime, Task<JsonObject> Work, AgentScript.Reactions Owner);
    static readonly object combatPreparationGate = new();
    static CombatPreparation combatPreparation;
    static long combatPreparationVersion;
    internal static readonly ScriptDecisionChannels DecisionChannels = new();
    static int taskPolicyRevision, taskPolicyEpoch;

    internal static JsonObject TaskPolicyStatus(bool includeCode = false)
    {
        var policy = taskPolicy;
        var result = new JsonObject { ["revision"] = taskPolicyRevision, ["active"] = policy?.Reactions?.Active == true,
            ["events"] = new JsonArray((policy?.Reactions?.Kinds ?? Array.Empty<string>()).Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
            ["watch"] = policy?.Watch?.DeepClone(), ["last_reaction"] = policy?.LastReaction?.DeepClone() };
        if (includeCode) result["code"] = policy?.Code;
        return result;
    }

    static void RestoreTaskPolicyWatch()
    {
        var policy = taskPolicy;
        Watches.SetPolicyWatch(policy?.Watch);
        if (policy?.Reactions == null) return;
        foreach (string kind in policy.Reactions.Kinds) Watches.RegisterHandler(policy.Id, kind);
    }

    internal static void ClearTaskPolicy(bool resetRevision = false)
    {
        FeedbackControl.Clear();
        FrameProgram.Clear();
        ClearCombatPreparation();
        if (resetRevision) AgentScript.ResetIntents();
        AgentNavigationIntent.Reset();
        CombatForecast.Reset();
        DecisionChannels.Reset();
        var previous = taskPolicy; taskPolicy = null;
        taskPolicyEpoch++;
        taskPolicyRevision = resetRevision ? 0 : taskPolicyRevision + 1;
        if (previous != null) Watches.EndHandlers(previous.Id);
        Watches.SetPolicyWatch(null);
        previous?.Retire();
    }

    static void ClearCombatPreparation(AgentScript.Reactions owner = null)
    {
        lock (combatPreparationGate)
        {
            var previous = combatPreparation;
            if (previous == null || owner != null && !ReferenceEquals(previous.Owner, owner)) return;
            combatPreparation = null;
            previous.Lifetime.Cancel();
            _ = previous.Work.ContinueWith(_ => previous.Lifetime.Dispose(), TaskScheduler.Default);
        }
    }

    internal static JsonObject PrepareCombatReaction(JsonObject args, CancellationToken root)
    {
        root.ThrowIfCancellationRequested();
        string key = ActionTasks.NodeContext.Value + ":" + args["combat_candidate"]?["context"]
            + ":" + args["combat_candidate"]?["plan_id"];
        lock (combatPreparationGate)
        {
            var previous = combatPreparation;
            if (previous != null)
            {
                if (!previous.Work.IsCompleted)
                {
                    if (previous.Key != key) previous.Lifetime.Cancel();
                    return new JsonObject { ["status"] = "combat_preparing" };
                }
                combatPreparation = null;
                try
                {
                    if (previous.Key == key)
                    {
                        var result = previous.Work.GetAwaiter().GetResult();
                        DiagLog.Write($"[combat_preparation] status=ready key={key} error={result["error"]}");
                        return result;
                    }
                }
                catch (OperationCanceledException)
                { return new JsonObject { ["error"] = "combat_forecast_replaced" }; }
                finally { previous.Lifetime.Dispose(); }
            }
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(root, AgentScript.ReactionCancellation.Value);
            var work = CombatForecast.Refresh(args.DeepClone().AsObject(), () => CombatAssessment(25), lifetime.Token);
            combatPreparation = new CombatPreparation(key, lifetime, work, AgentScript.CurrentReactions.Value);
            DiagLog.Write($"[combat_preparation] status=pending key={key}");
            _ = work.ContinueWith(_ => Interlocked.Increment(ref combatPreparationVersion),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new JsonObject { ["status"] = "combat_preparing" };
        }
    }

    static async Task RunTaskPolicy(TaskPolicyInstance policy)
    {
        string savePath = Main.SavePath, world = Main.worldName + ":" + Main.worldID;
        try
        {
            policy.Report = await AgentScript.Run("(function(){\n" + policy.Code + "\n})();return tool('__task_policy_wait');", 120,
                (name, args) => { if (name != "__task_policy_wait") NativeAgent.ValidateTool(name, args); },
                async (name, args, token) =>
                {
                    using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(token, AgentScript.ReactionCancellation.Value);
                    token = dispatch.Token;
                    var scope = AgentScript.CurrentReactions.Value;
                    if (name == "__task_policy_wait")
                    {
                        if (scope.Kinds.Length == 0) throw new InvalidOperationException("任务策略需要通过on注册事件处理。");
                        policy.Reactions = scope; policy.Ready.TrySetResult(true);
                        await Task.Delay(Timeout.Infinite, token);
                        return "{}";
                    }
                    if (name == "watch")
                    {
                        var validation = new AgentWatch(); validation.Configure(args);
                        policy.Watch = (JsonObject)args.DeepClone();
                        if (ReferenceEquals(taskPolicy, policy))
                            await NativeAgent.OnMain(() => { RestoreTaskPolicyWatch(); return true; }, token);
                        return validation.Snapshot().ToJsonString();
                    }
                    if (!scope.Handling)
                    {
                        if (!AgentScript.PreparationOnly("tool(" + JsonValue.Create(name).ToJsonString() + ",{});"))
                            throw new InvalidOperationException("策略初始化阶段只注册on、设置watch和读取资料；动作写在事件处理函数中。");
                        return await Execute(name, args, token);
                    }
                    return await ExecuteScriptTool(name, args, token, token);
                }, _ => Task.CompletedTask, policy.Lifetime.Token, programs:
                    new ScriptLibrary(System.IO.Path.Combine(savePath, "TerraBlindAgent", "programs")).Callable(),
                onResult: report =>
                {
                    policy.Report = report;
                    if (report["skill_calls"] is JsonArray { Count: > 0 } calls)
                        new ScriptLibrary(System.IO.Path.Combine(savePath, "TerraBlindAgent", "programs")).RecordCalls(calls, world);
                });
        }
        catch (OperationCanceledException) when (policy.Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            policy.Report = new JsonObject { ["error"] = "task_policy_failed", ["detail"] = error.Message };
            if (!ReferenceEquals(taskPolicy, policy)) return;
            await NativeAgent.OnMain(() =>
            {
                if (ReferenceEquals(taskPolicy, policy))
                {
                    Watches.EndHandlers(policy.Id); Watches.SetPolicyWatch(null);
                    Watches.Request("task_policy_failed", policy.Report);
                }
                return true;
            }, policy.Lifetime.Token);
        }
    }

    static async Task<JsonObject> ManageTaskPolicy(JsonObject args, CancellationToken token)
    {
        string operation = Text(args, "operation");
        if (operation == "get") return await NativeAgent.OnMain(() => TaskPolicyStatus(true), token);
        int expected = Int(args, "expected_revision", -1);
        int epoch = await NativeAgent.OnMain(() =>
        {
            if (expected != taskPolicyRevision) throw new InvalidOperationException("任务策略版本已改变，请先读取task_policy get。");
            return taskPolicyEpoch;
        }, token);
        if (operation == "clear") return await NativeAgent.OnMain(() =>
        {
            if (expected != taskPolicyRevision || epoch != taskPolicyEpoch)
                throw new InvalidOperationException("任务或策略版本已改变，请重新读取。");
            ClearTaskPolicy(); return TaskPolicyStatus();
        }, token);
        if (operation is not ("set" or "patch")) throw new ArgumentException("operation需要get/set/patch/clear。");
        string code = Text(args, "code");
        if (operation == "patch")
        {
            string oldText = Text(args, "old_text"), newText = args["new_text"]?.GetValue<string>();
            if (oldText.Length == 0 || newText == null) throw new ArgumentException("patch需要非空old_text和new_text。");
            code = await NativeAgent.OnMain(() =>
            {
                if (expected != taskPolicyRevision || epoch != taskPolicyEpoch)
                    throw new InvalidOperationException("任务或策略版本已改变，请重新读取。");
                string previous = taskPolicy?.Code;
                int start = previous?.IndexOf(oldText, StringComparison.Ordinal) ?? -1;
                if (start < 0 || previous.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
                    throw new ArgumentException("task_policy_patch_not_unique：old_text须在当前源码中恰好匹配一次。");
                return previous[..start] + newText + previous[(start + oldText.Length)..];
            }, token);
        }
        if (string.IsNullOrWhiteSpace(code) || code.Length > 15000) throw new ArgumentException("策略code需要1..15000字符。");
        var unchanged = await NativeAgent.OnMain(() =>
        {
            if (expected != taskPolicyRevision || epoch != taskPolicyEpoch)
                throw new InvalidOperationException("任务或策略版本已改变，请重新读取。");
            var current = taskPolicy;
            if (current?.Code != code || current.Reactions?.Active != true) return null;
            var status = TaskPolicyStatus(); status["unchanged"] = true; return status;
        }, token);
        if (unchanged != null) return unchanged;
        var candidate = new TaskPolicyInstance { Code = code };
        candidate.Worker = RunTaskPolicy(candidate);
        try
        {
            await Task.WhenAny(candidate.Ready.Task, candidate.Worker).WaitAsync(token);
            if (!candidate.Ready.Task.IsCompletedSuccessfully)
            {
                await candidate.Worker;
                throw new InvalidOperationException("任务策略初始化失败：" + candidate.Report?.ToJsonString());
            }
            return await NativeAgent.OnMain(() =>
            {
                if (expected != taskPolicyRevision || epoch != taskPolicyEpoch)
                    throw new InvalidOperationException("任务或策略版本已改变，候选策略未启用。");
                var previous = taskPolicy;
                if (previous != null) Watches.EndHandlers(previous.Id);
                taskPolicy = candidate; taskPolicyRevision++;
                RestoreTaskPolicyWatch();
                previous?.Retire();
                return TaskPolicyStatus();
            }, token);
        }
        catch { candidate.Retire(); throw; }
    }
    internal static readonly AgentTaskPlan Plan = new();
    internal static readonly Dictionary<string, ScriptProgress> Workflows = new();
    static readonly HashSet<string> continuedActions = new();
    static readonly Dictionary<string, string> recoveredActions = new();
    static Task continuation = Task.CompletedTask;
    static int executingScripts;
    static int actionScripts;
    static readonly AsyncLocal<(string Id, ScriptProgress Progress)?> recoveryContext = new();
    static readonly AsyncLocal<bool> preparedDispatch = new();
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
    static string lastExecutionNotice;
    internal static string PreparationNotifiedContext { get; private set; }
    static ulong? gapStarted;
    static bool gapReported;

    internal static string PreparationContext(string attempt, string node)
    {
        if (attempt == null || string.IsNullOrEmpty(node)) return attempt;
        var preparing = Plan.PreparationNodes(node, AgentTaskEvidence.Measure);
        return "node:" + node + ":" + Plan.ExecutionRevision(node) + ":"
            + string.Join(",", preparing.Select(id => id + ":" + Plan.ExecutionRevision(id.ToString())));
    }

    internal static Task NotifyExecution(CodexAgent codex, CancellationToken token)
    {
        if (!codex.HasActiveTurn || token.IsCancellationRequested) return Task.CompletedTask;
        var tactical = Watches.LocalDelivery ? new JsonArray() : Watches.TacticalAdvisories(acknowledge: false);
        if (tactical.Count > 0)
            return SendTactical();
        async Task SendTactical()
        {
            bool accepted = await codex.Steer("实时决策模型请求调整方案。当前动作继续，原目标与进度保留。根据现场证据核对装备或地形，准备具体修改，再按需要交接。现场："
                + tactical.ToJsonString(ModelJson), token);
            if (accepted) await NativeAgent.OnMain(() => { Watches.AcknowledgeTacticalAdvisories(tactical); return true; }, token);
            DiagLog.Write("[agent_tactical_review] accepted=" + accepted + " events=" + tactical.Count);
        }
        if (!Plan.Incomplete) return Task.CompletedTask;
        var action = Actions.HasPrimaryControl ? Actions.Summary().FirstOrDefault(item => item["tool"]?.ToString() != "fight"
            && item["status"]?.ToString() == "running") : null;
        string kind = action != null ? "prepare_successor" : "execution_gap";
        if (action == null)
        {
            if (Watches.LocalDelivery || ExecutionContinuity?["reason"]?.ToString() is not ("no_executable_action" or "event_review_without_action"))
                return Task.CompletedTask;
            action = Actions.LatestFinishedPrimary();
        }
        if (action == null) return Task.CompletedTask;
        string node = action["task_node"]?.ToString();
        string preparationContext = PreparationContext(action["handle"] + ":" + action["attempt"], node);
        string key = kind == "prepare_successor" ? kind + ":" + preparationContext
            : kind + ":" + action["handle"] + ":" + action["attempt"] + ":" + Plan.ExecutionRevision(node);
        if (kind == "prepare_successor" && PreparationNotifiedContext == preparationContext) return Task.CompletedTask;
        if (lastExecutionNotice == key) return Task.CompletedTask;
        lastExecutionNotice = key;
        var notice = new JsonObject { ["kind"] = kind, ["handle"] = action["handle"]?.DeepClone(),
            ["tool"] = action["tool"]?.DeepClone(), ["status"] = action["status"]?.DeepClone(), ["task_node"] = node,
            ["plan_revision"] = Plan.Snapshot()["revision"]?.DeepClone() };
        if (NativeAgent.GoalState?["host_wall_elapsed_seconds"] is JsonNode noticeElapsed)
            notice["goal_elapsed_seconds"] = noticeElapsed.DeepClone();
        int evidenceBudget = 1600;
        JsonObject Brief(JsonObject source, int depth = 0)
        {
            var facts = new JsonObject();
            foreach (string field in new[] { "error", "outcome", "reason", "status", "done", "tool", "x", "y", "type", "slot", "item_type", "count", "hp", "dist", "detail",
                "args", "evidence", "failure", "navigation", "contact_escape", "predicted_tile", "state", "pos", "value", "blocked", "collection", "last_result", "result" })
            {
                if (source[field] is JsonValue value)
                {
                    JsonNode copy = value.TryGetValue<string>(out string text) && text.Length > 192
                        ? JsonValue.Create(text[..192] + "…") : value.DeepClone();
                    int cost = field.Length + copy.ToJsonString(ModelJson).Length + 4;
                    if (cost > evidenceBudget) continue;
                    evidenceBudget -= cost; facts[field] = copy;
                }
                else if (depth < 6 && source[field] is JsonObject nested && evidenceBudget > field.Length + 8)
                {
                    evidenceBudget -= field.Length + 5;
                    var child = Brief(nested, depth + 1);
                    if (child.Count > 0) facts[field] = child;
                }
            }
            return facts;
        }
        if (action["args"] is JsonObject args) notice["args"] = Brief(args);
        if (action["result"] is JsonObject result) notice["result"] = Brief(result);
        var preparationNodes = Plan.PreparationNodes(node, AgentTaskEvidence.Measure);
        notice["task_policy"] = TaskPolicyStatus();
        if (kind == "prepare_successor" && preparationNodes.Count > 0) notice["preparation_nodes"] = preparationNodes;
        string instruction = kind == "prepare_successor"
            ? "宿主执行变化：当前后台动作继续运行。task_policy未启用时先准备整趟反应并设置，使当前节点和后继节点同时受益。趁本轮准备已查证的后继script及适用的on_failure；preparation_nodes列出的节点目前只有准备代码，趁行进补齐行动。复用适用技能，结合runtime修订本轮可复用的逻辑。准备和只读查询保持当前动作运行。已有充分准备则沿用。"
            : "宿主执行变化：当前动作已经结束且没有后续动作运行。结合最新句柄与现场优先恢复有效行动；保留已完成进度，修改后续script或准备恢复，随后执行。";
        return Send();
        async Task Send()
        {
            bool accepted = await codex.Steer(instruction + notice.ToJsonString(ModelJson), token);
            if (accepted && kind == "prepare_successor") PreparationNotifiedContext = preparationContext;
            if (!accepted && lastExecutionNotice == key) lastExecutionNotice = null;
            DiagLog.Write($"[agent_execution_notice] kind={kind} handle={notice["handle"]} accepted={accepted}");
        }
    }

    internal static void ObserveContinuity()
    {
        var last = Actions.LatestPrimary();
        if (last == null) return;
        bool scriptOwnsInput = Actions.HasPrimaryControl && last["tool"]?.ToString() == "run_script";
        if (ControlSession.PrimaryActive || Actions.HasPrimaryControl && !scriptOwnsInput || !Plan.Incomplete || Main.LocalPlayer.dead)
        {
            if (!Main.LocalPlayer.dead && (gapStarted != null || LastContinuation?["status"]?.ToString() == "execution_gap"))
            {
                string resolved = Plan.Incomplete ? "execution_resumed" : "task_completed";
                Watches.ResolveExecutionGap(resolved);
                if (LastContinuation?["status"]?.ToString() == "execution_gap")
                {
                    LastContinuation["status"] = resolved;
                    LastContinuation["resolved_tick"] = Main.GameUpdateCount;
                }
            }
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
        bool scriptRunning = Volatile.Read(ref executingScripts) > 0 || Workflows.Values.Any(w => w.Running);
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
        if (token.IsCancellationRequested || !continuation.IsCompleted || Actions.HasPrimaryControl || ControlSession.PrimaryActive || Volatile.Read(ref actionScripts) > 0
            || !Plan.RequiresNodeExecution || Main.LocalPlayer.dead || ContactEscapeActive) return;
        if (pendingNode != null)
        {
            if (Watches.HasRecoveryBlocker) return;
            string ready = pendingNode;
            pendingNode = null;
            if (Plan.Snapshot()["ready_steps"].AsArray().Any(n => n?.ToString() == ready))
                StartPreparedNode(ready, LastContinuation?["after_handle"]?.ToString(), token);
            return;
        }
        var action = Actions.LatestFinishedPrimary();
        string handle = action?["handle"]?.ToString(), owner = action?["task_node"]?.ToString();
        string execution = handle + ":" + action?["attempt"];
        string attempt = execution + ":" + Plan.ExecutionRevision(owner);
        if (handle == null || string.IsNullOrEmpty(owner) || continuedActions.Contains(attempt)) return;
        var plan = Plan.Snapshot();
        var previous = plan["steps"].AsArray().FirstOrDefault(s => s["id"]?.ToString() == owner);
        if (previous == null) return;
        Workflows.TryGetValue(previous["workflow"]?.ToString() ?? "", out var workflow);
        if (workflow?.Running == true) return;
        if (previous["status"]?.ToString() != "completed" && !plan["ready_steps"].AsArray().Any(n => n?.ToString() == owner)) return;
        bool verified = previous["status"]?.ToString() == "completed"
            || Plan.Advance(owner, AgentTaskEvidence.Measure)["verified"]?.GetValue<bool>() == true;
        bool prepared = !verified && workflow?.HasPreparedContinuation(previous["script"]?.ToString()) == true;
        string recovery = previous["on_failure"]?.ToString();
        if (!verified && action["status"]?.ToString() == "failed" && !string.IsNullOrWhiteSpace(recovery)
            && (!recoveredActions.TryGetValue(execution, out string recovered) || recovered != recovery))
        {
            if (Watches.HasRecoveryBlocker || Main.LocalPlayer.suffocating) return;
            recoveredActions[execution] = recovery;
            continuedActions.Add(attempt);
            StartPreparedNode(owner, handle, token, recovery);
            return;
        }
        continuedActions.Add(attempt);
        if (!verified && !prepared && (workflow == null || workflow.Yielded || workflow.ReviewRequested
            || action["status"]?.ToString() != "completed")) return;
        if (prepared) Watches.AcknowledgeExecutionStart(owner, Main.LocalPlayer.dead, Main.LocalPlayer.suffocating, Actions.Summary());
        Watches.AcknowledgeCompletion(action, nodeVerified: verified);
        plan = Plan.Snapshot();
        string next = verified ? plan["current_step"]?.ToString() : owner;
        if (next == null || !plan["ready_steps"].AsArray().Any(n => n?.ToString() == next)) return;
        if (Watches.HasRecoveryBlocker)
        {
            pendingNode = next;
            LastContinuation = new JsonObject { ["after_handle"] = handle, ["task_node"] = next,
                ["status"] = "waiting_event_review", ["tick"] = Main.GameUpdateCount };
            return;
        }
        StartPreparedNode(next, handle, token);
    }

    static void StartPreparedNode(string next, string handle, CancellationToken token, string recovery = null)
    {
        LastContinuation = new JsonObject { ["after_handle"] = handle, ["task_node"] = next,
            ["status"] = "starting", ["tick"] = Main.GameUpdateCount };
        var report = LastContinuation;
        if (recovery != null) report["mode"] = "recovery";
        continuation = Task.Run(async () =>
        {
            preparedDispatch.Value = true;
            ScriptProgress recoveryProgress = null;
            try
            {
                var args = new JsonObject { ["task_node"] = next };
                if (recovery != null)
                {
                    var failure = await Actions.Query(handle, 0, token);
                    string recoveryWorkflow = await NativeAgent.OnMain(() =>
                    {
                        string id = Plan.Script(next)["workflow"] + "-recovery";
                        report["failed_attempt"] = failure["attempt"]?.DeepClone();
                        return id;
                    }, token);
                    recoveryProgress = new ScriptProgress();
                    recoveryContext.Value = (recoveryWorkflow, recoveryProgress);
                    args["code"] = "const failure=tool('action_query',{handle:" + JsonValue.Create(handle).ToJsonString() + "});\n" + recovery;
                    args["workflow"] = recoveryWorkflow;
                }
                string result = await Execute("run_script", args, token);
                await NativeAgent.OnMain(() =>
                {
                    var returned = JsonNode.Parse(result);
                    report["status"] = "returned"; report["ok"] = returned?["ok"]?.DeepClone();
                    report["error"] = returned?["error"]?.DeepClone();
                    if (recovery != null && returned?["value"] is JsonObject recoveryValue)
                        report["recovery_reason"] = recoveryValue["reason"]?.DeepClone();
                    if (returned?["ok"]?.ToString() == "false" || returned?["value"] is JsonObject value
                        && (value["status"]?.ToString() is "yield" or "needs_review" || value["wake"] != null || value["error"] != null))
                        ReportExecutionGap(report, "script_requires_review");
                    else if (!Actions.HasPrimaryControl)
                    {
                        var script = Plan.Script(next);
                        bool continuing = Workflows.TryGetValue(script["workflow"].ToString(), out var workflow)
                            && workflow.IntentContinuationReady(script["code"].ToString());
                        var plan = Plan.Snapshot();
                        if (plan["ready_steps"].AsArray().Any(n => n?.ToString() == next))
                            plan = Plan.Advance(next, AgentTaskEvidence.Measure);
                        if (plan["steps"].AsArray().Any(n => n["id"]?.ToString() == next && n["status"]?.ToString() == "completed"))
                            pendingNode = plan["current_step"]?.ToString();
                        else if (!continuing) ReportExecutionGap(report, "script_returned_without_action_or_completion");
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
            finally
            {
                if (recoveryProgress != null)
                    await NativeAgent.OnMain(() =>
                    {
                        report["recovery_execution"] = recoveryProgress.Summary()["last_execution"]?.DeepClone();
                        return true;
                    }, default);
                recoveryContext.Value = null;
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
    static bool? requestedBossControl;
    internal static bool HasBossControlOverride => bossBefore != null;
    internal static void ObserveBossControl(bool processing)
    {
        var player = Main.LocalPlayer;
        if (!processing || Main.gameMenu || Manual.On || player == null || !player.active || player.dead
            || requestedBossControl == false || PvpDuel.Active || PlayerCombat.Active
            || !AgentPermissions.CanAttack(delegated: true) && !AgentPermissions.CanDodge(delegated: true)) return;
        if (!Main.npc.Any(npc => npc is { active: true, boss: true, life: > 0 }
            && Math.Abs(npc.Center.X - player.Center.X) + Math.Abs(npc.Center.Y - player.Center.Y) <= 200 * 16)) return;
        if (Combat.Enabled && Dodge.Enabled) return;
        SetBossControl(true);
        DiagLog.Write("[agent_boss_handoff] controller=enabled source=nearby_boss");
    }

    static void SetBossControl(bool enabled)
    {
        bossBefore ??= (Combat.Enabled, Dodge.Enabled, Dodge.MovementReserveScale);
        Combat.Enabled = Dodge.Enabled = enabled;
        if (enabled) { TreasureGrab.CloseChest(); if (!NativeAgent.Processing) Main.playerInventory = false; }
        else Combat.Stop();
    }
    internal static readonly ActionTasks Actions = new(ExecuteWithTaskPolicy, ObserveAction,
        owns => NativeAgent.OnMain(() => { if (owns() && !ContactEscapeActive) StopInputs(); return true; }, default),
        (handle, owns) => NativeAgent.OnMain(() => { if (owns()) Control.EndAction(handle); return true; }, default),
        (tool, owns) => NativeAgent.OnMain(() =>
        {
            if (owns() && !ContactEscapeActive)
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
    public static void NewTask() { ClearTaskPolicy(true); AgentModelOutput.Clear(); Tracking.Clear(); Discoveries.Clear(); continuedActions.Clear(); recoveredActions.Clear(); pendingNode = lastExecutionNotice = PreparationNotifiedContext = null; LastContinuation = ExecutionContinuity = LastRouteLoot = null; routeChests.Clear(); routeLootEnabled = false; gapStarted = null; gapReported = false; Watches.Reset(); Watches.EnsureDefaults(); Watches.IgnorePriorActions(Actions.Summary()); Recovery.Reset(); Actions.BeginTask(); Control.BeginTask(); }
    public static void Stop()
    {
        ClearCombatPreparation();
        AgentScript.ResetIntents();
        AgentNavigationIntent.Reset();
        DecisionChannels.Reset();
        Actions.CancelAll();
        Watches.Reset();
        RestoreTaskPolicyWatch();
        if (bossBefore is { } previous)
        {
            Combat.Enabled = previous.Attack; Dodge.Enabled = previous.Dodge; Dodge.MovementReserveScale = previous.Reserve; bossBefore = null;
        }
        requestedBossControl = null;
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

    static int Int(JsonObject args, string key, int fallback = 0)
    {
        var node = args[key];
        return node is JsonValue value && value.TryGetValue<int>(out int number) ? number : node?.Deserialize<int>() ?? fallback;
    }
    static string Text(JsonObject args, string key) => args[key]?.GetValue<string>() ?? "";
    static bool Bool(JsonObject args, string key) => args[key]?.GetValue<bool>() ?? false;
    internal static bool Rejected(JsonObject result) => result != null && (result["error"] != null ||
        result["ok"]?.ToString() == "false" || result["accepted"]?.ToString() == "false");

    internal static Task<JsonObject> Call(string path, JsonObject args, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        token.ThrowIfCancellationRequested();
        if (path == "/act" && args?["combat_candidate"] != null)
        {
            args = CombatForecast.Prepare(args);
            if (args["error"] != null) return args;
        }
        var response = HttpServerSystem.Dispatch(path, args?.ToJsonString(ModelJson) ?? "{}");
        var result = JsonNode.Parse(response.Body) as JsonObject
            ?? throw new InvalidOperationException($"{path} 未返回 JSON 对象。");
        if (response.Status >= 400 && result["error"] == null) result["error"] = $"status {response.Status}";
        if (response.Status < 400 && path is "/swap" or "/quick_heal" or "/craft")
            WorldFramesRequired = Math.Max(WorldFramesRequired, 2);
        return result;
    }, token);

    internal static int WorldFramesRequired;

    public static Task<string> State(CancellationToken token, bool includeTiles = false, bool includeWatchHistory = false) =>
        State(token, includeTiles, includeWatchHistory, null);

    public static Task<string> State(CancellationToken token, bool includeTiles, bool includeWatchHistory,
        JsonArray fields) => NativeAgent.OnMain(() =>
    {
        string[] selected = fields?.Select(field => field.GetValue<string>()).Distinct().ToArray();
        if (selected is { Length: 0 } || selected?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("fields需要至少一个顶层状态字段名。");
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
        if (state["enemies"] is JsonArray enemies)
            foreach (var enemy in enemies.OfType<JsonObject>())
            {
                int who = Int(enemy, "who", -1);
                if (who < 0 || who >= Main.maxNPCs) continue;
                var npc = Main.npc[who];
                if (!npc.active || npc.type != Int(enemy, "type", -1)) { enemy["observation_stale"] = true; continue; }
                enemy["attackable"] = npc.CanBeChasedBy(healthPlayer);
                enemy["contact_damage"] = npc.damage;
                enemy["defense"] = npc.defense;
                enemy["dont_take_damage"] = npc.dontTakeDamage;
                enemy["immortal"] = npc.immortal;
            }
        state["movement_capabilities"] = new JsonObject
        {
            ["no_fall_damage"] = healthPlayer.noFallDmg, ["fall_start_tile_y"] = healthPlayer.fallStart,
            ["extra_fall_tiles"] = healthPlayer.extraFall, ["extra_jump_usable"] = healthPlayer.AnyExtraJumpUsable(),
            ["wing_time_frames"] = healthPlayer.wingTime
        };
        if (selected == null || selected.Contains("movement_capabilities"))
            state["movement_capabilities"]["fall_forecast"] = NavigationSurvival.AssessFall(healthPlayer);
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
            observedPlayer["honey_slowed"] = healthPlayer.honeyWet && !healthPlayer.ignoreWater;
            observedPlayer["potion_delay_ticks"] = Main.LocalPlayer.potionDelay;
            observedPlayer["biome_flags"] = new JsonObject { ["jungle"] = healthPlayer.ZoneJungle,
                ["crimson"] = healthPlayer.ZoneCrimson, ["corruption"] = healthPlayer.ZoneCorrupt,
                ["hallow"] = healthPlayer.ZoneHallow, ["dungeon"] = healthPlayer.ZoneDungeon,
                ["desert"] = healthPlayer.ZoneDesert, ["snow"] = healthPlayer.ZoneSnow, ["ocean"] = healthPlayer.ZoneBeach,
                ["underground_desert"] = healthPlayer.ZoneUndergroundDesert };
            int wallType = Main.tile[(int)healthPlayer.Center.X / 16, (int)healthPlayer.Center.Y / 16].WallType;
            observedPlayer["background_wall"] = new JsonObject { ["type"] = wallType,
                ["spider"] = wallType == Terraria.ID.WallID.SpiderUnsafe };
            var ground = new JsonArray();
            int groundY = (int)(healthPlayer.position.Y + healthPlayer.height + 1) / 16;
            for (int x = (int)healthPlayer.position.X / 16; x <= (int)(healthPlayer.position.X + healthPlayer.width - 1) / 16; x++)
                if (WorldGen.InWorld(x, groundY)) ground.Add(new JsonObject { ["x"] = x, ["y"] = groundY,
                    ["type"] = Main.tile[x, groundY].HasTile ? (int)Main.tile[x, groundY].TileType : -1 });
            observedPlayer["ground_tiles"] = ground;
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
        if (selected?.Contains("inventory_counts") == true)
            state["inventory_counts"] = new JsonObject(healthPlayer.inventory.Take(50).Where(item => !item.IsAir && item.stack > 0)
                .GroupBy(item => item.type).Select(group => new KeyValuePair<string, JsonNode>(group.Key.ToString(), JsonValue.Create(group.Sum(item => item.stack)))));
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
        state["local_intents"] = AgentScript.IntentStatus;
        state["execution_continuity"] = ExecutionContinuity?.DeepClone();
        state["last_route_loot"] = LastRouteLoot?.DeepClone();
        state["control_overrides"] = Control.Snapshot();
        state["control_execution"] = ControlSession.Snapshot();
        state["idle_defense_active"] = FightCoordinator.IsIdleDefending;
        state["watch"] = includeWatchHistory ? Watches.Snapshot() : Watches.Summary();
        state["task_policy"] = TaskPolicyStatus();
        state["recovery"] = Recovery.Snapshot();
        state["codex_goal"] = NativeAgent.GoalState;
        var verification = Plan.VerifyFinal(AgentTaskEvidence.Measure);
        verification.Remove("plan");
        state["goal_verification"] = verification;
        state["boss_control"] = BossStatus();
        state["equipment_slots"] = Equipment.Snapshot();
        if (selected != null)
        {
            string[] unknown = selected.Where(field => !state.ContainsKey(field)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("未知状态字段：" + string.Join(",", unknown));
            state = new JsonObject(selected.Select(field => new KeyValuePair<string, JsonNode>(field, state[field]?.DeepClone())));
            state["snapshot_tick"] = Main.GameUpdateCount;
        }
        return state.ToJsonString(ModelJson);
    }, token);

    static JsonObject BossStatus(bool fresh = false)
    {
        var movement = fresh ? new JevariaCombat.GameSensor { MovementReserveScale = Dodge.MovementReserveScale }
            .Capture(Main.LocalPlayer, 0, JevariaCombat.DodgeIntent.Idle, 0) : Dodge.Snapshot;
        return new JsonObject
        {
            ["attack_enabled"] = Combat.Enabled, ["movement_enabled"] = Dodge.Enabled,
            ["attack_permitted"] = AgentPermissions.Attack, ["movement_permitted"] = AgentPermissions.Dodge,
            ["manual_control"] = Manual.On, ["pvp_active"] = PvpDuel.Active,
            ["inventory_open"] = Main.playerInventory,
            ["input_gate"] = Manual.On ? "manual" : Main.gameMenu ? "game_menu" : Main.playerInventory && !NativeAgent.Processing ? "inventory_open" : null,
            ["movement_reserve_scale"] = Dodge.MovementReserveScale,
            ["honey_slowed"] = Main.LocalPlayer.honeyWet && !Main.LocalPlayer.ignoreWater,
            ["movement_snapshot"] = movement != null ? new JsonObject
            {
                ["source"] = fresh ? "live_preview" : "jev_request",
                ["captured_tick"] = movement.Tick, ["reserve_scale"] = movement.MovementReserveScale,
                ["velocity_pixels_per_tick"] = new JsonObject { ["x"] = movement.Player.Velocity.X, ["y"] = movement.Player.Velocity.Y },
                ["extra_jump_available"] = movement.CanDoubleJump, ["wing_time_available"] = movement.CanFly,
                ["hook_equipped"] = movement.HasHook, ["vertical_mount_equipped"] = movement.HasMount,
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
    }

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
        if (NativeAgent.GoalState?["host_wall_elapsed_seconds"] is JsonNode elapsed)
            result["goal_elapsed_seconds"] = elapsed.DeepClone();
        if (progress.Count > 0) result["进度"] = progress;
        return result.ToJsonString();
    }, token);

    internal static async Task<JsonObject> Wait(string path, string active, string stop, int seconds, CancellationToken token,
        Func<double, Task<JsonObject>> navigationProgress = null, JsonObject reactionContext = null,
        Func<JsonObject, Task> onCompleted = null, Func<CancellationToken, Task> restart = null)
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
            var frozenAtStart = NativeAgent.FrozenTime();
            double Elapsed() => (timer.Elapsed - (NativeAgent.FrozenTime() - frozenAtStart)).TotalSeconds;
            while (seconds <= 0 || Elapsed() < seconds)
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
                {
                    if (onCompleted != null) await onCompleted(status);
                    if (reactionContext != null) await ReactAtBoundary(token, context: reactionContext);
                    return status;
                }
                if (reactionContext != null && stop == "/fight")
                    await ReactAtBoundary(token, context: reactionContext);
                else if (reactionContext != null)
                {
                    JsonObject completed = null;
                    await ReactAtBoundary(token, pause: async ct =>
                    {
                        if (restart != null)
                        {
                            await StopAction();
                            await NativeAgent.OnMain(() => { Halt.All(); return true; }, ct);
                            return;
                        }
                        if (completed != null) return;
                        int remaining = seconds <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(seconds - Elapsed()));
                        completed = await Wait(path, active, stop, remaining, ct, navigationProgress, onCompleted: onCompleted);
                    }, resume: restart, context: reactionContext);
                    if (completed != null) return completed;
                }
                if (navigationProgress != null)
                {
                    var stalled = await navigationProgress(Elapsed());
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
            if (onCompleted != null) await onCompleted(final);
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

    static long invocationSequence;
    internal static int QueriesInFlight;
    static readonly AsyncLocal<long> scriptInvocation = new();
    static bool ObservationTool(string name) => name is "get_state" or "game_api" or "game_catalog" or "find_tiles" or "inspect_terrain"
        or "find_descent" or "find_biome"
        or "terrain_map" or "opportunity_assess" or "nearby_drops" or "combat_assess" or "jev_judge" or "item_info" or "wiki_search" or "wiki_page"
        or "recipe" or "script_program" or "script_report" or "gather_observe" or "equipment_list" or "inventory_review" or "boss_status" or "action_query";
    public static async Task<string> Execute(string name, JsonObject args, CancellationToken token)
    {
        long id = Interlocked.Increment(ref invocationSequence);
        long started = Stopwatch.GetTimestamp();
        bool query = ObservationTool(name);
        if (query) Interlocked.Increment(ref QueriesInFlight);
        DiagLog.Write($"[agent_tool_begin] id={id} name={name} timestamp={started} node={ActionTasks.NodeContext.Value}");
        try { return await ExecuteObserved(name, args, token); }
        finally
        {
            if (query) Interlocked.Decrement(ref QueriesInFlight);
            DiagLog.Write($"[agent_tool_end] id={id} name={name} elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
        }
    }

    static async Task<string> ExecuteObserved(string name, JsonObject args, CancellationToken token)
    {
        if (name == "script_report") return AgentModelOutput.Read(Text(args, "id")).ToJsonString(ModelJson);
        string taskNode = ActionTasks.NodeContext.Value;
        if (name == "action_control" && Text(args, "command") == "resume")
        {
            var completed = Actions.Summary().FirstOrDefault(a => a["handle"]?.ToString() == Text(args, "handle")
                && a["status"]?.ToString() == "completed");
            if (completed != null && (args["args"] == null || JsonNode.DeepEquals(args["args"], completed["args"])))
            {
                var observed = await Actions.Query(Text(args, "handle"), 0, token);
                observed["no_action"] = "already_completed";
                await NativeAgent.OnMain(() =>
                {
                    Watches.AcknowledgeCompletion(observed);
                    Recovery.Record(name, args, observed.ToJsonString());
                    return true;
                }, token);
                return observed.ToJsonString();
            }
        }
        if (!ObservationTool(name) && Recovery.Revision > 0
            && await NativeAgent.OnMain(() => Recovery.Denied(name, args), token))
            return new JsonObject { ["error"] = "repeated_failure_blocked", ["detail"] = "相同参数已失败3次，必须修正方案。",
                ["recovery"] = await NativeAgent.OnMain(Recovery.Snapshot, token) }.ToJsonString();
        bool needsLivingPlayer = AgentControlSession.ActionTool(name)
            || name is "combat_start" or "duel_player" or "set_combat"
            || name == "action_control" && Text(args, "command") == "resume";
        if (needsLivingPlayer
            && await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
        {
            while (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
            return new JsonObject { ["outcome"] = "interrupted_by_death",
                ["detail"] = "Respawned; re-evaluate this action against fresh state instead of replaying its old arguments.",
                ["state_after_respawn"] = JsonNode.Parse(await State(token)) }.ToJsonString();
        }
        bool external = name is "nav_to" or "mine" or "mine_vein" or "chop_wood" or "use_item"
            or "place_at" or "act" or "interact" or "loot_all" or "craft" or "inventory_swap" or "combat_reposition"
            or "gather" or "collect_treasure" or "build_house" or "hell_run" or "equip_item" or "open_item" or "inventory_drop" or "inventory_trash" or "npc_chat";
        bool action = external || name is "fight" or "fight_player";
        bool startsPrimary = name == "action_start" && Text(args, "tool") != "fight";
        if ((external || startsPrimary) && string.IsNullOrEmpty(taskNode)
            && await NativeAgent.OnMain(() => Plan.RequiresNodeExecution, token))
            return new JsonObject { ["error"] = "dag_node_required",
                ["detail"] = "动作需要所属DAG节点。执行原脚本用run_script({task_node:id})；临时恢复用run_script({task_node:id,workflow:'独立恢复名称',code:'恢复代码'})，原节点脚本保留。修改原脚本用task_plan_patch nodes。",
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
        long lease = 0;
        long controlTicket = 0;
        long script = scriptInvocation.Value;
        try
        {
            if (name == "act" && args["combat_candidate"] != null)
            {
                args = await CombatForecast.Refresh(args, () => CombatAssessment(25), token);
                if (Rejected(args)) return args.ToJsonString();
            }
            if (action)
            {
                var denied = await NativeAgent.OnMain(() =>
                {
                    if (external) lease = PvpDuel.BeginAction(name);
                    FightCoordinator.StopIdleDefense();
                    return ControlSession.Begin(name, args["control_claim"]?.ToString(), out controlTicket,
                        AgentControlSession.RequiredChannels(name, args), script);
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
        var reactions = AgentScript.CurrentReactions.Value;
        if (reactions?.Handling == true && name == "use_item")
        {
            var deferred = await NativeAgent.OnMain(() =>
            {
                int slot = Int(args, "slot", -1);
                if (slot < 0 || slot >= Main.LocalPlayer.inventory.Length) return null;
                var item = Main.LocalPlayer.inventory[slot];
                return item.stack > 0 && item.consumable && ControlSession.Gathering(item.type)
                    ? new JsonObject { ["outcome"] = "deferred_consumption", ["item_type"] = item.type,
                        ["detail"] = "当前采集先验收实际库存；采集结束后可使用目标物品。" } : null;
            }, token);
            if (deferred != null) return deferred.ToJsonString();
        }
        if (reactions?.Handling == true && name is "action_control" or "action_start" or "action_queue" or "control" or "task_policy")
            return new JsonObject { ["error"] = "reaction_action_lifecycle",
                ["detail"] = "事件中直接调用nav_to/gather/use_item等，宿主交接并恢复原动作。任务句柄控制交回Agent执行。" }.ToJsonString();
        if (name == "act" && args["combat_candidate"] != null)
        {
            var requested = args;
            args = reactions?.Handling == true && reactions.Kind == "tick"
                ? PrepareCombatReaction(args, ReferenceEquals(reactions, taskPolicy?.Reactions) ? taskPolicy.Lifetime.Token : root)
                : await CombatForecast.Refresh(args, () => CombatAssessment(25), token);
            if (Text(args, "status") == "combat_preparing") return args.ToJsonString();
            if (Text(args, "error") == "combat_candidate_no_handoff_prefix"
                && reactions?.Handling == true && reactions.BeforeAction != null)
            {
                await reactions.BeforeAction(name, requested, token);
                DiagLog.Write("[combat_handoff_pause] " + requested["combat_candidate"].ToJsonString());
                args = await CombatForecast.Refresh(requested, () => CombatAssessment(25), token);
            }
            if (Rejected(args)) return args.ToJsonString();
        }
        if (reactions?.Handling == true && AgentControlSession.ActionTool(name) && reactions.BeforeAction != null)
            await reactions.BeforeAction(name, args, token);
        if (reactions?.Handling == true) return await Execute(name, args, token);
        if (!ActionTasks.Supports(name))
            return await ExecuteWithTaskPolicy(name, args, name is "action_start" or "action_queue" || name == "action_control" && Text(args, "command") == "resume" ? root : token);
        if (name != "fight" && string.IsNullOrEmpty(ActionTasks.NodeContext.Value)
            && await NativeAgent.OnMain(() => Plan.RequiresNodeExecution, token))
            return await Execute(name, args, token);
        if (name == "fight" && !AgentPermissions.Attack) return await Execute(name, args, token);
        if (Actions.RunningScript) return await ExecuteWithTaskPolicy(name, args, token);
        await NativeAgent.OnMain(() => { Watches.EnsureDefaults(); return true; }, token);
        string handle = Actions.Start(name, args, root, inline: true)["handle"].ToString();
        while (true)
        {
            var current = await Actions.Query(handle, 250, token);
            bool pending = await NativeAgent.OnMain(() => scriptEvents ? Watches.HasInterrupting : Watches.HasModelInterrupting, token);
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
                if (scriptEvents) result["events"] = await NativeAgent.OnMain(Watches.Take, token);
            }
            await NativeAgent.OnMain(() => { Watches.AcknowledgeResult(current); return true; }, token);
            return result.ToJsonString();
        }
    }

    static int pendingPrimaryActions;
    static async Task<string> ExecuteWithTaskPolicy(string name, JsonObject args, CancellationToken token)
    {
        bool primary = AgentControlSession.ActionTool(name) && AgentScript.CurrentReactions.Value?.Handling != true;
        if (primary) Interlocked.Increment(ref pendingPrimaryActions);
        try
        {
            if (primary) await NativeAgent.IdleReaction.WaitAsync(token);
            if (primary && name != "gather")
                await ReactAtBoundary(token, context: new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() });
            string output = await Execute(name, args, token);
            var local = AgentScript.CurrentReactions.Value;
            var policy = taskPolicy?.Reactions;
            if (!AgentControlSession.ActionTool(name) || local?.Handling == true || policy?.Has("action_failed") != true
                || local?.Active == true && local.Has("action_failed")) return output;
            if (JsonNode.Parse(output) is not JsonObject failed || !ScriptProgress.Failed(failed)
                || failed["task_policy_recovery"] != null) return output;
            var decision = await ServiceReaction(policy, () => Task.FromResult(new JsonObject { ["kind"] = "action_failed",
                ["tool"] = name, ["args"] = args.DeepClone(), ["result"] = failed.DeepClone(), ["task_node"] = ActionTasks.NodeContext.Value }), token);
            if (decision?["retry"]?.GetValue<bool>() == true)
            {
                output = await Execute(name, args, token);
                if (JsonNode.Parse(output) is JsonObject retried)
                { retried["task_policy_recovery"] = true; output = retried.ToJsonString(); }
            }
            return output;
        }
        finally { if (primary) Interlocked.Decrement(ref pendingPrimaryActions); }
    }

    internal static async Task ReactWhileIdle(CancellationToken token)
    {
        try
        {
            string node = await NativeAgent.OnMain(() =>
            {
                if (Volatile.Read(ref pendingPrimaryActions) > 0
                    || taskPolicy?.Reactions?.Active != true || Main.LocalPlayer.dead || Manual.On || ContactEscapeActive
                    || ControlSession.PrimaryActive || Actions.HasPrimaryControl) return null;
                return Plan.RequiresNodeExecution ? Plan.Snapshot()["current_step"]?.ToString() : "";
            }, token);
            if (node == null) return;
            string previous = ActionTasks.NodeContext.Value;
            ActionTasks.NodeContext.Value = node;
            try { await ReactAtBoundary(token, context: new JsonObject { ["tool"] = "idle" }); }
            finally { ActionTasks.NodeContext.Value = previous; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagLog.Write("[task_policy_idle_failed] " + error.Message);
            await NativeAgent.OnMain(() => { Watches.Request("task_policy_idle_failed",
                new JsonObject { ["detail"] = error.Message }); return true; }, default);
        }
    }

    internal static async Task ReactAtBoundary(CancellationToken token,
        Func<CancellationToken, Task> pause = null, Func<CancellationToken, Task> resume = null, JsonObject context = null)
    {
        var local = AgentScript.CurrentReactions.Value;
        if (local?.Handling == true) return;
        if (local?.Active != true) local = null;
        var global = taskPolicy?.Reactions;
        foreach (var reactions in new[] { local, global }.Where(r => r != null).Distinct())
        {
            if (ReferenceEquals(reactions, global) && context?["tool"]?.ToString() == "fight"
                && await NativeAgent.OnMain(() => ControlSession.Tool is not (null or "fight")
                    && !(ControlSession.Tool == "gather" && context["owner_tool"]?.ToString() == "gather"), token)) continue;
            string[] kinds = reactions.Kinds.Except(ReferenceEquals(reactions, global) ? local?.PolicyOverrides ?? Array.Empty<string>() : Array.Empty<string>()).ToArray();
            await ServiceReaction(reactions, async () =>
            {
                var notices = await NativeAgent.OnMain(() => Watches.TakeForScript(kinds), token);
                JsonObject notice = notices.FirstOrDefault()?.AsObject();
                if (notice == null && kinds.Contains("tick") && reactions.TickDue(DecisionChannels.CompletionVersion
                    + CombatForecast.CompletionVersion + Interlocked.Read(ref combatPreparationVersion))) notice = new JsonObject { ["kind"] = "tick" };
                if (notice != null)
                {
                    notice["execution"] = context?.DeepClone();
                    notice["task_node"] = ActionTasks.NodeContext.Value;
                }
                return notice;
            }, token, pause, resume);
            if (local?.NavigationConditionMet == true) break;
        }
    }

    static async Task<JsonObject> ServiceReaction(AgentScript.Reactions reactions, Func<Task<JsonObject>> read,
        CancellationToken token, Func<CancellationToken, Task> pause = null, Func<CancellationToken, Task> resume = null)
    {
        if (!reactions.TryEnter()) return null;
        bool paused = false;
        bool construction = false;
        Action restore = null;
        var previous = reactions.BeforeAction;
        reactions.BeforeAction = async (name, args, ct) =>
        {
            if (name == "fight") return;
            bool changesRoute = name is "nav_to" or "gather" or "mine" or "mine_vein" or "chop_wood" or "use_item"
                or "place_at" or "act" or "collect_treasure" or "build_house" or "hell_run" or "combat_reposition";
            if (changesRoute && !paused && pause != null) { await pause(ct); paused = true; }
            if ((AgentControlSession.RequiredChannels(name, args) & ControlChannels.ItemUse) != 0 || construction && paused)
                while (await NativeAgent.OnMain(() => !Main.LocalPlayer.dead
                    && (Main.LocalPlayer.itemAnimation > 0 || construction && paused && Main.LocalPlayer.itemTime > 0), ct)) await NativeAgent.NextUpdate(ct);
            if (restore == null) restore = await NativeAgent.OnMain(ControlSession.SuspendPrimary, ct);
        };
        try
        {
            var notice = await read();
            if (notice == null) return null;
            construction = notice["execution"]?["tool"]?.ToString() == "place_at";
            if (NativeAgent.GoalState is JsonObject goal)
                notice["goal"] = new JsonObject { ["status"] = goal["native"]?["status"]?.DeepClone(),
                    ["objective"] = goal["native"]?["objective"]?.DeepClone(),
                    ["elapsed_seconds"] = goal["host_wall_elapsed_seconds"]?.DeepClone() };
            long started = Stopwatch.GetTimestamp();
            var decision = await reactions.Dispatch(notice, token);
            JsonObject review = null;
            if (decision?["agent_review"] is JsonObject request)
                review = await NativeAgent.OnMain(() => Watches.RequestTacticalReview(Text(request, "key"),
                    Text(request, "reason"), request["evidence"] as JsonObject), token);
            if (notice["kind"]?.ToString() != "action_failed" && decision?["handled"]?.GetValue<bool>() != true)
            {
                reactions.Remove(notice["kind"].ToString());
                await NativeAgent.OnMain(() =>
                {
                    if (notice["kind"]?.ToString() == "tick") Watches.Request("script_tick_requires_review", decision);
                    else Watches.ReturnToAgent(notice);
                    return true;
                }, token);
            }
            var record = new JsonObject { ["kind"] = notice["kind"]?.DeepClone(),
                ["execution_tool"] = notice["execution"]?["tool"]?.DeepClone(),
                ["execution_phase"] = notice["execution"]?["phase"]?.DeepClone(),
                ["elapsed_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds, ["took_control"] = paused,
                ["handled"] = decision?["handled"]?.DeepClone(), ["retry"] = decision?["retry"]?.DeepClone(),
                ["outcome"] = decision?["outcome"]?.DeepClone(), ["reason"] = decision?["reason"]?.DeepClone(),
                ["task_node"] = notice["task_node"]?.DeepClone() };
            if (review != null) record["agent_review"] = review;
            if (ReferenceEquals(taskPolicy?.Reactions, reactions)) taskPolicy.LastReaction = record;
            DiagLog.Write("[script-reaction] " + record);
            return decision;
        }
        finally
        {
            reactions.BeforeAction = previous;
            try
            {
                if (restore != null) await NativeAgent.OnMain(() => { restore(); return true; }, default);
                if (paused && resume != null && !token.IsCancellationRequested
                    && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token)) await resume(token);
            }
            finally { reactions.Exit(); }
        }
    }

    static async Task<string> ExecuteCore(string name, JsonObject args, CancellationToken token)
    {
        bool localIntent = AgentScript.CurrentIntent.Value != null;
        var before = await NativeAgent.OnMain(() =>
        {
            DiagLog.Write($"[agent] tool {name} {args}");
            if (!ObservationTool(name) && name is not ("say" or "ask" or "nav_intent"))
                NativeAgent.Progress("开始执行：" + name, "执行 " + name,
                    !localIntent && !PvpDuel.Active && !name.StartsWith("combat_"));
            return name is "get_state" or "say" or "ask" or "wiki_search" or "wiki_page" ? null : Inventory();
        }, token);
        if (name == "get_state") return await State(token, Bool(args, "include_tiles"), Bool(args, "include_watch_history"), args["fields"] as JsonArray);
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
            case "task_policy":
                return (await ManageTaskPolicy(args, token)).ToJsonString();
            case "nearby_drops":
                return new JsonObject { ["drops"] = await NativeAgent.OnMain(() => NearbyDrops.Read(Int(args, "radius", 25)), token) }.ToJsonString();
            case "combat_assess":
                return (await NativeAgent.OnMain(() =>
                {
                    var scene = CombatAssessment(Int(args, "radius", 25));
                    if (args["terrain_radius"] != null) scene["terrain"] = TerrainInspection.Read(Int(args, "terrain_radius"));
                    if (Bool(args, "forecast")) scene["forecast"] = CombatForecast.Poll(args, scene);
                    return scene;
                }, token)).ToJsonString();
            case "nav_intent":
                return (await NativeAgent.OnMain(() => AgentNavigationIntent.Update(args), token)).ToJsonString();
            case "jev_judge":
                string decisionKey = await NativeAgent.OnMain(() => Config.I.TypeSafeKey?.Trim(), token);
                if (args["channel"] != null)
                    return DecisionChannels.Poll(args, (request, ct) => ScriptDecision.Judge(HarvestHttp, decisionKey, request, ct)).ToJsonString();
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
                if (Text(args, "task_node") == "" && args["code"] != null
                    && (Text(args, "workflow") != "" || AgentScript.ContainsInput(Text(args, "code"))))
                {
                    string owner = await NativeAgent.OnMain(() =>
                    {
                        if (!Plan.RequiresNodeExecution) return null;
                        var ready = Plan.Snapshot()["ready_steps"].AsArray();
                        return ready.Count == 1 ? ready[0].ToString() : null;
                    }, token);
                    if (owner != null)
                    {
                        args = (JsonObject)args.DeepClone();
                        args["task_node"] = owner;
                    }
                }
                if (Text(args, "task_node") != "" && args["code"] != null && Text(args, "workflow") == "")
                {
                    args = (JsonObject)args.DeepClone();
                    args["workflow"] = "temporary-" + Guid.NewGuid().ToString("N");
                }
                if (!Actions.RunningScript && !preparedDispatch.Value
                    && Text(args, "task_node") != "" && args["code"] == null && args["tool"] == null
                    && args["program"] == null && args["workflow"] == null)
                {
                    while (true)
                    {
                        var joined = await NativeAgent.OnMain(() =>
                        {
                            string node = Text(args, "task_node");
                            string workflowId = Plan.Script(node)["workflow"].ToString();
                            bool dispatching = !continuation.IsCompleted && LastContinuation?["task_node"]?.ToString() == node
                                && LastContinuation?["status"]?.ToString() == "starting";
                            if (dispatching && LastContinuation?["mode"]?.ToString() == "recovery")
                                return (Report: (JsonObject)null, Recovery: continuation);
                            bool running = Workflows.TryGetValue(workflowId, out var existing) && existing.Running;
                            var active = Actions.Summary().FirstOrDefault(action => action["task_node"]?.ToString() == node
                                && action["tool"]?.ToString() != "fight" && action["status"]?.ToString() == "running");
                            if (!running && !dispatching && active?["tool"]?.ToString() != "run_script") return (Report: (JsonObject)null, Recovery: (Task)null);
                            return (Report: new JsonObject { ["ok"] = true, ["already_running"] = true, ["input_effects"] = false,
                                ["task_node"] = node, ["workflow"] = workflowId, ["handle"] = active?["handle"]?.DeepClone(),
                                ["status"] = active != null || running ? "running" : "starting" }, Recovery: (Task)null);
                        }, token);
                        if (joined.Recovery != null) { await joined.Recovery.WaitAsync(token); continue; }
                        if (joined.Report == null) break;
                        DiagLog.Write("[agent_script_join] " + joined.Report.ToJsonString());
                        return joined.Report.ToJsonString();
                    }
                }
                bool goalBackground = !Actions.RunningScript && !preparedDispatch.Value && !args.ContainsKey("background")
                    && Text(args, "task_node") != "" && NativeAgent.GoalState?["native"]?["status"]?.ToString() == "active"
                    && (args["code"] == null || !AgentScript.PreparationOnly(Text(args, "code")));
                if (Bool(args, "background") || goalBackground)
                {
                    string node = Text(args, "task_node");
                    if (node == "" || args["program"] != null || args["code"] != null && Text(args, "workflow") == "")
                        throw new ArgumentException("后台脚本需要task_node；临时代码需要独立workflow。");
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
                            var scriptArgs = (JsonObject)args.DeepClone();
                            scriptArgs.Remove("background");
                            var started = Actions.Start("run_script", scriptArgs, token);
                            started["default_background"] = goalBackground;
                            if (scriptArgs["workflow"] != null) started["workflow_id"] = scriptArgs["workflow"].DeepClone();
                            return started;
                        }
                        finally { ActionTasks.NodeContext.Value = previous; }
                    }, token)).ToJsonString();
                }
                bool transientWorkflow = Text(args, "task_node") != "" && args["code"] != null;
                if (Text(args, "task_node") != "")
                {
                    bool temporary = args["code"] != null || args["tool"] != null;
                    if (args["program"] != null || !temporary && args["workflow"] != null
                        || args["code"] != null && Text(args, "workflow") == "")
                        throw new ArgumentException("task_node执行原脚本或tool/args单工具；代码恢复须同时提供code和独立workflow，不支持program。");
                    string nodeId = Text(args, "task_node");
                    var nodeScript = await NativeAgent.OnMain(() =>
                    {
                        var script = Plan.Script(nodeId);
                        if (temporary && Text(args, "workflow") == script["workflow"]?.ToString())
                            throw new ArgumentException("临时恢复workflow须与原节点workflow不同，保留原步骤进度。");
                        if (Plan.Snapshot()["current_step"]?.ToString() != nodeId
                            && Actions.Summary().Any(a => a["tool"]?.ToString() != "fight" && a["status"]?.ToString() == "running"))
                            throw new InvalidOperationException("切换节点前须暂停原动作并保留句柄；独立防身攻击不阻止切换。");
                        Plan.Select(nodeId);
                        Watches.AcknowledgeExecutionStart(nodeId, Main.LocalPlayer.dead, Main.LocalPlayer.suffocating, Actions.Summary());
                        return script;
                    }, token);
                    args = (JsonObject)args.DeepClone();
                    if (!temporary)
                    {
                        args["code"] = nodeScript["code"].DeepClone();
                        args["workflow"] = nodeScript["workflow"].DeepClone();
                    }
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
                string skillSavePath = Main.SavePath, skillWorld = Main.worldName + ":" + Main.worldID;
                void RecordSkills(JsonObject report)
                {
                    var records = (JsonArray)report["skill_calls"].DeepClone();
                    if (program != null)
                    {
                        var record = new JsonObject { ["name"] = program["name"].DeepClone(), ["revision"] = program["revision"].DeepClone(),
                            ["source_sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(program["code"].ToString()))),
                            ["status"] = report["ok"]?.GetValue<bool>() == true ? "returned" : "interrupted",
                            ["elapsed_ms"] = report["elapsed_ms"].DeepClone(), ["tool_calls"] = report["tool_calls"].DeepClone(),
                            ["result"] = new JsonObject { ["error"] = report["error"]?.DeepClone() } };
                        foreach (string field in new[] { "status", "reason", "error", "outcome" })
                            if (report["value"] is JsonObject returned && returned[field] is JsonValue v && v.TryGetValue<string>(out string text))
                                record["result"][field] = text[..Math.Min(text.Length, 192)];
                        if (report["failure"] is JsonObject failure)
                            record["last_failure"] = new JsonObject { ["tool"] = failure["tool"]?.DeepClone(),
                                ["error"] = failure["evidence"]?["error"]?.DeepClone(), ["reason"] = failure["evidence"]?["reason"]?.DeepClone(),
                                ["outcome"] = failure["evidence"]?["outcome"]?.DeepClone() };
                        records.Add(record);
                    }
                    if (records.Count == 0) return;
                    try { new ScriptLibrary(System.IO.Path.Combine(skillSavePath, "TerraBlindAgent", "programs")).RecordCalls(records, skillWorld); }
                    catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                    { DiagLog.Write("[agent_skill_evidence] " + error.Message); }
                }
                var localRecovery = recoveryContext.Value;
                var workflow = await NativeAgent.OnMain(() =>
                {
                    string id = Text(args, "workflow");
                    if (id == "") return null;
                    if (id.Length > 80) throw new ArgumentException("workflow需要1..80字符。");
                    if (localRecovery is { } scoped && id == scoped.Id) return scoped.Progress;
                    if (!Workflows.TryGetValue(id, out var saved))
                    {
                        if (Workflows.Count >= 32) throw new InvalidOperationException("本世界最多32个workflow。");
                        Workflows[id] = saved = new ScriptProgress();
                    }
                    return saved;
                }, token);
                Guid eventReader = localEvents ? await NativeAgent.OnMain(Watches.BeginScriptDelivery, token) : Guid.Empty;
                Guid handlerReader = Guid.NewGuid();
                bool mayAct = !AgentScript.PreparationOnly(source);
                Interlocked.Increment(ref executingScripts);
                if (mayAct) Interlocked.Increment(ref actionScripts);
                long currentInvocation = Interlocked.Increment(ref invocationSequence);
                AgentScript.Reactions scriptReactions = null;
                try
                {
                    result = await AgentScript.Run(source, Int(args, "seconds", 120), NativeAgent.ValidateTool,
                        async (child, childArgs, childToken) =>
                        {
                            string previousNode = ActionTasks.NodeContext.Value;
                            long previousInvocation = scriptInvocation.Value;
                            scriptInvocation.Value = currentInvocation;
                            ActionTasks.NodeContext.Value = Text(args, "task_node") is string owner && owner.Length > 0 ? owner : previousNode;
                            try { return await ExecuteScriptTool(child, childArgs, token, childToken, localEvents); }
                            finally { ActionTasks.NodeContext.Value = previousNode; scriptInvocation.Value = previousInvocation; }
                        },
                        async cleanupToken => await NativeAgent.OnMain(() =>
                        {
                            if (ContactEscapeActive) return true;
                            if (queuedScript) StopInputs(preserveAttack: Actions.HasAttack);
                            else Stop();
                            return true;
                        }, cleanupToken), token, workflow,
                        localEvents ? null : checkToken => NativeAgent.OnMain(() => Watches.HasRecoveryBlocker ? Watches.Pending(recoveryOnly: true) : null, checkToken),
                        new ScriptLibrary(System.IO.Path.Combine(Main.SavePath, "TerraBlindAgent", "programs")).Callable(), RecordSkills,
                        kind =>
                        {
                            scriptReactions = AgentScript.CurrentReactions.Value;
                            NativeAgent.OnMain(() => { Watches.RegisterHandler(handlerReader, kind); return true; }, token).GetAwaiter().GetResult();
                        });
                }
                finally
                {
                    if (scriptReactions != null) ClearCombatPreparation(scriptReactions);
                    Interlocked.Decrement(ref executingScripts);
                    if (mayAct) Interlocked.Decrement(ref actionScripts);
                    await NativeAgent.OnMain(() => { Watches.EndHandlers(handlerReader); return true; }, default);
                    if (eventReader != Guid.Empty)
                        await NativeAgent.OnMain(() => { Watches.EndScriptDelivery(eventReader); return true; }, default);
                }
                if (transientWorkflow && workflow != null)
                    await NativeAgent.OnMain(() =>
                    {
                        string id = Text(args, "workflow");
                        bool retained = Workflows.TryGetValue(id, out var saved) && ReferenceEquals(saved, workflow);
                        if (retained && !workflow.Running && workflow.Snapshot()["steps"].AsArray().Count == 0)
                        { Workflows.Remove(id); retained = false; }
                        result["workflow_retained"] = retained;
                        result["workflow_id"] = id;
                        return true;
                    }, token);
                if (program != null)
                    result["program"] = new JsonObject { ["name"] = program["name"].DeepClone(), ["revision"] = program["revision"].DeepClone() };
                if (Text(args, "task_node") != "") result["task_node"] = args["task_node"].DeepClone();
                if (result["value"] is JsonObject returnedValue && returnedValue["wake"] is JsonObject deliveredWake)
                {
                    string activeHandle = Actions.ActiveHandle;
                    if (!queuedScript && activeHandle != null && deliveredWake["continue_action"]?.ToString() != activeHandle)
                        await Actions.Control(activeHandle, "pause", null, token);
                    var deliveredEvents = await NativeAgent.OnMain(() => Watches.Take(8, recoveryOnly: true), token);
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
                bool scriptOwnsAction = Actions.RunningScript;
                string eventHandle = await NativeAgent.OnMain(() => !scriptOwnsAction && Watches.HasInterrupting ? Actions.ActiveHandle : null, token);
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
                        await NativeAgent.OnMain(() =>
                        {
                            Plan.Script(ownerNode);
                            if (!string.IsNullOrEmpty(callerNode) && callerNode != ownerNode
                                && Plan.Snapshot()["ready_steps"].AsArray().Any(node => node?.ToString() == callerNode))
                                Plan.Advance(callerNode, AgentTaskEvidence.Measure);
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
                        var edited = (args["nodes"] as JsonArray ?? new JsonArray())
                            .Where(n => n["script"] != null || n["depends_on"] != null).Select(n => n["id"]?.ToString()).ToHashSet();
                        var dependencies = (args["nodes"] as JsonArray ?? new JsonArray())
                            .Where(n => n["depends_on"] != null).Select(n => n["id"]?.ToString()).ToHashSet();
                        if (Actions.Summary().Any(a => a["tool"]?.ToString() != "fight"
                            && (a["status"]?.ToString() == "running" && dependencies.Contains(a["task_node"]?.ToString())
                                || a["status"]?.ToString() == "queued" && edited.Contains(a["task_node"]?.ToString()))))
                            throw new InvalidOperationException("执行中节点的依赖修改须先暂停；已排队节点须先取消待执行句柄。script更新保留当前执行，从下次续接生效。");
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
                    result = await gatherWorld.Observe(token, reactive: false);
                    if (sourceArea != null)
                        result["candidates"] = new JsonArray(result["candidates"].AsArray().OfType<JsonObject>()
                            .Where(sourceArea.Contains).Select(c => c.DeepClone()).ToArray());
                    result["source_area"] = sourceArea?.Snapshot();
                }
                else
                {
                    await NativeAgent.OnMain(() => { ControlSession.BindGatherTargets(targets); return true; }, token);
                    result = await Gather.Run(targets, Int(args, "seconds", 120), gatherWorld, token, sourceArea);
                }
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
                    result["chest_was_locked"] = chestBefore?.Locked;
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
                    : kind == "item" ? await NativeAgent.OnMain(() => ItemCatalog(Text(args, "name")), token)
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
                        if (result["status"]?.ToString() == "completed") ClearTaskPolicy();
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
            case "open_item":
            case "inventory_drop":
            case "inventory_trash":
                var idleClock = Stopwatch.StartNew();
                while (idleClock.Elapsed.TotalSeconds < 2 && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                    || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                    await Task.Delay(50, token);
                result = await NativeAgent.OnMain(() => name switch
                {
                    "open_item" => PvpInventory.Open(args),
                    "inventory_drop" => PvpInventory.Drop(args),
                    "inventory_trash" => PvpInventory.Trash(args),
                    _ => PvpInventory.Swap(args)
                }, token);
                if (name == "open_item" && !Rejected(result)) await Task.Delay(300, token);
                break;
            case "equipment_list":
                return (await NativeAgent.OnMain(Equipment.Snapshot, token)).ToJsonString();
            case "inventory_review":
                return (await NativeAgent.OnMain(PvpInventory.Review, token)).ToJsonString();
            case "equip_item":
                while (!await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                    || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                    await NativeAgent.NextUpdate(token);
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
                    var probes = terrain["stance_probes"].AsArray();
                    var seen = probes.OfType<JsonObject>().Select(p => (Int(p, "support_x"), Int(p, "support_y"))).ToHashSet();
                    foreach (float fraction in new[] { .25f, .5f, .75f, 1f })
                    foreach (int dx in new[] { -6, 0, 6 })
                    foreach (int dy in new[] { -8, 0, 8 })
                    {
                        var sample = Microsoft.Xna.Framework.Vector2.Lerp(player.Center, npc.Center, fraction);
                        int x = (int)(sample.X / 16) + dx;
                        int firstY = (int)((sample.Y + player.height / 2f) / 16) + dy;
                        for (int y = firstY; y <= firstY + 8; y++)
                        {
                            if (!seen.Add((x, y))) continue;
                            var probe = PvpTerrain.ProbeStance(player, npc.Center, x, y);
                            if (probe["supported"]?.GetValue<bool>() != true) continue;
                            if (probe["body_fits"]?.GetValue<bool>() == true) probes.Add(probe);
                            break;
                        }
                    }
                    var weapons = player.inventory.Take(10).Where(item => CombatLoadout.Usable(item)
                        && (CombatLoadout.PreferredType == 0 || item.type == CombatLoadout.PreferredType)
                        && (item.useAmmo == 0 || player.HasAmmo(item)) && item.mana * player.manaCost <= player.statMana).ToArray();
                    JsonArray HittingWeapons(Microsoft.Xna.Framework.Vector2 source) => new(weapons
                        .Where(item => FightCoordinator.CanHitTargetFrom(player, item, source, npc.Center, npc.width / 2f))
                        .Select(item => (JsonNode)JsonValue.Create(item.type)).DistinctBy(item => item.ToString()).ToArray());
                    foreach (var stance in terrain["stance_probes"].AsArray().Concat(requestedStances).OfType<JsonObject>())
                    {
                        if (stance["x"] == null) continue;
                        var center = new Microsoft.Xna.Framework.Vector2(stance["x"].GetValue<float>(), stance["y"].GetValue<float>());
                        stance["target_distance_tiles"] = Microsoft.Xna.Framework.Vector2.Distance(center, npc.Center) / 16;
                        stance["player_distance_tiles"] = Microsoft.Xna.Framework.Vector2.Distance(center, player.Center) / 16;
                        stance["weapon_types"] = stance["body_fits"]?.GetValue<bool>() == true ? HittingWeapons(center) : new JsonArray();
                    }
                    return new JsonObject { ["npc"] = who, ["npc_type"] = npc.type, ["hp"] = npc.life,
                        ["distance_tiles"] = Microsoft.Xna.Framework.Vector2.Distance(player.Center, npc.Center) / 16,
                        ["attackable"] = npc.CanBeChasedBy(player), ["contact_damage"] = npc.damage,
                        ["dont_take_damage"] = npc.dontTakeDamage, ["immortal"] = npc.immortal,
                        ["clear_line"] = terrain["direct_line"]["clear"].DeepClone(),
                        ["current_weapon_types"] = HittingWeapons(player.Center),
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
                        requestedBossControl = Bool(args, "boss_control");
                        SetBossControl(requestedBossControl.Value);
                    }
                    if (reserve is double requestedReserve)
                    {
                        bossBefore ??= (Combat.Enabled, Dodge.Enabled, Dodge.MovementReserveScale);
                        Dodge.MovementReserveScale = (float)requestedReserve;
                    }
                    if (args["auto_dodge"] != null) AgentPermissions.Dodge = Bool(args, "auto_dodge");
                    if (args["auto_attack"] != null) AgentPermissions.Attack = Bool(args, "auto_attack");
                    if (!AgentPermissions.Attack) { Combat.Stop(); FightCoordinator.Stop(); PlayerCombat.Stop(); }
                    if (args["movement_intent"] is JsonObject movementIntent)
                    {
                        var applied = FeedbackControl.Set(movementIntent);
                        if (applied["error"] != null) return applied;
                    }
                    if (args["frame_program"] is JsonObject frameProgram) FrameProgram.Set(frameProgram);
                    return new JsonObject { ["auto_dodge"] = AgentPermissions.Dodge, ["auto_attack"] = AgentPermissions.Attack,
                        ["frame_program"] = FrameProgram.Snapshot(),
                        ["movement_intent"] = FeedbackControl.Snapshot(),
                        ["weapon_type"] = CombatLoadout.PreferredType,
                        ["boss_control"] = BossStatus(fresh: true) };
                }, token);
                break;
            case "boss_status":
                return (await NativeAgent.OnMain(() => BossStatus(fresh: true), token)).ToJsonString();
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
            case "find_descent":
                string descentQuery=args.ToJsonString(ModelJson);
                var descentResponse=await Task.Run(() => HttpServerSystem.Dispatch("/find_descent",descentQuery),token);
                result=JsonNode.Parse(descentResponse.Body).AsObject();
                if(descentResponse.Status>=400&&result["error"]==null) result["error"]="status "+descentResponse.Status;
                break;
            case "item_info": case "find_biome":
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
                if (!Rejected(result)) result = await Wait("/mine_status", "running", "/mine_stop", 120, token,
                    reactionContext: new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() });
                if (Text(result, "outcome") == "out_of_box"
                    || Text(result, "outcome") == "out_of_reach" && Int(result, "mined") > 0)
                {
                    var mining = result;
                    var navigation = await Navigate(new JsonObject { ["x"] = args["target_x"].DeepClone(),
                        ["y"] = args["target_y"].DeepClone(), ["mode"] = "mine" }, token);
                    if (Bool(navigation, "done"))
                        result = new JsonObject { ["outcome"] = "done", ["mined"] = mining["mined"]?.DeepClone(),
                            ["mining_attempt"] = mining.DeepClone(), ["mode_adjustment"] = "replanned_after_mining_drift" };
                    result["navigation"] = navigation;
                }
                if (!Rejected(result) && Text(result, "outcome") != "done")
                {
                    result["ok"] = false;
                    result["error"] = "mining_incomplete";
                }
                break;
            case "use_item":
                var usedItem = await NativeAgent.OnMain(() =>
                {
                    var player = Main.LocalPlayer;
                    int slot = Int(args, "slot", -1);
                    if (slot < 0 || slot >= player.inventory.Length) return null;
                    var item = player.inventory[slot];
                    return new JsonObject { ["type"] = item.type, ["consumable"] = item.consumable, ["heal_life"] = item.healLife,
                        ["started_tick"] = (ulong)Main.GameUpdateCount,
                        ["collect_tool"] = item.pick > 0 || item.axe > 0 || item.hammer > 0,
                        ["count"] = player.inventory.Where(i => i.type == item.type).Sum(i => i.stack) };
                }, token);
                bool boundedCollect = usedItem?["collect_tool"]?.GetValue<bool>() == true && Int(args, "duration_ticks") > 0;
                if (usedItem?["collect_tool"]?.GetValue<bool>() == true && !boundedCollect)
                {
                    var collection = await CollectTarget(Int(args, "x"), Int(args, "y"), token, requestedSlot: Int(args, "slot", -1));
                    if (collection != null) { result = collection; break; }
                }
                result = await Call("/item_use", new JsonObject
                {
                    ["target_wx"] = Int(args, "x"), ["target_wy"] = Int(args, "y"), ["slot"] = Int(args, "slot", -1),
                    ["duration_ticks"] = Int(args, "duration_ticks", 30), ["strict"] = Bool(args, "strict")
                }, token);
                if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token,
                    navigationProgress: boundedCollect ? _ => NativeAgent.OnMain(() =>
                        Main.GameUpdateCount - usedItem["started_tick"].GetValue<ulong>() >= (ulong)Int(args, "duration_ticks")
                            ? new JsonObject { ["outcome"] = "time_elapsed", ["duration_ticks"] = Int(args, "duration_ticks") } : null, token) : null,
                    reactionContext: new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() },
                    onCompleted: async receipt =>
                    {
                        if (Text(receipt, "outcome") != "n/a" || usedItem?["consumable"]?.GetValue<bool>() != true) return;
                        await NativeAgent.OnMain(() =>
                        {
                            var player = Main.LocalPlayer;
                            int remaining = player.inventory.Where(i => i.type == usedItem["type"].GetValue<int>()).Sum(i => i.stack);
                            int consumed = usedItem["count"].GetValue<int>() - remaining;
                            receipt["consumed"] = Math.Max(0, consumed); receipt["verified"] = consumed > 0;
                            if (consumed > 0) receipt["outcome"] = "consumed";
                            else
                            {
                                receipt["error"] = "item_use_not_observed"; receipt["outcome"] = "no_progress";
                                receipt["reason"] = usedItem["heal_life"].GetValue<int>() > 0 && player.potionDelay > 0
                                    ? "potion_sickness" : "consumable_count_unchanged";
                            }
                            receipt["potion_delay_ticks"] = player.potionDelay;
                            return true;
                        }, token);
                    });
                break;
            case "place_at":
                result = await Place(args, token);
                break;
            case "house_site":
                int rooms = Int(args, "rooms", 1), direction = Int(args, "dir", 1);
                var site = new JsonObject { ["w"] = rooms * 5 + 1, ["h"] = 10, ["range"] = Int(args, "range", 100) };
                foreach (string field in new[] { "from_x", "from_y" })
                    if (args[field] != null) site[field] = args[field].DeepClone();
                result = await Call("/scan_house", site, token);
                if (Bool(result, "found"))
                    result["build_args"] = new JsonObject { ["rooms"] = rooms, ["dir"] = direction,
                        ["x"] = result["at"][0].GetValue<int>() + (direction < 0 ? rooms * 5 : 0),
                        ["y"] = result["at"][1].GetValue<int>() };
                break;
            case "build_house":
                result = await Call("/build_house", args, token);
                if (!Rejected(result) && result["accepted"]?.GetValue<bool>() == true)
                    result = await Wait("/build_house_status", "running", "/build_house_stop", 0, token);
                break;
            case "hell_run":
                result = await NativeAgent.OnMain(() => new JsonObject
                {
                    ["accepted"] = StateSnapshotPlayer.StartHellRun(out string reason), ["reason"] = reason
                }, token);
                if (!Rejected(result)) result = await Wait("/hell_run_status", "running", "/hell_run_stop", 0, token);
                break;
            case "interact":
                result = await NativeAgent.OnMain(() => HttpServerSystem.InteractAt(Int(args, "x"), Int(args, "y")), token);
                if (Text(result, "outcome") == "pending")
                {
                    int x = Int(result, "x"), y = Int(result, "y");
                    int world = Int(result, "world_id");
                    JsonObject confirmed = null;
                    for (int frame = 0; frame < 300 && confirmed == null; frame++)
                    {
                        await NativeAgent.NextUpdate(token);
                        confirmed = await NativeAgent.OnMain(() =>
                        {
                            var p = Main.LocalPlayer;
                            if (Main.gameMenu || Main.worldID != world || !p.active || p.dead)
                                return new JsonObject { ["error"] = "chest_open_interrupted", ["x"] = x, ["y"] = y };
                            if (p.chest < 0 || p.chest >= Main.chest.Length || Main.chest[p.chest] == null) return null;
                            var chest = Main.chest[p.chest];
                            bool opened = chest.x == x && chest.y == y;
                            HttpServerSystem.LastInteract = opened ? "opened" : "chest_changed";
                            return new JsonObject { ["outcome"] = opened ? "opened" : "chest_changed",
                                ["error"] = opened ? null : "chest_changed", ["chest"] = p.chest, ["x"] = chest.x, ["y"] = chest.y };
                        }, token);
                    }
                    result = confirmed ?? new JsonObject { ["error"] = "chest_open_not_confirmed", ["x"] = x, ["y"] = y };
                }
                break;
            case "loot_all":
                result = await NativeAgent.OnMain(HttpServerSystem.LootOpenChest, token);
                break;
            case "craft":
                var craftArgs = new JsonObject { ["item_name"] = Text(args, "name"), ["amount"] = Int(args, "amount", 1) };
                var matches = await NativeAgent.OnMain(() => ItemCatalog(Text(args, "name")), token);
                var itemIds = matches["matches"].AsArray().Where(m => m["exact"]?.GetValue<bool>() == true)
                    .Select(m => m["type"].GetValue<int>()).Distinct().ToArray();
                if (itemIds.Length == 1) craftArgs["item_id"] = itemIds[0];
                result = await Call("/craft", craftArgs, token);
                break;
            case "act":
                JsonObject observation = null;
                bool observe = Bool(args, "observe");
                if (observe)
                {
                    if (BeginActObservation == null || EndActObservation == null)
                        return "{\"error\":\"observation_unavailable\"}";
                    observation = await NativeAgent.OnMain(BeginActObservation, token);
                    if (Rejected(observation)) return observation.ToJsonString();
                }
                try
                {
                    result = await Call("/act", args, token);
                    if (!Rejected(result)) result = await Wait("/act_status", "active", "/act_stop", 120, token,
                        reactionContext: new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() });
                }
                finally
                {
                    if (observe) observation = await NativeAgent.OnMain(EndActObservation, default);
                }
                if (observation != null) result["observation"] = observation;
                if (args["combat_candidate"] != null)
                    result = await NativeAgent.OnMain(() => CombatForecast.Receipt(args, result), token);
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
                    result = await Wait("/fight_active", "active", "/fight", Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120), token,
                        reactionContext: new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() });
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

    static JsonObject ItemCatalog(string name) => AgentCatalog.Query(typeof(Terraria.ID.ItemID), name, type =>
        Terraria.ID.ContentSamples.ItemsByType.TryGetValue(type, out var item) ? new[] { item.Name } : Array.Empty<string>());

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
                            var match = new JsonObject { ["x"] = x, ["y"] = y, ["dist"] = (float)Math.Sqrt(dx * dx + dy * dy),
                                ["emission"] = emission,
                                ["tile_type"] = type, ["frame_x"] = (int)Main.tile[x, y].TileFrameX, ["frame_y"] = (int)Main.tile[x, y].TileFrameY };
                            if (type is Terraria.ID.TileID.Containers or Terraria.ID.TileID.Containers2)
                                match["locked"] = Chest.IsLocked(x, y);
                            matches.Add(match);
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
        bool localIntent = AgentScript.CurrentIntent.Value != null;
        return await NativeAgent.OnMain(() =>
        {
            DiagLog.Write($"[agent] result {name} {output}");
            if (!ObservationTool(name))
                NativeAgent.Progress(localIntent ? "局部意图完成：" + name : "执行结束：" + name + "，等待下一步决策。",
                    localIntent ? "局部行动续接" : "等待模型决策", !localIntent && !PvpDuel.Active && !name.StartsWith("combat_"));
            return output;
        }, token);
    }

    internal static async Task<JsonObject> CollectTarget(int x, int y, CancellationToken token, bool nonSolidOnly = true, int requestedSlot = -1,
        Func<JsonObject> completion = null)
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
            return new JsonObject { ["slot"] = slot, ["in_reach"] = canMine,
                ["solid"] = Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType],
                ["accepted_traps"] = tile.TileType == Terraria.ID.TileID.PressurePlates && slot >= 0
                    && Power(Main.LocalPlayer.inventory[slot]) > 0 ? new JsonArray(new JsonObject {
                        ["x"] = x, ["y"] = y, ["type"] = (int)tile.TileType,
                        ["frame_x"] = (int)tile.TileFrameX, ["frame_y"] = (int)tile.TileFrameY }) : null };
        }, token);
        if (target == null) return null;
        if (Int(target, "slot", -1) < 0) return new JsonObject { ["error"] = "no_collect_tool" };
        if (!Bool(target, "in_reach"))
        {
            JsonObject Approach(string mode) => new() { ["x"] = x, ["y"] = y, ["mode"] = mode,
                ["accepted_traps"] = target["accepted_traps"]?.DeepClone() };
            var approach = await Navigate(Approach(Bool(target, "solid") ? "mine" : "interact"), token, interruption: completion);
            var completed = completion == null ? null : await NativeAgent.OnMain(completion, token);
            if (completed != null) return completed;
            if (approach["done"]?.GetValue<bool>() != true)
                return new JsonObject { ["error"] = "cannot_reach_collect_target", ["navigation"] = approach };
            if (await NativeAgent.OnMain(() => !Main.tile[x, y].HasUnactuatedTile, token))
            {
                await Task.Delay(400, token);
                return new JsonObject { ["outcome"] = "removed", ["mode_adjustment"] = "navigation_mined_target" };
            }
            bool InMiningRange()
            {
                var player = Main.LocalPlayer;
                int previous = player.selectedItem;
                try { player.selectedItem = Int(target, "slot", -1); return Reach.CanMine(player, x, y); }
                finally { player.selectedItem = previous; }
            }
            bool reachable = await NativeAgent.OnMain(InMiningRange, token);
            if (!reachable && !Bool(target, "solid"))
            {
                approach = await Navigate(Approach("stand"), token, interruption: completion);
                completed = completion == null ? null : await NativeAgent.OnMain(completion, token);
                if (completed != null) return completed;
                if (approach["done"]?.GetValue<bool>() != true)
                    return new JsonObject { ["error"] = "cannot_reach_collect_target", ["navigation"] = approach };
                reachable = await NativeAgent.OnMain(InMiningRange, token);
            }
            if (!reachable) return new JsonObject { ["error"] = "collect_target_out_of_mining_range",
                ["x"] = x, ["y"] = y, ["navigation"] = approach };
        }
        var result = await Call("/item_use", new JsonObject { ["target_wx"] = x, ["target_wy"] = y,
            ["slot"] = target["slot"].DeepClone(), ["strict"] = true }, token);
        if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 15, token,
            navigationProgress: completion == null ? null : _ => NativeAgent.OnMain(completion, token),
            reactionContext: new JsonObject { ["tool"] = "use_item", ["args"] = new JsonObject {
                ["slot"] = target["slot"].DeepClone(), ["x"] = x, ["y"] = y, ["strict"] = true } });
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

    internal static async Task<JsonObject> Navigate(JsonObject args, CancellationToken token, int seconds = 0,
        Func<JsonObject> interruption = null)
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
        var clearances = new JsonArray();
        var clearedSites = new HashSet<string>();
        var clock = Stopwatch.StartNew();
        JsonObject result;
        while (true)
        {
            bool reserve = await NativeAgent.OnMain(() =>
            {
                if (!VanillaNavBridge.Enabled) return false;
                FightCoordinator.NavigationSnapshotPending = true;
                return true;
            }, token);
            JsonObject start;
            try
            {
                while (reserve && !await NativeAgent.OnMain(() =>
                {
                    var player = Main.LocalPlayer;
                    return player.dead || player.itemAnimation == 0 && player.itemTime == 0
                        || player.HeldItem.pick > 0 || player.HeldItem.createTile >= 0;
                }, token)) await Task.Delay(16, token);
                start = await Call("/nav_recede", request, token);
            }
            finally
            {
                if (reserve) await NativeAgent.OnMain(() => { FightCoordinator.NavigationSnapshotPending = false; return true; }, default);
            }
            if (Rejected(start))
            {
                if (clearances.Count > 0) start["verified_clearances"] = clearances;
                return start;
            }
            var progress = new NavigationProgress(requireApproach: true);
            var reactions = AgentScript.CurrentReactions.Value;
            bool? lastPlanning = null;
            int remaining = seconds <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(seconds - clock.Elapsed.TotalSeconds));
            result = await Wait("/nav_recede_done", "nav", "/nav_recede_stop", remaining, token,
            async elapsed =>
            {
                await ReactAtBoundary(token,
                        async ct =>
                        {
                            var stopped = await Call("/nav_recede_stop", null, ct);
                            if (Rejected(stopped)) throw new InvalidOperationException(stopped.ToJsonString());
                        },
                        async ct =>
                        {
                            var restarted = await Call("/nav_recede", request, ct);
                            if (Rejected(restarted)) throw new InvalidOperationException(restarted.ToJsonString());
                            progress = new NavigationProgress(requireApproach: true);
                        }, new JsonObject { ["tool"] = "nav_to", ["args"] = args.DeepClone() });
                if (reactions?.NavigationConditionMet == true)
                    return new JsonObject { ["outcome"] = "condition_met", ["condition_met"] = true };
                return await NativeAgent.OnMain(() =>
            {
                var interrupted = interruption?.Invoke();
                if (interrupted != null) return interrupted;
                var player = Main.LocalPlayer;
                bool planning = !RecedingNav.FieldReady;
                if (lastPlanning != planning)
                {
                    lastPlanning = planning;
                    DiagLog.Write($"[agent_navigation_phase] phase={(planning ? "building_field" : "executing")} elapsed_seconds={elapsed:0.###}");
                }
                int gx = Int(request, "gx"), gy = Int(request, "gy");
                if (!planning && !Reach.CanInteract(player, gx, gy))
                {
                    var field = MazeWand.PeekFieldOrNull(gx, gy);
                    if (field?.Count == 1 && field.ContainsKey((gx, gy)))
                        return new JsonObject { ["error"] = "navigation_goal_isolated", ["outcome"] = "no_route",
                            ["reason"] = "goal_has_no_traversable_neighbor", ["field_cells"] = field.Count,
                            ["target"] = new JsonObject { ["x"] = gx, ["y"] = gy }, ["elapsed_seconds"] = elapsed };
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
                double distance = NavigationDistance(player, gx, gy, Bool(request, "stand"));
                return progress.Observe(elapsed, player.position.X, player.position.Y, distance, terrain, planning);
                }, token);
            });
            bool escaped = await NativeAgent.OnMain(() => Text(result, "status") == "failed" && Text(result, "reason") == "stopped" && LastContactEscape != null
                && !ReferenceEquals(escapeBefore, LastContactEscape), token);
            if (!escaped) break;
            while (await NativeAgent.OnMain(() => ContactEscapeActive && !Main.LocalPlayer.dead, token))
                await NativeAgent.NextUpdate(token);
            JsonObject remainingHazard = null;
            var clearance = await NativeAgent.OnMain(() =>
            {
                var escape = LastContactEscape;
                var player = Main.LocalPlayer;
                if (player.dead || player.suffocating || Text(escape, "status") != "controls_released"
                    || !Bool(escape, "contact_clear") || Collision.HurtTiles(player.position, player.width, player.height, player).type >= 0) return null;
                remainingHazard = TerrainInspection.NavigationHazard(acceptedTraps);
                if (remainingHazard != null) return null;
                var hazards = (escape["hazard_tiles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                if (escape["predicted_tile"] is JsonObject predicted && !hazards.Any(tile =>
                    Int(tile, "x") == Int(predicted, "x") && Int(tile, "y") == Int(predicted, "y") && Int(tile, "type") == Int(predicted, "type")))
                    hazards.Add(predicted);
                bool removed = hazards.Any(tile =>
                {
                    int x = Int(tile, "x"), y = Int(tile, "y");
                    return WorldGen.InWorld(x, y) && (!Main.tile[x, y].HasTile || Main.tile[x, y].TileType != Int(tile, "type"));
                });
                bool relocated = escape["start_pos"] is JsonObject origin
                    && Math.Pow(player.position.X - origin["x"].Deserialize<double>(), 2)
                        + Math.Pow(player.position.Y - origin["y"].Deserialize<double>(), 2) >= Math.Pow(Math.Max(player.width, player.height), 2);
                if (!removed && !relocated) return null;
                string site = string.Join(";", hazards.Select(tile => tile["x"] + "," + tile["y"] + ":" + tile["type"]));
                if (!clearedSites.Add(site)) return null;
                escapeBefore = escape;
                var verified = (JsonObject)escape.DeepClone();
                verified["resume_reason"] = removed ? "hazard_removed" : "relocated_clear";
                return verified;
            }, token);
            if (clearance == null || seconds > 0 && clock.Elapsed.TotalSeconds >= seconds)
            {
                if (remainingHazard != null) result = remainingHazard;
                break;
            }
            clearances.Add(clearance);
            DiagLog.Write("[agent_navigation_resume] " + new JsonObject { ["target"] = args.DeepClone(),
                ["clearance"] = clearance.DeepClone(), ["elapsed_ms"] = clock.ElapsedMilliseconds });
        }
        return await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            if (clearances.Count > 0) result["verified_clearances"] = clearances;
            if (!Bool(result, "done") && LastContactEscape != null && !ReferenceEquals(escapeBefore, LastContactEscape))
            {
                result["outcome"] = "unsafe";
                if (result["error"] == null) result["error"] = "navigation_contact_escape";
                result["contact_escape"] = LastContactEscape.DeepClone();
            }
            int distance = Math.Max(Math.Abs(ActExecutor.OriginCx(p) - Int(args, "x")), Math.Abs(ActExecutor.OriginCy(p) - Int(args, "y")));
            bool arrived = Bool(request, "reach") ? Reach.CanInteract(p, Int(args, "x"), Int(args, "y")) : distance <= 6;
            if (Bool(request, "reach") && (Bool(args, "exact") || Text(args, "mode") == "mine"))
                result["mode_adjustment"] = "non_solid_target_use_reach_then_use_tool";
            if (Bool(result, "done") && !arrived)
            { result["done"] = false; result["status"] = "stopped_short"; result["dist"] = distance; }
            if (!Bool(result, "done") && !Bool(result, "condition_met") && !Rejected(result)) result["error"] = "navigation_incomplete";
            return result;
        }, token);
    }

    internal static double NavigationDistance(Player player, int gx, int gy, bool stand) =>
        Math.Abs(player.Center.X - (gx * 16 + 8))
        + Math.Abs((stand ? player.position.Y + player.height : player.Center.Y) - (gy * 16 + (stand ? 16 : 8)));

    internal static JsonObject CombatAssessment(int radius)
    {
        if (radius is < 1 or > 60) throw new ArgumentException("radius需要1..60格。");
        var p = Main.LocalPlayer;
        JsonArray SelfBlast(Item weapon)
        {
            int type = weapon.shoot;
            if (weapon.useAmmo > 0 && !p.PickAmmo(weapon, out type, out _, out _, out _, out _, true)) return null;
            var size = FightCoordinator.SelfBlastSize(type);
            return new JsonArray(size.X, size.Y);
        }
        var targets = Main.npc.Where(n => n != null && n.active && !n.friendly && !n.townNPC && n.life > 0
            && Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center) <= radius * 16)
            .OrderBy(n => Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center)).Take(8);
        return new JsonObject { ["tick"] = Main.GameUpdateCount, ["recent_damage"] = LastDamage?.DeepClone(),
            ["movement_intent"] = FeedbackControl.Snapshot(),
            ["navigation_controls"] = new JsonObject {
                ["next_frames"] = new JsonArray(StateSpacePlanner.PeekMovementControls(12, traceUnavailable: false)
                    .Select(keys => (JsonNode)JsonValue.Create(keys)).ToArray()),
                ["walk_direction"] = StateSpacePlanner.PeekWalkIntent()?.Direction,
                ["keys"] = "Bitmask: 1=left, 2=right, 4=jump, 8=up, 16=down. These are queued route inputs, not newly simulated predictions." },
            ["fall_forecast"] = NavigationSurvival.AssessFall(p),
            ["cover_candidates"] = CoverCandidates(p, radius),
            ["preferred_weapon_type"] = CombatLoadout.PreferredType,
            ["navigation_options"] = AgentNavigationIntent.Options(),
            ["boss_present"] = Main.npc.Any(n => n != null && n.active && n.boss
                && Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center) < 3200),
            ["player"] = new JsonObject { ["hp"] = p.statLife,
                ["max_hp"] = p.statLifeMax2,
                ["life_regen_raw"] = p.lifeRegen, ["suffocating"] = p.suffocating,
                ["pos"] = new JsonObject { ["x"] = p.position.X, ["y"] = p.position.Y },
                ["velocity"] = new JsonObject { ["x"] = p.velocity.X, ["y"] = p.velocity.Y },
                ["size_pixels"] = new JsonObject { ["width"] = p.width, ["height"] = p.height },
                ["controls"] = new JsonObject { ["left"] = p.controlLeft, ["right"] = p.controlRight, ["jump"] = p.controlJump },
                ["extra_jump_available"] = p.AnyExtraJumpUsable(), ["wing_time"] = p.wingTime },
            ["projectiles"] = new JsonArray(Main.projectile.Where(shot => shot != null && shot.active && shot.hostile && shot.damage > 0
                && Microsoft.Xna.Framework.Vector2.Distance(shot.Center, p.Center) <= radius * 16)
                .OrderBy(shot => Microsoft.Xna.Framework.Vector2.Distance(shot.Center, p.Center)).Take(12)
                .Select(shot => (JsonNode)new JsonObject { ["type"] = shot.type, ["damage"] = shot.damage,
                    ["x"] = shot.Center.X / 16, ["y"] = shot.Center.Y / 16,
                    ["width_tiles"] = shot.width / 16f, ["height_tiles"] = shot.height / 16f,
                    ["vx_pixels_per_tick"] = shot.velocity.X * (shot.extraUpdates + 1),
                    ["vy_pixels_per_tick"] = shot.velocity.Y * (shot.extraUpdates + 1) }).ToArray()),
            ["targets"] = new JsonArray(targets.Select(n => (JsonNode)new JsonObject
            {
                ["who"] = n.whoAmI, ["type"] = n.type, ["hp"] = n.life, ["contact_damage"] = n.damage, ["defense"] = n.defense,
                ["ignores_tiles"] = n.noTileCollide, ["ignores_gravity"] = n.noGravity,
                ["name"] = Lang.GetNPCNameValue(n.type),
                ["x"] = n.Center.X / 16, ["y"] = n.Center.Y / 16,
                ["width_tiles"] = n.width / 16f, ["height_tiles"] = n.height / 16f,
                ["vx_pixels_per_tick"] = n.velocity.X, ["vy_pixels_per_tick"] = n.velocity.Y,
                ["distance_tiles"] = Microsoft.Xna.Framework.Vector2.Distance(n.Center, p.Center) / 16,
                ["attackable"] = n.CanBeChasedBy(p) && FightCoordinator.TempleTargetAllowed(p, n),
                ["weapons"] = new JsonArray(p.inventory.Take(50).Select((item, slot) => (item, slot))
                    .Where(entry => CombatLoadout.Usable(entry.item)).Select(entry => (JsonNode)new JsonObject
                    {
                        ["slot"] = entry.slot, ["type"] = entry.item.type, ["name"] = entry.item.Name,
                        ["prefix"] = entry.item.prefix, ["stack"] = entry.item.stack,
                        ["damage"] = p.GetWeaponDamage(entry.item), ["use_time_ticks"] = entry.item.useTime,
                        ["projectile_type"] = entry.item.shoot, ["shoot_speed"] = entry.item.shootSpeed,
                        ["self_blast_size_pixels"] = SelfBlast(entry.item),
                        ["consumable"] = entry.item.consumable, ["mana_cost"] = entry.item.mana,
                        ["needs_clear_line"] = FightCoordinator.NeedsClearLine(entry.item),
                        ["has_ammo"] = entry.item.useAmmo <= 0 || p.HasAmmo(entry.item),
                        ["mana_available"] = entry.item.mana * p.manaCost <= p.statMana,
                        ["line_or_reach_passed"] = FightCoordinator.CanHitTarget(p, entry.item, n.Center, n.width / 2f)
                    }).ToArray())
            }).ToArray()) };
    }

    static JsonArray CoverCandidates(Player player, int radius)
    {
        var result = new JsonArray();
        if (player.dead || player.gravDir != 1 || player.velocity.Y != 0
            || !ControlAllows(ControlBehavior.Placement, ControlChannels.ItemUse, delegated: true)) return result;
        var materials = player.inventory.Take(50).Select((item, slot) => (item, slot))
            .Where(v => !v.item.IsAir && !v.item.favorited && v.item.stack > 0 && v.item.createTile >= 0
                && Main.tileSolid[v.item.createTile] && !Main.tileSolidTop[v.item.createTile])
            .OrderByDescending(v => v.item.stack).Take(2).ToArray();
        int bottom = (int)Math.Ceiling(player.Bottom.Y / 16f) - 1;
        int previous = player.selectedItem;
        try
        {
            foreach (var (item, slot) in materials)
            foreach (int side in new[] { -1, 1 })
            foreach (int gap in new[] { 2, 3 })
            {
                int x = side < 0 ? (int)(player.position.X / 16) - gap : (int)((player.position.X + player.width - 1) / 16) + gap;
                if (!WorldGen.InWorld(x, bottom + 1) || !WorldGen.InWorld(x, bottom - 3)) continue;
                var footing = Main.tile[x, bottom + 1];
                if (!footing.HasUnactuatedTile || !Main.tileSolid[footing.TileType] || Main.tileSolidTop[footing.TileType]) continue;
                player.selectedItem = slot;
                int existing = Enumerable.Range(0, 4).TakeWhile(i => Main.tile[x, bottom - i].HasUnactuatedTile
                    && Main.tile[x, bottom - i].TileType == item.createTile).Count();
                int cost = 4 - existing;
                if (cost == 0 || item.stack < cost) continue;
                bool Fits(int y) => !Main.tile[x, y].HasTile && (Main.tile[x, y].LiquidAmount == 0 || Main.tile[x, y].LiquidType != Terraria.ID.LiquidID.Lava)
                    && Reach.CanPlace(player, x, y) && !Control.ForbiddenAt(ControlBehavior.Placement, x, y);
                if (!Enumerable.Range(existing, cost).All(i => Fits(bottom - i))) continue;
                bool Crosses(Microsoft.Xna.Framework.Vector2 target)
                {
                    float dx = target.X - player.Center.X;
                    if (Math.Abs(dx) < 1) return false;
                    float t = (x * 16 + 8 - player.Center.X) / dx;
                    float y = player.Center.Y + t * (target.Y - player.Center.Y);
                    return t > 0 && t < 1 && y >= (bottom - 3) * 16 && y <= (bottom + 1) * 16;
                }
                var enemies = Main.npc.Where(n => n != null && n.active && !n.friendly && !n.townNPC && n.damage > 0
                    && !n.noTileCollide && Microsoft.Xna.Framework.Vector2.Distance(n.Center, player.Center) <= radius * 16 && Crosses(n.Center)).ToArray();
                var shots = Main.projectile.Where(s => s != null && s.active && s.hostile && s.damage > 0 && s.tileCollide
                    && Microsoft.Xna.Framework.Vector2.Distance(s.Center, player.Center) <= radius * 16 && Crosses(s.Center)).ToArray();
                if (enemies.Length == 0 && shots.Length == 0) continue;
                result.Add(new JsonObject { ["id"] = $"cover_{(side < 0 ? "left" : "right")}_{x}_{bottom}_{item.type}",
                    ["side"] = side < 0 ? "left" : "right", ["item_type"] = item.type, ["item_name"] = item.Name,
                    ["available"] = item.stack, ["existing_cells"] = existing, ["cost"] = cost, ["estimated_use_ticks"] = Math.Max(1, item.useTime) * cost,
                    ["action"] = new JsonObject { ["item"] = item.type.ToString(), ["x"] = x, ["y"] = bottom - existing,
                        ["n"] = cost, ["step_y"] = -1, ["preserve_for_action"] = true, ["local"] = true },
                    ["intercepts_npcs"] = new JsonArray(enemies.Select(n => (JsonNode)JsonValue.Create(n.whoAmI)).ToArray()),
                    ["intercepts_projectiles"] = new JsonArray(shots.Select(s => (JsonNode)JsonValue.Create(s.whoAmI)).ToArray()),
                    ["scope"] = "Four empty non-lava cells in native placement range above solid footing. Upper cells depend on placing lower cells first. Intercepts describe current center-to-player lines; enemies can move around the wall. Actual placement and effects require verification." });
            }
        }
        finally { player.selectedItem = previous; }
        return result;
    }

    static async Task<JsonObject> Place(JsonObject args, CancellationToken token)
    {
        string protectAction = Bool(args, "preserve_for_action") ? Actions.ActiveHandle : null;
        bool local = Bool(args, "local");
        if (local && (args["x"] == null || args["y"] == null)) throw new ArgumentException("local放置需要明确世界格坐标x/y。");
        int count = Math.Clamp(Int(args, "n", 1), 1, 200);
        int placed = 0;
        var cells = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            var payload = new JsonObject { ["item"] = Text(args, "item") };
            if (args["x"] != null && args["y"] != null)
                payload["world"] = new JsonArray(Int(args, "x") + i * Int(args, "step_x"), Int(args, "y") + i * Int(args, "step_y"));
            var result = await Call(local ? "/place_at" : "/place_anywhere", payload, token);
            var placement = (JsonObject)result.DeepClone();
            var progress = new NavigationProgress();
            if (!Rejected(result)) result = await Wait(local ? "/place_at_status" : "/place_anywhere_status", "running",
                local ? "/place_at_stop" : "/place_anywhere_stop", local ? 0 : 120, token,
                local ? null : elapsed => NativeAgent.OnMain(() => PlacementProgress(progress, elapsed, placement), token),
                new JsonObject { ["tool"] = "place_at", ["args"] = args.DeepClone() },
                restart: local ? null : async ct =>
                {
                    var resumed = await Call("/place_anywhere", new JsonObject { ["item"] = payload["item"]?.DeepClone(),
                        ["world"] = placement["world"]?.DeepClone() }, ct);
                    if (Rejected(resumed)) throw new InvalidOperationException(resumed.ToJsonString());
                    progress = new NavigationProgress();
                });
            if (local)
            {
                placement["world"] = payload["world"]?.DeepClone();
                placement["expected_tile"] = result["cells"]?.AsArray().FirstOrDefault()?["type"]?.DeepClone();
            }
            if (Text(result, "error") == "placement_no_progress")
                await NativeAgent.OnMain(() => { StopInputs(preserveAttack: Actions.HasAttack); return true; }, token);
            if (Text(result, "outcome") == "done")
                result = await NativeAgent.OnMain(() =>
                {
                    int x = placement["world"]?[0]?.GetValue<int>() ?? -1, y = placement["world"]?[1]?.GetValue<int>() ?? -1;
                    int expectedTile = Int(placement, "expected_tile", -1), expectedWall = Int(placement, "expected_wall", -1);
                    bool inWorld = x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
                    bool verified = inWorld && (expectedTile >= 0 && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == expectedTile
                        || expectedWall > 0 && Main.tile[x, y].WallType == expectedWall);
                    result["world"] = placement["world"]?.DeepClone(); result["verified"] = verified;
                    if (verified && expectedTile >= 0 && protectAction != null && Actions.Summary().Any(a =>
                        a["handle"]?.ToString() == protectAction && a["status"]?.ToString() is "running" or "paused"))
                        result["mining_protection"] = Control.Deny(new[] { ControlBehavior.Mining }, ControlLifetime.Action,
                            protectAction, "Preserve verified construction during its current action", (x, y, 1, 1));
                    if (!verified)
                    {
                        bool occupied = inWorld && Main.tile[x, y].HasTile;
                        result["outcome"] = "stuck";
                        result["error"] = occupied ? "placement_target_occupied" : "placement_not_verified";
                        result["expected_tile"] = expectedTile; result["expected_wall"] = expectedWall;
                        result["actual_tile"] = occupied ? new JsonObject { ["type"] = (int)Main.tile[x, y].TileType,
                            ["frame_x"] = (int)Main.tile[x, y].TileFrameX, ["frame_y"] = (int)Main.tile[x, y].TileFrameY,
                            ["solid"] = Main.tileSolid[Main.tile[x, y].TileType] } : null;
                        result["actual_wall"] = inWorld ? Main.tile[x, y].WallType : -1;
                        result["detail"] = occupied ? "目标格已有其他方块或物件，actual_tile记录现场；按建造方案处理占位物或改选坐标后继续。"
                            : "放置执行器已结束，目标格尚未出现所需方块；结合实际坐标及地形重新检查。";
                    }
                    return result;
                }, token);
            cells.Add(result);
            if (Text(result, "outcome") != "done") break;
            placed++;
        }
        return new JsonObject { ["ok"] = placed == count, ["outcome"] = placed == count ? "done" : placed > 0 ? "partial" : "stuck",
            ["placed"] = placed, ["wanted"] = count, ["cells"] = cells };
    }

    internal static JsonObject PlacementProgress(NavigationProgress progress, double elapsed, JsonObject placement)
    {
        var p = Main.LocalPlayer;
        int px = (int)p.Center.X / 16, py = (int)p.Center.Y / 16;
        var terrain = new Dictionary<int, long>();
        for (int x = px - 4; x <= px + 4; x++)
        for (int y = py - 4; y <= py + 4; y++)
            if (WorldGen.InWorld(x, y))
            {
                var tile = Main.tile[x, y];
                terrain[x * Main.maxTilesY + y] = ((long)tile.WallType << 24)
                    + (tile.HasTile ? 1L + ((long)tile.TileType << 8) + ((long)tile.Slope << 4) + (tile.IsHalfBlock ? 2 : 0) : 0);
            }
        int gx = placement["world"]?[0]?.GetValue<int>() ?? px, gy = placement["world"]?[1]?.GetValue<int>() ?? py;
        double distance = Math.Abs(p.Center.X - gx * 16) + Math.Abs(p.Center.Y - gy * 16);
        var stalled = progress.Observe(elapsed, p.position.X, p.position.Y, distance, terrain,
            RecedingNav.Active && !RecedingNav.FieldReady);
        if (stalled != null)
        {
            stalled["error"] = "placement_no_progress";
            stalled["target"] = placement.DeepClone();
        }
        return stalled;
    }

    static async Task<JsonObject> MineVein(JsonObject args, CancellationToken token)
    {
        string name = Text(args, "name");
        int wanted = Int(args, "count", 1);
        if (wanted is < 1 or > 9999) throw new ArgumentException("count需要1..9999。");
        var source = await NativeAgent.OnMain(() =>
        {
            var catalog = TileCatalog(name);
            var exact = catalog["matches"].AsArray().Where(match => Bool(match.AsObject(), "exact")).ToArray();
            if (catalog["truncated"]?.GetValue<bool>() == true || exact.Select(match => Int(match.AsObject(), "type")).Distinct().Count() != 1)
                return new JsonObject { ["error"] = "ambiguous_tile_name", ["name_candidates"] = catalog["matches"].DeepClone() };
            int tile = Int(exact[0].AsObject(), "type");
            var items = Terraria.ID.ContentSamples.ItemsByType.Values.Where(item => item.createTile == tile).ToArray();
            if (args["item_type"] == null && items.Length != 1)
                return new JsonObject { ["error"] = "item_type_required", ["tile_type"] = tile,
                    ["item_candidates"] = new JsonArray(items.Select(item => (JsonNode)new JsonObject { ["type"] = item.type, ["name"] = item.Name }).ToArray()) };
            int type = args["item_type"] == null ? items[0].type : Int(args, "item_type");
            return new JsonObject { ["item_type"] = type, ["tile_name"] = exact[0]["internal"].DeepClone(),
                ["item_inference"] = args["item_type"] == null ? "unique_placing_item" : "caller" };
        }, token);
        if (source["error"] != null) return source;
        var targets = new[] { new GatherTarget(Int(source, "item_type"), wanted,
            new[] { Text(source, "tile_name") }, Array.Empty<int>()) };
        var gathered = await Gather.Run(targets, 0, new GameGather(targets), token);
        gathered["requested_count"] = wanted;
        gathered["item_inference"] = source["item_inference"].DeepClone();
        return gathered;
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

internal sealed class AgentContext
{
    JsonObject previous;
    internal void Reset() => previous = null;

    static JsonObject Pick(JsonNode value, params string[] fields)
    {
        var result = new JsonObject();
        foreach (string field in fields)
            if (value?[field] != null) result[field] = value[field].DeepClone();
        return result;
    }

    static JsonObject Index(IEnumerable<JsonNode> items, string key)
    {
        var result = new JsonObject();
        foreach (var item in items) result[item[key].ToString()] = item.DeepClone();
        return result;
    }

    static JsonObject Changes(JsonObject current, JsonObject previous, string path, JsonArray removed)
    {
        var result = new JsonObject();
        string Path(string key) => path + "/" + key.Replace("~", "~0").Replace("/", "~1");
        foreach (var field in current)
        {
            if (previous.TryGetPropertyValue(field.Key, out var before) && JsonNode.DeepEquals(before, field.Value)) continue;
            if (field.Value is JsonObject nested && before is JsonObject old)
            {
                var changed = Changes(nested, old, Path(field.Key), removed);
                if (changed.Count > 0) result[field.Key] = changed;
            }
            else result[field.Key] = field.Value?.DeepClone();
        }
        foreach (var field in previous.Where(field => !current.ContainsKey(field.Key))) removed.Add(Path(field.Key));
        return result;
    }

    internal JsonObject Capture(JsonObject state)
    {
        var current = (JsonObject)state.DeepClone();
        foreach (string field in new[] { "equipment", "camera", "tiles", "objects", "detected_tiles", "available_recipes", "codex_goal" })
            current.Remove(field);
        if (state["inventory_slots"] is JsonArray inventory)
            current["inventory_slots"] = Index(inventory.Where(item => item["type"]?.GetValue<int>() > 0), "slot");
        foreach (string field in new[] { "town_npcs", "enemies" })
            if (state[field] is JsonArray entities) current[field] = Index(entities, "who");
        if (state["combat"]?["active"]?.GetValue<bool>() == false)
            current["combat"] = Pick(state["combat"], "session", "active", "outcome", "directive", "target", "until_stopped");
        if (state["task_plan"] is JsonObject plan)
        {
            var brief = Pick(plan, "status", "mode", "revision", "progress_revision", "current_step", "ready_steps");
            brief["steps"] = new JsonArray((plan["steps"] as JsonArray ?? new JsonArray()).Select(step =>
            {
                var item = Pick(step, "id", "description", "status", "depends_on", "conditions", "workflow");
                item["script_present"] = !string.IsNullOrWhiteSpace(step["script"]?.ToString());
                item["on_failure_present"] = !string.IsNullOrWhiteSpace(step["on_failure"]?.ToString());
                return (JsonNode)item;
            }).ToArray());
            current["task_plan"] = brief;
        }
        if (state["script_workflows"] is JsonObject workflows)
        {
            var brief = new JsonObject();
            foreach (var workflow in workflows)
            {
                var item = Pick(workflow.Value, "revision", "segment", "running", "last_error");
                item["steps"] = new JsonArray((workflow.Value?["steps"] as JsonArray ?? new JsonArray())
                    .Select(step => (JsonNode)Pick(step, "id", "status")).ToArray());
                item["detail_tool"] = "script_progress";
                brief[workflow.Key] = item;
            }
            current["script_workflows"] = brief;
        }
        if (state["action_tasks"] is JsonArray actions)
        {
            var live = actions.Where(action => action["status"]?.ToString() is "running" or "paused" or "queued").ToArray();
            current["action_tasks"] = Index((live.Length > 0 ? live : actions.TakeLast(1)).Select(action =>
            {
                var item = Pick(action, "handle", "tool", "status", "attempt", "task_node", "after_handle", "result");
                if (action["args"] is JsonObject args)
                {
                    item["args"] = args.DeepClone();
                    item["args"].AsObject().Remove("code");
                }
                return (JsonNode)item;
            }), "handle");
        }
        if (state["watch"] is JsonObject watch)
            current["watch"] = Pick(watch, "pending_count", "events", "passive_events", "radius", "health_below");
        var removed = new JsonArray();
        var result = new JsonObject { ["mode"] = previous == null ? "snapshot" : "delta", ["format"] = "host_state_v2",
            ["state"] = previous == null ? current.DeepClone() : Changes(current, previous, "", removed), ["removed"] = removed };
        if (previous == null)
            result["detail"] = "自动现场state递归合并对象，数组整体替换，null表示清空值；removed按JSON Pointer删除路径，~1还原/，~0还原~。inventory_slots按slot、town_npcs/enemies按who、action_tasks按handle索引。get_state仍返回原始数组。完整现场用get_state，动作详情用action_query，脚本进度用script_progress。";
        previous = current;
        return result;
    }
}
