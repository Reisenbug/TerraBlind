using System;
using System.Collections.Concurrent;
using System.IO;
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
    static readonly ConcurrentQueue<Action> FrameQueue = new();
    static readonly ConcurrentQueue<Action> WorldFrameQueue = new();
    static NativeAgent _instance;
    HttpClient _http;
    AgentConversation _conversation;
    CodexAgent _codex;
    CancellationTokenSource _cancel;
    CancellationTokenSource _scriptWarmup;
    Task _running;
    TaskCompletionSource<string> _answer;
    string _pending;
    string _system;
    JsonObject _goalOptions;
    Task _goalStopping = Task.CompletedTask;
    Task _dangerDelivery = Task.CompletedTask;
    Task _idleReaction = Task.CompletedTask;
    internal static Task IdleReaction => _instance?._idleReaction ?? Task.CompletedTask;
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
    internal static bool WorkPending => _instance?._pending != null || Processing;
    internal static Func<TimeSpan> FrozenTime = () => TimeSpan.Zero;
    internal static int WorldWaiters;
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
        if (!AgentNetwork.CanAutomate) { AgentChat.Say("Use /ai <player> task <instruction> to contact the AI player."); return; }
        if (!Config.I.AgentEnabled) { AgentChat.Say("The built-in agent is disabled."); return; }
        var self = _instance;
        if (self == null) return;
        if (string.IsNullOrWhiteSpace(text)) { AgentChat.Say("Enter an instruction."); return; }
        if (text.Trim() is "stop" or "停止") { self.Cancel(); AgentChat.Say("Stopped."); return; }
        if (text.Trim() == "status") { AgentChat.Say(Status); return; }
        bool goalCommand = text.StartsWith("/goal ", StringComparison.Ordinal) || text.StartsWith("goal ", StringComparison.Ordinal);
        if (goalCommand)
        {
            string objective = text[(text.IndexOf(' ') + 1)..].Trim();
            if (objective == "status") { AgentChat.Say(GoalState?.ToJsonString() ?? "No Codex goal."); return; }
            if (objective is "pause" or "stop") { self.Cancel(); AgentChat.Say("Goal paused. Progress saved."); return; }
            if (!Config.I.AgentUseCodex) { AgentChat.Say("/goal requires the Codex backend."); return; }
            if (objective == "resume")
            {
                self._goalOptions ??= ReadSavedGoal();
                if (self._goalOptions == null) { AgentChat.Say("There is no goal to resume."); return; }
                if (GoalState?["native"]?["status"]?.ToString() is "budgetLimited" or "usageLimited" or "complete")
                { AgentChat.Say("The goal is complete or has reached its limit. Start a new goal with a new budget."); return; }
                self.Cancel();
                self._goalOptions["request"] = Guid.NewGuid().ToString("N");
                self._goalOptions["resume_previous"] = true;
                self._pending = self._goalOptions["objective"].ToString(); Status = "queued"; return;
            }
            long? budget = null;
            if (objective.StartsWith("--tokens ", StringComparison.Ordinal))
            {
                var parts = objective.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !long.TryParse(parts[1], out long amount) || amount <= 0)
                { AgentChat.Say("Usage: /tb /goal --tokens <positive count> <objective>"); return; }
                budget = amount; objective = parts[2];
            }
            if (string.IsNullOrWhiteSpace(objective)) { AgentChat.Say("Enter a goal."); return; }
            self.Cancel();
            self._goalOptions = new JsonObject { ["objective"] = objective, ["request"] = Guid.NewGuid().ToString("N") };
            if (budget.HasValue) self._goalOptions["tokenBudget"] = budget.Value;
            self._pending = objective; Status = "queued";
            Progress("Goal accepted.", "queued"); return;
        }
        if (self._answer != null)
        {
            self._answer.TrySetResult(text); self._answer = null;
            Status = "running"; return;
        }
        if (PvpDuel.Active && self._running?.IsCompleted == false)
        {
            self._combatMessages.Enqueue(text);
            Progress("Combat instruction queued. Use /tb stop to stop.", "combat instruction queued");
            return;
        }
        self.Cancel();
        self._goalOptions = null;
        self._pending = text;
        Status = "queued";
        Progress("Instruction accepted.", "queued");
    }

    static string SessionDirectory()
    {
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer || string.IsNullOrEmpty(Main.ActivePlayerFileData?.Path)) return null;
        string world = Main.ActiveWorldFileData is { UniqueId: var id } && id != Guid.Empty ? id.ToString("N") : Main.worldID.ToString();
        string player = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Main.ActivePlayerFileData.Path)));
        return Path.Combine(Main.SavePath, "TerraBlindAgent", "conversations", world, player);
    }

    static JsonObject ReadSavedGoal()
    {
        string directory = SessionDirectory();
        if (directory == null) return null;
        string path = Path.Combine(directory, "goal.json");
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        { DiagLog.Write("[agent] saved goal unreadable: " + error.Message); return null; }
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
        => UpdateAgent(true);

    public override void UpdateUI(Microsoft.Xna.Framework.GameTime gameTime)
    {
        if (Main.gamePaused) UpdateAgent(false);
    }

    void UpdateAgent(bool worldAdvanced)
    {
        if (Main.dedServ) return;
        if (!Config.I.AgentEnabled || !AgentNetwork.CanAutomate)
        {
            Cancel();
            CloseCodex();
            return;
        }
        int frameWaiters = FrameQueue.Count;
        for (int i = 0; i < frameWaiters && FrameQueue.TryDequeue(out var waiter); i++) waiter();
        if (worldAdvanced)
        {
            int worldWaiters = WorldFrameQueue.Count;
            for (int i = 0; i < worldWaiters && WorldFrameQueue.TryDequeue(out var waiter); i++) waiter();
        }
        while (MainQueue.TryDequeue(out var action)) action();
        bool runtimeActive = _cancel?.IsCancellationRequested == false
            && Status is not ("needs_repair" or "budgetLimited" or "failed");
        if (!Main.gameMenu && runtimeActive)
        {
            AgentTools.ContinuePreparedNode(_cancel.Token);
            AgentTools.ObserveContinuity();
            if (worldAdvanced)
            {
                AgentTools.ObserveBossControl(Processing);
                AgentTools.LootPassingChest();
            }
        }
        if (worldAdvanced && !Main.gameMenu && (AgentTools.Watches.LocalDelivery || AgentTools.Watches.HasScriptHandlers
                || runtimeActive)
            && AgentTools.Watches.Armed && DateTime.UtcNow - _lastWatch >= TimeSpan.FromMilliseconds(250))
        {
            _lastWatch = DateTime.UtcNow;
            var p = Main.LocalPlayer;
            var chests = new JsonArray();
            foreach (var chest in Main.chest)
                if (chest != null && chest.item.Any(i => !i.IsAir)
                    && Math.Abs(chest.x - p.Center.X / 16) + Math.Abs(chest.y - p.Center.Y / 16) <= AgentTools.Watches.Radius)
                    chests.Add(new JsonObject { ["x"] = chest.x, ["y"] = chest.y });
            AgentTools.Watches.Observe(new JsonObject { ["hp"] = p.statLife, ["max_hp"] = p.statLifeMax2,
                ["dead"] = p.dead, ["suffocating"] = p.suffocating,
                ["invasion"] = AgentTools.InvasionStatus(targets: false),
                ["tick"] = (long)Main.GameUpdateCount, ["honey_slowed"] = p.honeyWet && !p.ignoreWater,
                ["last_damage"] = AgentTools.LastDamage?.DeepClone(), ["last_death"] = AgentTools.LastDeath?.DeepClone(),
                ["item_counts"] = new JsonObject(p.inventory.Where(item => !item.IsAir).GroupBy(item => item.type)
                    .Select(group => new System.Collections.Generic.KeyValuePair<string, JsonNode>(group.Key.ToString(), JsonValue.Create(group.Sum(item => item.stack))))),
                ["pos"] = new JsonObject { ["x"] = p.position.X, ["y"] = p.position.Y },
                ["inventory_full"] = !p.inventory.Take(50).Any(i => i.IsAir), ["chests"] = chests,
                ["bosses"] = new JsonArray(Main.npc.Where(n => n != null && n.active && n.boss && n.life > 0
                    && Math.Abs(n.Center.X - p.Center.X) + Math.Abs(n.Center.Y - p.Center.Y) <= 200 * 16)
                    .Select(n => (JsonNode)new JsonObject { ["who"] = n.whoAmI, ["type"] = n.type,
                        ["name"] = Terraria.Lang.GetNPCNameValue(n.type), ["hp"] = n.life, ["max_hp"] = n.lifeMax,
                        ["x"] = n.Center.X / 16f, ["y"] = n.Center.Y / 16f, ["tick"] = Main.GameUpdateCount }).ToArray()),
                ["tiles"] = ObserveTiles(p),
                ["drops"] = AgentTools.Watches.Subscribed("nearby_drop") ? NearbyDrops.Read(AgentTools.Watches.Radius) : null,
                ["actions"] = AgentTools.Actions.Summary() });
            if (AgentTools.Watches.Samples % 20 == 0)
                DiagLog.Write("[agent_observe] " + AgentTools.Watches.LastObservation.ToJsonString());
            if (_codex != null && Processing && _dangerDelivery.IsCompleted)
            {
                var danger = AgentTools.Watches.Pending()["events"].AsArray().LastOrDefault(e =>
                    e["id"].GetValue<int>() > _lastDangerNotice
                    && (e["kind"].ToString() is "low_health" or "honey_slowdown" or "invasion_started" || !AgentTools.Watches.LocalDelivery
                        && e["kind"].ToString() is "suffocating" or "player_died" or "boss_appeared"));
                if (danger != null)
                {
                    _lastDangerNotice = danger["id"].GetValue<int>();
                    var observation = new JsonObject { ["event_id"] = _lastDangerNotice, ["kind"] = danger["kind"].DeepClone(),
                        ["hp"] = p.statLife, ["max_hp"] = p.statLifeMax2, ["dead"] = p.dead,
                        ["potion_delay_ticks"] = p.potionDelay, ["active_handle"] = AgentTools.Actions.ActiveHandle,
                        ["last_damage"] = AgentTools.LastDamage?.DeepClone() };
                    bool bossAppeared = danger["kind"].ToString() == "boss_appeared";
                    if (bossAppeared) observation["boss"] = danger["evidence"]?.DeepClone();
                    if (danger["kind"].ToString() == "honey_slowdown") observation["honey_slowed"] = true;
                    string instruction = bossAppeared
                        ? "宿主观察到附近Boss，原目标及进度保留。依据任务、战斗许可与现场，决定交接战斗、调整路线或继续当前行动。调整前核对当前动作句柄。现场："
                        : "宿主实时危险观察，原目标和进度保留。低血量仅通知，当前动作继续；不得单凭低血量暂停、取消、原地回血或等待审阅。仅任务明确约束死亡时按该约束处理。请在行动期间调整后续方案：";
                    _dangerDelivery = _codex.Steer(instruction + observation.ToJsonString(AgentTools.ModelJson), _cancel.Token);
                }
                else _dangerDelivery = AgentTools.NotifyExecution(_codex, _cancel.Token);
            }
        }
        if (!Main.gameMenu && runtimeActive && AgentTools.Watches.HasScriptHandlers && _idleReaction.IsCompleted)
            _idleReaction = AgentTools.ReactWhileIdle(_cancel.Token);
        if (Status == "background action running" && !AgentTools.Actions.HasActive) Status = "idle";
        if (worldAdvanced && PvpDuel.Active) PvpDuel.Update(Main.LocalPlayer);
        if (_running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false
            && _answer == null && DateTime.UtcNow - _lastFeedback >= TimeSpan.FromSeconds(30))
            Progress("Still working: " + Status + ". Use /tb stop to stop.", Status, visible: false);
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
        { Progress("Set the Agent API key, base URL, and model in mod settings.", "failed"); return; }
        if (!useCodex) CloseCodex();
        var codex = useCodex ? _codex ??= new CodexAgent() : null;
        if (codex != null)
        {
            codex.SessionDirectory = SessionDirectory();
            if (goalOptions != null && codex.SessionDirectory != null)
            {
                try { CodexAgent.WriteSessionFile(Path.Combine(codex.SessionDirectory, "goal.json"), goalOptions.ToJsonString()); }
                catch (IOException error) { DiagLog.Write("[agent] could not save goal: " + error.Message); }
            }
        }
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        AgentTools.NewTask(goal, allowPassingChestLoot: goalOptions != null);
        string safetyPolicy = AgentTools.AllowTaskRecall
            ? "skill('safety_reactions',0);"
            : "skill('safety_reactions',0,{allow_recall:false});";
        _lastDangerNotice = 0;
        string system = AgentScript.Instructions(_system + "\n回复语言风格：" + Config.I.AgentPersona, _schemas);
        var entrySchemas = AgentScript.EntryTools(_schemas);
        Progress(useCodex ? "Connecting to Codex." : "Connecting to the model.", "waiting for model", visible: false);
        DiagLog.Write($"[agent] backend={(useCodex ? "codex" : "api")} model={(useCodex ? codexModel : model)} api_revision={AgentTools.ApiRevision} assembly={typeof(NativeAgent).Assembly.ManifestModule.ModuleVersionId}");
        _running = Task.Run(async () =>
        {
            try
            {
                await _goalStopping.WaitAsync(token);
                await AgentTools.Execute("task_policy", new JsonObject { ["operation"] = "set", ["expected_revision"] = 0,
                    ["code"] = safetyPolicy }, token);
                var modelContext = new AgentContext();
                int contextRevision = 0;
                Task<bool> Covered() => OnMain(() => (AgentTools.ControlSession.PrimaryActive || AgentTools.Actions.HasPrimaryControl)
                    && !Main.LocalPlayer.dead && !AgentTools.Watches.HasModelInterrupting, token);
                async Task<string> Context()
                {
                    if (useCodex)
                    {
                        bool executing = await Covered();
                        await codex.PrepareContext(codexPath, cwd, codexModel, system, entrySchemas, goalOptions != null, token,
                            compact: executing, keepExecuting: Covered);
                    }
                    if (useCodex && contextRevision != codex.ContextRevision)
                    { modelContext.Reset(); contextRevision = codex.ContextRevision; }
                    string result = modelContext.Capture(JsonNode.Parse(await AgentTools.State(token)).AsObject()).ToJsonString(AgentTools.ModelJson);
                    DiagLog.Write($"[agent-context] chars={result.Length} revision={contextRevision}");
                    return result;
                }
                string state = await Context();
                void Say(string text) => MainQueue.Enqueue(() => { if (!token.IsCancellationRequested) AgentChat.Say(text); });
                string input = goal + (goalOptions?["resume_previous"]?.GetValue<bool>() == true
                    ? "\n已重新进入存档。先用当前游戏状态核对以前的进度，按实际缺口继续原目标。" : "")
                    + "\n现状：" + state;
                long recoverySession = -1;
                int noProgressContinuations = 0;
                int preparationContinuations = 0;
                string preparedAttempt = null;
                bool preparedDuringRespawn = false;
                while (true)
                {
                    int progressBefore = await OnMain(() => AgentTools.Plan.Progress, token);
                    int arrivalsBefore = AgentTools.Actions.NavigationArrivals;
                    int discoveriesBefore = await OnMain(() => AgentTools.DiscoveryRevision, token);
                    var workflowBefore = await OnMain(() => AgentTools.Workflows.ToDictionary(w => w.Key, w => w.Value.Segment), token);
                    if (useCodex)
                        await codex.RunAsync(input, codexPath, cwd, codexModel, system, entrySchemas, AgentTools.Execute, Say, token, goalOptions, Context,
                            Covered);
                    else
                        await _conversation.RunAsync(input, url, key, model, system, entrySchemas, AgentTools.Execute, Say, token);
                    bool scriptYielded = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.Yielded
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    bool reviewRequested = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.ReviewRequested
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    var backgroundReview = System.Diagnostics.Stopwatch.StartNew();
                    bool historyChanged = useCodex && contextRevision != codex.ContextRevision;
                    string preparingAttempt = await OnMain(() => AgentTools.PreparationContext(
                        AgentTools.Actions.ActiveAttempt, AgentTools.Actions.ActiveNode), token);
                    var preparation = await OnMain(() => (Delay: AgentTools.Plan.RequiresNodeExecution ? 0 : 15,
                        Nodes: AgentTools.Plan.PreparationNodes(AgentTools.Actions.ActiveNode, AgentTaskEvidence.Measure)), token);
                    bool prepareNext = historyChanged || preparingAttempt != null && preparingAttempt != preparedAttempt
                        && (preparingAttempt != AgentTools.PreparationNotifiedContext || preparation.Nodes.Count > 0);
                    double reviewDelay = preparation.Delay;
                    while (!reviewRequested && (!prepareNext || backgroundReview.Elapsed.TotalSeconds < reviewDelay)
                        && await OnMain(() => AgentTools.Actions.HasActive && (goalOptions != null || AgentTools.Watches.Armed)
                        && !AgentTools.Watches.HasModelInterrupting && !AgentTools.Watches.HasHealthNotice && !AgentTools.Watches.HasAdvisories, token))
                    {
                        await Task.Delay(100, token);
                        preparingAttempt = await OnMain(() => AgentTools.PreparationContext(
                            AgentTools.Actions.ActiveAttempt, AgentTools.Actions.ActiveNode), token);
                        prepareNext = historyChanged || preparingAttempt != null && preparingAttempt != preparedAttempt
                            && (preparingAttempt != AgentTools.PreparationNotifiedContext || preparation.Nodes.Count > 0);
                    }
                    if (!reviewRequested && (prepareNext
                        || await OnMain(() => AgentTools.Watches.HasHealthNotice || AgentTools.Watches.HasAdvisories, token))
                        && await OnMain(() => AgentTools.Actions.HasActive && !AgentTools.Watches.HasModelInterrupting, token))
                    {
                        preparedAttempt = preparingAttempt;
                        var healthNotices = await OnMain(AgentTools.Watches.TakeHealthNotices, token);
                        var opportunities = await OnMain(AgentTools.Watches.Advisories, token);
                        input = "并行观察：后台动作继续。补齐当前节点on_failure和后继script；独立动作的父脚本已退出时用action_queue衔接。"
                            + "\n待补齐节点：" + preparation.Nodes.ToJsonString()
                            + "\n健康通知：" + healthNotices.ToJsonString()
                            + "\n沿途机会：" + opportunities.ToJsonString(AgentTools.ModelJson)
                            + "\n现状：" + await Context();
                        await OnMain(() => { DiagLog.Write("[agent] background_review active_handle=" + AgentTools.Actions.ActiveHandle
                            + " prepare=" + prepareNext + " health=" + healthNotices.Count
                            + " opportunities=" + string.Join(",", opportunities.Select(e => e["kind"]?.ToString()))); return true; }, token);
                        continue;
                    }
                    if (await OnMain(() => AgentTools.Watches.HasModelInterrupting, token))
                    {
                        if (AgentTools.Actions.NavigationArrivals > arrivalsBefore) noProgressContinuations = 0;
                        string handle = AgentTools.Actions.ActiveHandle;
                        if (handle != null) await AgentTools.Actions.Control(handle, "pause", null, token);
                        if (goalOptions == null && await OnMain(() => AgentTools.Recovery.Exhausted, token)) break;
                        var events = await OnMain(() => AgentTools.Watches.TakeModelInterrupts(8), token);
                        input = "宿主事件已暂停原后台动作并释放输入。根据事件调整剩余代码；已完成step保留，uncertain先核对。"
                            + "临时参数仅属于该动作，不能放宽玩家权限；需要继续时恢复同一handle。\n事件：" + events.ToJsonString()
                            + "\n现状：" + await Context();
                        continue;
                    }
                    bool died = await OnMain(() => Main.LocalPlayer.dead, token);
                    if (died && !await OnMain(() => PvpDuel.Active, token))
                    {
                        if (preparedDuringRespawn)
                            while (await OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
                        preparedDuringRespawn = await OnMain(() => Main.LocalPlayer.dead, token);
                        input = preparedDuringRespawn
                            ? "继续原目标。角色正在等待复活，现在查询资源、配方和现场证据，修订未完成节点的script，准备恢复方案。实际动作在复活后核对新位置再执行。\n现状：" + await Context()
                            : "继续原目标。刚才死亡，现已复活。依据实际现状执行已准备的恢复方案。\n现状：" + await Context();
                        continue;
                    }
                    preparedDuringRespawn = false;
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
                        input = "工具失败：按证据修正参数、来源或局部方案，继续原任务。"
                            + "修复最多3轮，同一失败参数最多3次。\n失败证据："
                            + (await OnMain(() => AgentTools.Recovery.Snapshot(), token)).ToJsonString(AgentTools.ModelJson) + "\n现状：" + await Context();
                        continue;
                    }
                    bool discovered = await OnMain(() => AgentTools.DiscoveryRevision > discoveriesBefore, token);
                    if (canContinue && (progressed || discovered && preparationContinuations++ < 4 || noProgressContinuations++ < 1))
                    {
                        input = "计划未完成或脚本yield：结合当前workflow续写并执行剩余步骤，uncertain先核对。"
                            + (goalOptions == null ? "新查证资料仅允许最多4轮准备续接；没有新资料或实际进展时仅额外续接一次。"
                                : "长期goal持续续接，不因普通指令的轮数限制结束；利用recovery.failure_history改变失败方案，不只查询和空yield。")
                            + "请执行采集、移动或制作；缺少可执行办法时保留真实阻塞并调查替代方案。\n现状："
                            + await Context();
                        continue;
                    }
                    if (goalOptions != null && !await OnMain(() => AgentTools.Plan.VerifyFinal(AgentTaskEvidence.Measure)["verified"].GetValue<bool>(), token))
                    {
                        await OnMain(() => { Progress("Continuing the goal.", "continuing goal", visible: false); return true; }, token);
                        input = "目标尚未通过宿主验收，按当前阻塞调整并继续原计划。"
                            + "已有后台动作保持执行；失败按新证据换方案。"
                            + "修复blocked节点并保留既有进度。"
                            + "\n现状：" + await Context();
                        continue;
                    }
                    JsonObject report;
                    while (true)
                    {
                        report = await OnMain(() =>
                        {
                            if (!PvpDuel.Active) return new JsonObject { ["ended"] = true };
                            Status = "waiting for combat instructions";
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
                    input = "PvP新要求或本会话一次受阻求助：处理后结束本轮，Jev继续控制。\n战况：" + report.ToJsonString();
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
                    await OnMain(() =>
                    {
                        DiagLog.Write("[agent] goal result " + final?.ToJsonString());
                        if (final?["verified"]?.GetValue<bool>() == true) Progress("Goal complete. Verified in game.", "complete");
                        return true;
                    }, token);
                }
                await OnMain(() =>
                {
                    if (AgentTools.Plan.Incomplete || AgentTools.Workflows.Values.Any(w => w.Yielded)
                        || goalOptions != null && codex.GoalState?["native"]?["status"]?.ToString() != "complete")
                    {
                        if (goalOptions != null)
                            Progress("Goal needs recovery. Progress saved. Use /tb /goal resume to continue.", "needs_repair");
                        else Progress("This run ended before verification. Progress saved.", "paused");
                    }
                    else Status = AgentTools.Actions.HasActive ? "background action running" : "idle";
                    if (!AgentTools.Actions.HasActive && !AgentTools.Plan.Incomplete)
                    { AgentTools.ClearTaskPolicy(); AgentTools.Watches.End(); }
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
                    AgentChat.Say("Action stopped: " + error);
                });
            }
        });
    }

    internal static Task<T> OnMain<T>(Func<T> action, CancellationToken token)
    {
        var result = new TaskCompletionSource<T>();
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

    internal static Task NextUpdate(CancellationToken token, bool worldFrame = false)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => result.TrySetCanceled(token));
        (worldFrame ? WorldFrameQueue : FrameQueue).Enqueue(() => { result.TrySetResult(true); registration.Dispose(); });
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

    public override void OnWorldLoad()
    {
        if (!Main.dedServ) AgentTools.RefreshHousingProtection();
        if (Main.dedServ || !Config.I.AgentEnabled || _scriptWarmup != null) return;
        _scriptWarmup = new CancellationTokenSource();
        var token = _scriptWarmup.Token;
        _ = Task.Run(() => AgentScript.WarmUp(token), token);
    }

    public override void OnWorldUnload()
    {
        Cancel();
        AgentTools.ClearTaskPolicy(true);
        AgentTools.Plan.Reset();
        AgentTools.Workflows.Clear();
        AgentTools.Actions.Reset();
        AgentTools.Control.Reset();
        AgentTools.Control.ReplaceHousing(Array.Empty<(int, int, int, int)>());
        AgentTools.ControlSession.Reset();
        AgentTools.LastDamage = AgentTools.LastDeath = AgentTools.LastContactEscape = AgentTools.LastAutomaticHeal = null;
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
        _scriptWarmup?.Cancel();
        _scriptWarmup?.Dispose();
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
