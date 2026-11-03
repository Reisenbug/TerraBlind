using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public class NativeAgent : ModSystem
{
    static readonly ConcurrentQueue<Action> MainQueue = new();
    static NativeAgent _instance;
    HttpClient _http;
    AgentConversation _conversation;
    CodexAgent _codex;
    CancellationTokenSource _cancel;
    Task _running;
    TaskCompletionSource<string> _answer;
    string _pending;
    string _system;
    JsonObject _goalOptions;
    Task _goalStopping = Task.CompletedTask;
    Task _dangerDelivery = Task.CompletedTask;
    int _lastDangerNotice;
    internal static JsonObject GoalState => _instance?._codex?.GoalState;
    DateTime _lastFeedback;
    DateTime _lastWatch;
    JsonArray _schemas;
    readonly ConcurrentQueue<string> _combatMessages = new();
    internal static bool CombatMessagesPending => _instance?._combatMessages.IsEmpty == false;
    internal static JsonArray TakeCombatMessages()
    {
        var messages = new JsonArray();
        if (_instance != null)
            while (_instance._combatMessages.TryDequeue(out string message)) messages.Add(message);
        return messages;
    }
    public static string Status { get; private set; } = "idle";
    internal static bool Processing => _instance?._running?.IsCompleted == false
        && _instance._cancel?.IsCancellationRequested == false && _instance._answer == null;

    internal static JsonArray ObserveTiles(Player player)
    {
        var result = new JsonArray();
        var watch = AgentTools.Watches;
        if (!watch.Subscribed("nearby_tile")) return result;
        int px = (int)(player.Center.X / 16), py = (int)(player.Center.Y / 16), radius = watch.Radius;
        for (int x = Math.Max(0, px - radius); x <= Math.Min(Main.maxTilesX - 1, px + radius); x++)
            for (int y = Math.Max(0, py - radius); y <= Math.Min(Main.maxTilesY - 1, py + radius); y++)
            {
                int distance = Math.Abs(x - px) + Math.Abs(y - py);
                if (distance > radius) continue;
                var tile = Main.tile[x, y];
                if (!tile.HasTile || !watch.WatchesTile(tile.TileType)) continue;
                result.Add(new JsonObject { ["tile_type"] = (int)tile.TileType, ["x"] = x, ["y"] = y,
                    ["frame_x"] = (int)tile.TileFrameX, ["frame_y"] = (int)tile.TileFrameY,
                    ["distance_tiles_manhattan"] = distance, ["liquid_amount"] = (int)tile.LiquidAmount,
                    ["liquid_type"] = tile.LiquidType, ["route_assessed"] = false,
                    ["unit"] = "tile_cell", ["collection_verified"] = false });
            }
        return result;
    }

    public override void Load()
    {
        if (Main.dedServ) return;
        _instance = this;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _conversation = new AgentConversation(_http);
        _system = Encoding.UTF8.GetString(Mod.GetFileBytes("Agent/System.txt"));
        _schemas = JsonNode.Parse(Encoding.UTF8.GetString(Mod.GetFileBytes("Agent/Tools.json"))).AsArray();
        AgentControlSession.DescribeClaims(_schemas);
        AgentScript.DescribePrograms(_schemas);
    }

    public static void Submit(string text)
    {
        if (!AgentNetwork.CanAutomate) { AgentChat.Say("请通过 /ai <编号> task <指令> 联系 AI 玩家。"); return; }
        if (!Config.I.AgentEnabled) { AgentChat.Say("内置 agent 已关闭。"); return; }
        var self = _instance;
        if (self == null) return;
        if (string.IsNullOrWhiteSpace(text)) { AgentChat.Say("指令不能为空。"); return; }
        if (text.Trim() is "stop" or "停止") { self.Cancel(); AgentChat.Say("停下了。"); return; }
        if (text.Trim() == "status") { AgentChat.Say(Status); return; }
        bool goalCommand = text.StartsWith("/goal ", StringComparison.Ordinal) || text.StartsWith("goal ", StringComparison.Ordinal);
        if (goalCommand)
        {
            string objective = text[(text.IndexOf(' ') + 1)..].Trim();
            if (objective == "status") { AgentChat.Say(GoalState?.ToJsonString() ?? "没有Codex目标。"); return; }
            if (objective is "pause" or "stop") { self.Cancel(); AgentChat.Say("目标已停止执行，进度保留。"); return; }
            if (!Config.I.AgentUseCodex) { AgentChat.Say("/goal需要启用Codex后端。"); return; }
            if (objective == "resume")
            {
                if (self._goalOptions == null) { AgentChat.Say("没有可以恢复的目标。"); return; }
                if (GoalState?["native"]?["status"]?.ToString() is "budgetLimited" or "usageLimited" or "complete")
                { AgentChat.Say("当前目标已完成或达到额度限制；请明确建立新目标和预算。"); return; }
                self.Cancel();
                self._goalOptions["request"] = Guid.NewGuid().ToString("N");
                self._pending = self._goalOptions["objective"].ToString(); Status = "queued"; return;
            }
            long? budget = null;
            if (objective.StartsWith("--tokens ", StringComparison.Ordinal))
            {
                var parts = objective.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !long.TryParse(parts[1], out long amount) || amount <= 0)
                { AgentChat.Say("用法：/tb /goal --tokens 正整数 目标内容"); return; }
                budget = amount; objective = parts[2];
            }
            if (string.IsNullOrWhiteSpace(objective)) { AgentChat.Say("目标不能为空。"); return; }
            self.Cancel();
            self._goalOptions = new JsonObject { ["objective"] = objective, ["request"] = Guid.NewGuid().ToString("N") };
            if (budget.HasValue) self._goalOptions["tokenBudget"] = budget.Value;
            self._pending = objective; Status = "queued";
            Progress("收到长期目标，准备执行并验收。", "queued"); return;
        }
        if (self._answer != null)
        {
            self._answer.TrySetResult(text); self._answer = null;
            Status = "running"; return;
        }
        if (PvpDuel.Active && self._running?.IsCompleted == false)
        {
            self._combatMessages.Enqueue(text);
            Progress("收到，下一轮战况判断会处理这条要求；停止请直接说 stop。", "战斗指令已排队");
            return;
        }
        self.Cancel();
        self._goalOptions = null;
        self._pending = text;
        Status = "queued";
        Progress("收到指令，准备处理。", "queued");
    }

    internal static void Progress(string text, string status, bool visible = true)
    {
        Status = status;
        if (_instance != null) _instance._lastFeedback = DateTime.UtcNow;
        DiagLog.Write("[agent] " + status + " " + text);
        if (visible) AgentChat.Say(text);
    }

    void Cancel()
    {
        _pending = null;
        bool stopActions = _running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false;
        _cancel?.Cancel();
        if (_goalStopping.IsCompleted && _codex?.GoalState?["native"]?["status"]?.ToString() == "active")
        {
            var codex = _codex;
            _goalStopping = Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await codex.SuspendGoal("paused", timeout.Token); }
                catch (Exception error) { DiagLog.Write("[agent] goal pause failed: " + error.Message); }
            });
        }
        if (stopActions || PvpDuel.Active || AgentTools.Actions.HasActive || AgentTools.HasBossControlOverride) AgentTools.Stop();
        while (_combatMessages.TryDequeue(out _)) { }
        _answer?.TrySetCanceled(); _answer = null;
        Status = "idle";
    }

    public static void Stop() => _instance?.Cancel();

    void CloseCodex()
    {
        var codex = _codex; _codex = null;
        if (codex == null) return;
        if (_goalStopping.IsCompleted) codex.Dispose();
        else _ = _goalStopping.ContinueWith(_ => codex.Dispose(), TaskScheduler.Default);
    }

    public override void PostUpdateEverything()
    {
        if (Main.dedServ) return;
        if (!Config.I.AgentEnabled || !AgentNetwork.CanAutomate)
        {
            Cancel();
            CloseCodex();
            return;
        }
        while (MainQueue.TryDequeue(out var action)) action();
        if (!Main.gameMenu && (AgentTools.Watches.LocalDelivery || _running?.IsCompleted == false && _cancel?.IsCancellationRequested == false)
            && AgentTools.Watches.Armed && DateTime.UtcNow - _lastWatch >= TimeSpan.FromMilliseconds(250))
        {
            _lastWatch = DateTime.UtcNow;
            var p = Main.LocalPlayer;
            var chests = new JsonArray();
            foreach (var chest in Main.chest)
                if (chest != null && chest.item.Any(i => !i.IsAir)
                    && Math.Abs(chest.x - p.Center.X / 16) + Math.Abs(chest.y - p.Center.Y / 16) <= AgentTools.Watches.Radius)
                    chests.Add(new JsonObject { ["x"] = chest.x, ["y"] = chest.y });
            AgentTools.Watches.Observe(new JsonObject { ["hp"] = p.statLife, ["dead"] = p.dead, ["suffocating"] = p.suffocating,
                ["last_damage"] = AgentTools.LastDamage?.DeepClone(), ["last_death"] = AgentTools.LastDeath?.DeepClone(),
                ["item_counts"] = new JsonObject(p.inventory.Where(item => !item.IsAir).GroupBy(item => item.type)
                    .Select(group => new System.Collections.Generic.KeyValuePair<string, JsonNode>(group.Key.ToString(), JsonValue.Create(group.Sum(item => item.stack))))),
                ["pos"] = new JsonObject { ["x"] = p.position.X, ["y"] = p.position.Y },
                ["inventory_full"] = !p.inventory.Take(50).Any(i => i.IsAir), ["chests"] = chests,
                ["tiles"] = ObserveTiles(p),
                ["drops"] = AgentTools.Watches.Subscribed("nearby_drop") ? NearbyDrops.Read(AgentTools.Watches.Radius) : null,
                ["actions"] = AgentTools.Actions.Summary() });
            if (AgentTools.Watches.Samples % 20 == 0)
                DiagLog.Write("[agent_observe] " + AgentTools.Watches.LastObservation.ToJsonString());
            if (_codex != null && Processing && !AgentTools.Watches.LocalDelivery && _dangerDelivery.IsCompleted)
            {
                var danger = AgentTools.Watches.Pending()["events"].AsArray().LastOrDefault(e =>
                    e["id"].GetValue<int>() > _lastDangerNotice
                    && e["kind"].ToString() is "low_health" or "suffocating" or "player_died");
                if (danger != null)
                {
                    _lastDangerNotice = danger["id"].GetValue<int>();
                    var observation = new JsonObject { ["event_id"] = _lastDangerNotice, ["kind"] = danger["kind"].DeepClone(),
                        ["hp"] = p.statLife, ["max_hp"] = p.statLifeMax2, ["dead"] = p.dead,
                        ["potion_delay_ticks"] = p.potionDelay, ["active_handle"] = AgentTools.Actions.ActiveHandle,
                        ["last_damage"] = AgentTools.LastDamage?.DeepClone() };
                    _dangerDelivery = _codex.Steer("宿主实时危险观察，原目标和进度保留。请按最新现场调整尚未执行的动作；此通知不代表已经脱险："
                        + observation.ToJsonString(AgentTools.ModelJson), _cancel.Token);
                }
            }
        }
        if (Status == "后台动作执行中" && !AgentTools.Actions.HasActive) Status = "idle";
        if (PvpDuel.Active) PvpDuel.Update(Main.LocalPlayer);
        if (_running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false
            && _answer == null && DateTime.UtcNow - _lastFeedback >= TimeSpan.FromSeconds(30))
            Progress("任务仍在处理中：" + Status + "。可用 /tb stop 停止。", Status, !PvpDuel.Active);
        if (!Main.gameMenu && Main.LocalPlayer.active && Main.LocalPlayer.dead)
        {
            return;
        }
        if (_pending == null || _running != null && !_running.IsCompleted) return;
        if (Main.gameMenu || !Main.LocalPlayer.active) return;
        string goal = _pending; _pending = null;
        string key = Config.I.AgentApiKey?.Trim();
        string model = Config.I.AgentModel?.Trim();
        string url = Config.I.AgentBaseUrl?.Trim();
        bool useCodex = Config.I.AgentUseCodex;
        string codexPath = Config.I.AgentCodexPath;
        string codexModel = Config.I.AgentCodexModel?.Trim();
        if (string.IsNullOrEmpty(codexModel)) codexModel = "gpt-6-luna";
        string cwd = System.IO.Path.Combine(Main.SavePath, "TerraBlindAgent");
        var goalOptions = _goalOptions?.DeepClone() as JsonObject;
        if (!useCodex && (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        { Progress("先在模组配置里填写 Agent API Key、Base URL 和模型名。", "failed"); return; }
        if (!useCodex) CloseCodex();
        var codex = useCodex ? _codex ??= new CodexAgent() : null;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        AgentTools.NewTask();
        _lastDangerNotice = 0;
        string system = AgentScript.Instructions(_system + "\n回复语言风格：" + Config.I.AgentPersona, _schemas);
        var entrySchemas = AgentScript.EntryTools(_schemas);
        Progress(useCodex ? "正在连接 Codex 并等待响应。" : "正在请求模型。", "等待模型响应");
        DiagLog.Write($"[agent] backend={(useCodex ? "codex" : "api")} model={(useCodex ? codexModel : model)} api_revision={AgentTools.ApiRevision} assembly={typeof(NativeAgent).Assembly.ManifestModule.ModuleVersionId}");
        _running = Task.Run(async () =>
        {
            try
            {
                await _goalStopping.WaitAsync(token);
                string state = await AgentTools.State(token);
                void Say(string text) => MainQueue.Enqueue(() => { if (!token.IsCancellationRequested) AgentChat.Say(text); });
                string input = goal + "\n现状：" + state;
                long recoverySession = -1;
                int noProgressContinuations = 0;
                int preparationContinuations = 0;
                while (true)
                {
                    int progressBefore = await OnMain(() => AgentTools.Plan.Progress, token);
                    int arrivalsBefore = AgentTools.Actions.NavigationArrivals;
                    int discoveriesBefore = await OnMain(() => AgentTools.DiscoveryRevision, token);
                    var workflowBefore = await OnMain(() => AgentTools.Workflows.ToDictionary(w => w.Key, w => w.Value.Segment), token);
                    if (useCodex)
                        await codex.RunAsync(input, codexPath, cwd, codexModel, system, entrySchemas, AgentTools.Execute, Say, token, goalOptions);
                    else
                        await _conversation.RunAsync(input, url, key, model, system, entrySchemas, AgentTools.Execute, Say, token);
                    bool scriptYielded = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.Yielded
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    bool reviewRequested = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.ReviewRequested
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    while (!reviewRequested && await OnMain(() => AgentTools.Actions.HasActive && (goalOptions != null || AgentTools.Watches.Armed)
                        && !AgentTools.Watches.HasInterrupting, token))
                        await Task.Delay(100, token);
                    if (await OnMain(() => AgentTools.Watches.HasInterrupting, token))
                    {
                        if (AgentTools.Actions.NavigationArrivals > arrivalsBefore) noProgressContinuations = 0;
                        string handle = AgentTools.Actions.ActiveHandle;
                        if (handle != null) await AgentTools.Actions.Control(handle, "pause", null, token);
                        if (goalOptions == null && await OnMain(() => AgentTools.Recovery.Exhausted, token)) break;
                        var events = await OnMain(() => AgentTools.Watches.Take(), token);
                        input = "原目标：" + goal + "\n宿主事件已暂停原后台动作并释放输入。根据实际事件调整剩余代码；已完成step不重复，uncertain先核对。"
                            + "临时参数仅属于该动作，不能放宽玩家权限；需要继续时恢复同一handle。\n事件：" + events.ToJsonString()
                            + "\n现状：" + await AgentTools.State(token);
                        continue;
                    }
                    bool died = await OnMain(() => Main.LocalPlayer.dead, token);
                    if (died && !await OnMain(() => PvpDuel.Active, token))
                    {
                        while (await OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
                        input = "原目标：" + goal + "\n刚才死亡，现已复活。依据实际现状继续未完成目标，不要重放旧坐标操作。\n现状：" + await AgentTools.State(token);
                        continue;
                    }
                    bool canContinue = await OnMain(() => AgentTools.Plan.Incomplete
                        && AgentTools.Plan.Snapshot()["status"]?.ToString() != "blocked"
                        && !PvpDuel.Active, token);
                    if (goalOptions != null && !await OnMain(() => AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure)["verified"].GetValue<bool>(), token))
                        canContinue = await OnMain(() => AgentTools.Plan.Snapshot()["status"]?.ToString() != "blocked", token);
                    bool progressed = await OnMain(() => AgentTools.Plan.Progress > progressBefore, token)
                        || AgentTools.Actions.NavigationArrivals > arrivalsBefore;
                    if (AgentTools.Actions.NavigationArrivals > arrivalsBefore)
                        await OnMain(() => { DiagLog.Write("[agent_progress] navigation_arrivals=" + AgentTools.Actions.NavigationArrivals); return true; }, token);
                    if (scriptYielded)
                    {
                        canContinue = true;
                    }
                    if (progressed) { noProgressContinuations = 0; preparationContinuations = 0; }
                    if (await OnMain(() => AgentTools.Recovery.Exhausted, token)) canContinue = false;
                    if (canContinue && await OnMain(() => AgentTools.Recovery.Offer(), token))
                    {
                        input = "原目标：" + goal + "\n最近工具失败，先修正实际错误，再继续原任务。参数歧义请选返回的精确internal重试，缺来源请换区域；不能把错误打印出来就结束。"
                            + "长导航不因耗时失败，不重复启动已有句柄。修复最多3轮，同一失败参数最多3次。\n失败证据与完整候选："
                            + (await OnMain(() => AgentTools.Recovery.Snapshot(), token)).ToJsonString() + "\n现状：" + await AgentTools.State(token);
                        continue;
                    }
                    bool discovered = await OnMain(() => AgentTools.DiscoveryRevision > discoveriesBefore, token);
                    if (canContinue && (progressed || discovered && preparationContinuations++ < 4 || noProgressContinuations++ < 1))
                    {
                        input = "原目标：" + goal + "\n计划尚未完成或脚本主动yield，请结合script_workflows和当前状态续写剩余代码。不要重做已完成step；uncertain步骤先核对。不能只建立计划或说准备行动就结束；无可执行办法则明确报告阻塞，不再yield空转。新查证资料仅允许最多4轮准备续接，重复查询不增加准备机会；没有新资料或实际进展时仅额外续接一次。请执行采集、移动或制作。\n现状："
                            + await AgentTools.State(token);
                        continue;
                    }
                    if (goalOptions != null && !await OnMain(() => AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure)["verified"].GetValue<bool>(), token))
                    {
                        await OnMain(() => { Progress("目标未完成，继续调整方案。", "目标续接中", false); return true; }, token);
                        await Task.Delay(5000, token);
                        input = "原目标：" + goal + "\n目标尚未通过宿主验收，继续执行。不要结束等待玩家resume。"
                            + "已有后台动作则等待它或处理真实事件；已失败动作依据最新证据换方案，不重放相同失败参数。"
                            + "当前步骤blocked不等于永久放弃：调查实际阻塞，修复后继续原计划，不清空既有进度。"
                            + "普通探索按已有权限处理可达障碍；只有必须由玩家决定的问题才ask。\n现状：" + await AgentTools.State(token);
                        continue;
                    }
                    JsonObject report;
                    while (true)
                    {
                        report = await OnMain(() =>
                        {
                            if (!PvpDuel.Active) return new JsonObject { ["ended"] = true };
                            Status = "等待战斗指令（不调用模型）";
                            bool requested = CombatMessagesPending;
                            if (!requested && (!PvpDuel.RecoveryRequired || recoverySession == PvpDuel.Session)) return null;
                            if (!requested) recoverySession = PvpDuel.Session;
                            var current = PvpDuel.Report();
                            current["player_messages"] = TakeCombatMessages();
                            return current;
                        }, token);
                        if (report != null) break;
                        await Task.Delay(250, token);
                    }
                    if (report["ended"] != null) break;
                    input = "原目标：" + goal + "\n按玩家补充或本会话唯一一次自动受阻求助处理。完成具体操作或说明限制后结束本轮，不轮询、不等待、不重启战斗；Jev会继续控制。\n战况：" + report.ToJsonString();
                }
                if (goalOptions != null)
                {
                    var final = await codex.CompleteGoal(ct => OnMain(() =>
                    {
                        var verified = AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure);
                        if (AgentTools.Actions.HasActive) verified["verified"] = false;
                        return verified;
                    }, ct), token);
                    if (final?["verified"]?.GetValue<bool>() != true)
                    {
                        string handle = AgentTools.Actions.ActiveHandle;
                        if (handle != null) await AgentTools.Actions.Control(handle, "pause", null, token);
                        if (await OnMain(() => AgentTools.Recovery.Exhausted || AgentTools.Plan.Snapshot()["status"]?.ToString() == "blocked", token))
                            await codex.SuspendGoal("blocked", token);
                    }
                    await OnMain(() => { DiagLog.Write("[agent] goal result " + final?.ToJsonString()); return true; }, token);
                }
                await OnMain(() =>
                {
                    if (AgentTools.Plan.Incomplete || AgentTools.Workflows.Values.Any(w => w.Yielded)
                        || goalOptions != null && codex.GoalState?["native"]?["status"]?.ToString() != "complete")
                    {
                        if (goalOptions != null)
                            Progress("本轮执行未完成，已停止空转；目标与进度保留，等待修复或 /tb /goal resume。", "needs_repair");
                        else Progress("本轮已结束，但长任务尚未验证完成；进度已保留，可发指令继续。", "paused");
                    }
                    else Status = AgentTools.Actions.HasActive ? "后台动作执行中" : "idle";
                    if (!AgentTools.Actions.HasActive && !AgentTools.Plan.Incomplete) AgentTools.Watches.End();
                    return true;
                }, token);
            }
            catch (OperationCanceledException) { }
            catch (GoalBudgetExceededException error)
            {
                await OnMain(() => { AgentTools.Stop(); Progress(error.Message, "budgetLimited"); return true; }, default);
            }
            catch (Exception ex)
            {
                string error = string.IsNullOrEmpty(key) ? ex.Message : ex.Message.Replace(key, "[redacted]");
                MainQueue.Enqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    AgentTools.Plan.Record("host_failure", error);
                    AgentTools.Stop(); Status = "failed";
                    DiagLog.Write("[agent] " + error);
                    AgentChat.Say("这次没执行完：" + error);
                });
            }
        });
    }

    internal static Task<T> OnMain<T>(Func<T> action, CancellationToken token)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => result.TrySetCanceled(token));
        MainQueue.Enqueue(() =>
        {
            try
            {
                if (!token.IsCancellationRequested) result.TrySetResult(action());
            }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { registration.Dispose(); }
        });
        return result.Task;
    }

    internal static void ValidateTool(string name, JsonObject args)
    {
        var schema = _instance?._schemas?.FirstOrDefault(t => t["function"]?["name"]?.ToString() == name);
        if (schema == null) throw new ArgumentException("未知游戏工具：" + name);
        foreach (var field in schema["function"]?["parameters"]?["required"]?.AsArray() ?? new JsonArray())
            if (args[field.ToString()] == null)
            {
                var error = new ArgumentException("工具缺少必填参数：" + field);
                error.Data["tool_schema"] = schema["function"].DeepClone();
                throw error;
            }
    }

    internal static JsonObject DescribeApi(JsonObject args) => AgentScript.DescribeApi(_instance._schemas, args);

    internal static async Task<string> Ask(string question, CancellationToken token)
    {
        Task<string> answer = await OnMain(() =>
        {
            _instance._answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Status = "waiting for answer";
            AgentChat.Say(question);
            return _instance._answer.Task;
        }, token);
        return await answer.WaitAsync(token);
    }

    public override void OnWorldUnload()
    {
        Cancel();
        AgentTools.Plan.Reset();
        AgentTools.Workflows.Clear();
        AgentTools.Actions.Reset();
        AgentTools.Control.Reset();
        AgentTools.ControlSession.Reset();
        AgentTools.LastDamage = AgentTools.LastDeath = AgentTools.LastContactEscape = null;
        AgentTools.ContactEscapeActive = false;
        CombatLoadout.PreferredType = 0;
        AgentTaskEvidence.Reset();
        PlayerCombat.Stop();
        PvpDuel.Stop();
        CloseCodex();
        _goalOptions = null;
        if (_http != null) _conversation = new AgentConversation(_http);
        AgentPermissions.Reset();
    }

    public override void Unload()
    {
        Cancel();
        CloseCodex();
        _http?.Dispose();
        _instance = null;
        while (MainQueue.TryDequeue(out _)) { }
    }
}

public static class AgentPermissions
{
    static bool dodge = true, attack = true;
    public static bool CanDodge(bool delegated = false) => dodge && AgentTools.ControlAllows(ControlBehavior.Dodge, ControlChannels.Movement, delegated);
    public static bool CanAttack(bool delegated = false) => attack && AgentTools.ControlAllows(ControlBehavior.Attack, ControlChannels.ItemUse, delegated);
    public static bool Dodge { get => CanDodge(); set => dodge = value; }
    public static bool Attack { get => CanAttack(); set => attack = value; }
    public static void Reset() { dodge = attack = true; }
}
