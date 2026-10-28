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
    DateTime _lastFeedback;
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

    public override void Load()
    {
        if (Main.dedServ) return;
        _instance = this;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _conversation = new AgentConversation(_http);
        _system = Encoding.UTF8.GetString(Mod.GetFileBytes("Agent/System.txt"));
        _schemas = JsonNode.Parse(Encoding.UTF8.GetString(Mod.GetFileBytes("Agent/Tools.json"))).AsArray();
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
        if (stopActions || PvpDuel.Active) AgentTools.Stop();
        while (_combatMessages.TryDequeue(out _)) { }
        _answer?.TrySetCanceled(); _answer = null;
        Status = "idle";
    }

    public static void Stop() => _instance?.Cancel();

    public override void PostUpdateEverything()
    {
        if (Main.dedServ) return;
        if (!Config.I.AgentEnabled || !AgentNetwork.CanAutomate)
        {
            Cancel();
            _codex?.Dispose(); _codex = null;
            return;
        }
        while (MainQueue.TryDequeue(out var action)) action();
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
        if (!useCodex && (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        { Progress("先在模组配置里填写 Agent API Key、Base URL 和模型名。", "failed"); return; }
        if (!useCodex) { _codex?.Dispose(); _codex = null; }
        var codex = useCodex ? _codex ??= new CodexAgent() : null;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        AgentTools.NewTask();
        string system = AgentScript.Instructions(_system + "\n回复语言风格：" + Config.I.AgentPersona, _schemas);
        var entrySchemas = AgentScript.EntryTools(_schemas);
        Progress(useCodex ? "正在连接 Codex 并等待响应。" : "正在请求模型。", "等待模型响应");
        DiagLog.Write($"[agent] backend={(useCodex ? "codex" : "api")} model={(useCodex ? codexModel : model)}");
        _running = Task.Run(async () =>
        {
            try
            {
                string state = await AgentTools.State(token);
                void Say(string text) => MainQueue.Enqueue(() => { if (!token.IsCancellationRequested) AgentChat.Say(text); });
                string input = goal + "\n现状：" + state;
                long recoverySession = -1;
                int noProgressContinuations = 0;
                while (true)
                {
                    int progressBefore = await OnMain(() => AgentTools.Plan.Progress, token);
                    var workflowBefore = await OnMain(() => AgentTools.Workflows.ToDictionary(w => w.Key, w => w.Value.Segment), token);
                    int completedBefore = await OnMain(() => AgentTools.Workflows.Values.Sum(w => w.Completed), token);
                    if (useCodex)
                        await codex.RunAsync(input, codexPath, cwd, codexModel, system, entrySchemas, AgentTools.Execute, Say, token);
                    else
                        await _conversation.RunAsync(input, url, key, model, system, entrySchemas, AgentTools.Execute, Say, token);
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
                    bool progressed = await OnMain(() => AgentTools.Plan.Progress > progressBefore, token);
                    bool scriptYielded = await OnMain(() => AgentTools.Workflows.Any(w => w.Value.Yielded
                        && w.Value.Segment > (workflowBefore.TryGetValue(w.Key, out int previous) ? previous : 0)), token);
                    if (scriptYielded)
                    {
                        canContinue = true;
                        progressed |= await OnMain(() => AgentTools.Workflows.Values.Sum(w => w.Completed) > completedBefore, token);
                    }
                    if (progressed) noProgressContinuations = 0;
                    if (canContinue && (progressed || noProgressContinuations++ < 1))
                    {
                        input = "原目标：" + goal + "\n计划尚未完成或脚本主动yield，请结合script_workflows和当前状态续写剩余代码。不要重做已完成step；uncertain步骤先核对。不能只建立计划或说准备行动就结束；无可执行办法则明确报告阻塞，不再yield空转。未取得实际进展时宿主仅额外续接一次。\n现状："
                            + await AgentTools.State(token);
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
                await OnMain(() =>
                {
                    if (AgentTools.Plan.Incomplete || AgentTools.Workflows.Values.Any(w => w.Yielded))
                        Progress("本轮已结束，但长任务尚未验证完成；进度已保留，可发指令继续。", "paused");
                    else Status = "idle";
                    return true;
                }, token);
            }
            catch (OperationCanceledException) { }
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
            if (args[field.ToString()] == null) throw new ArgumentException("工具缺少必填参数：" + field);
    }

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
        AgentTaskEvidence.Reset();
        PlayerCombat.Stop();
        PvpDuel.Stop();
        _codex?.Dispose(); _codex = null;
        if (_http != null) _conversation = new AgentConversation(_http);
        AgentPermissions.Reset();
    }

    public override void Unload()
    {
        Cancel();
        _codex?.Dispose(); _codex = null;
        _http?.Dispose();
        _instance = null;
        while (MainQueue.TryDequeue(out _)) { }
    }
}

public static class AgentPermissions
{
    public static bool Dodge = true;
    public static bool Attack = true;
    public static void Reset() { Dodge = Attack = true; }
}
