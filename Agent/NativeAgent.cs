using System;
using System.Collections.Concurrent;
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
    JsonArray _schemas;
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
        self.Cancel();
        self._pending = text;
    }

    void Cancel()
    {
        _pending = null;
        bool stopActions = _running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false;
        _cancel?.Cancel();
        if (stopActions) AgentTools.Stop();
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
        if (!Main.gameMenu && Main.LocalPlayer.active && Main.LocalPlayer.dead)
        {
            if (_running != null && !_running.IsCompleted && _cancel?.IsCancellationRequested == false)
            { Cancel(); AgentChat.Say("倒下了，这个任务先停下。"); }
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
        string cwd = System.IO.Path.Combine(Main.SavePath, "TerraBlindAgent");
        if (!useCodex && (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        { AgentChat.Say("先在模组配置里填写 Agent API Key、Base URL 和模型名。"); return; }
        if (!useCodex) { _codex?.Dispose(); _codex = null; }
        var codex = useCodex ? _codex ??= new CodexAgent() : null;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        AgentTools.NewTask();
        string system = _system + "\n回复语言风格：" + Config.I.AgentPersona;
        Status = "thinking";
        DiagLog.Write($"[agent] backend={(useCodex ? "codex" : "api")} model={(useCodex ? codexModel : model)}");
        _running = Task.Run(async () =>
        {
            try
            {
                string state = await AgentTools.State(token);
                void Say(string text) => MainQueue.Enqueue(() => { if (!token.IsCancellationRequested) AgentChat.Say(text); });
                if (useCodex)
                    await codex.RunAsync(goal + "\n现状：" + state, codexPath, cwd, codexModel, system, _schemas, AgentTools.Execute, Say, token);
                else
                    await _conversation.RunAsync(goal + "\n现状：" + state, url, key, model, system, _schemas, AgentTools.Execute, Say, token);
                await OnMain(() => { Status = "idle"; return true; }, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                string error = string.IsNullOrEmpty(key) ? ex.Message : ex.Message.Replace(key, "[redacted]");
                MainQueue.Enqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
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
        PlayerCombat.Stop();
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
