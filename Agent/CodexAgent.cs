using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TerraBlind;

internal static class AgentImages
{
    static readonly ConcurrentDictionary<string, byte[]> images = new();
    static readonly ConcurrentQueue<string> order = new();
    internal static string Register(byte[] png)
    {
        if (png.Length is < 8 or > 4_000_000 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("地图图片需要4MB以内的PNG。");
        string id = Guid.NewGuid().ToString("N"); images[id] = png; order.Enqueue(id);
        while (order.Count > 16 && order.TryDequeue(out string old)) images.TryRemove(old, out _);
        return id;
    }
    internal static string Take(JsonNode result)
        => result?["image_id"]?.ToString() is string id && images.TryRemove(id, out var png)
            ? "data:image/png;base64," + Convert.ToBase64String(png) : null;
}

public sealed class CodexConnection : IDisposable
{
    readonly TextReader _reader;
    readonly TextWriter _writer;
    readonly Action _stop;
    readonly CancellationTokenSource _lifetime = new();
    readonly SemaphoreSlim _write = new(1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>();
    long _id;
    public bool Closed { get; private set; }

    public CodexConnection(TextReader reader, TextWriter writer, Action stop)
    {
        _reader = reader; _writer = writer; _stop = stop;
        _ = Read();
    }

    async Task Read()
    {
        Exception error = new IOException("Codex 后台连接已关闭。");
        try
        {
            while (await _reader.ReadLineAsync(_lifetime.Token) is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var message = JsonNode.Parse(line).AsObject();
                if (message["method"] != null) _events.Writer.TryWrite(message);
                else if (message["id"] is JsonValue id && id.TryGetValue<long>(out long number)
                    && _pending.TryRemove(number, out var result)) result.TrySetResult(message);
            }
        }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) error = new IOException("Codex 协议连接失败。", ex); }
        finally
        {
            Closed = true;
            foreach (var pair in _pending) pair.Value.TrySetException(error);
            _events.Writer.TryComplete(error);
        }
    }

    public async Task Send(JsonObject message, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try
        {
            if (Closed) throw new IOException("Codex 后台连接已关闭。");
            await _writer.WriteLineAsync(message.ToJsonString().AsMemory(), token);
            await _writer.FlushAsync(token);
        }
        finally { _write.Release(); }
    }

    public async Task<JsonObject> Request(string method, JsonObject args, CancellationToken token)
    {
        long id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args }, token);
            var reply = await completion.Task.WaitAsync(token);
            if (reply["error"] != null) throw new InvalidOperationException("Codex: " + reply["error"]?["message"]);
            return reply["result"] as JsonObject ?? new JsonObject();
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public ValueTask<JsonObject> Next(CancellationToken token) => _events.Reader.ReadAsync(token);

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _stop();
        _reader.Dispose();
        try { _writer.Dispose(); } catch (IOException) { }
    }
}

public sealed class CodexAgent : IDisposable
{
    readonly Func<string, string, CodexConnection> _connect;
    CodexConnection _rpc;
    string _thread, _signature;
    volatile string _activeTurn;
    JsonObject _usage;
    JsonObject _goal;
    readonly object _goalGate = new();
    string _goalRequest;
    long _goalTokenBaseline;
    readonly JsonArray _goalHistory = new();
    public JsonObject GoalState
    {
        get
        {
            lock (_goalGate) return _goal == null ? null : new JsonObject { ["native"] = _goal.DeepClone(),
                ["host_tokens_used"] = GoalTokens, ["history"] = _goalHistory.DeepClone(),
                ["scheduler"] = "game_host", ["completion_authority"] = "verified_game_state" };
        }
    }
    long GoalTokens => Math.Max(0, (_usage?["total"]?["totalTokens"]?.GetValue<long>() ?? 0) - _goalTokenBaseline);
    public JsonObject TokenUsage => (JsonObject)_usage?.DeepClone();
    public int UsageUpdates { get; private set; }
    public int TurnsStarted { get; private set; }

    internal async Task<bool> Steer(string observation, CancellationToken token)
    {
        var rpc = _rpc;
        string turn = _activeTurn, thread = _thread;
        if (turn == null || rpc == null || rpc.Closed || token.IsCancellationRequested) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await rpc.Request("turn/steer", new JsonObject { ["threadId"] = thread, ["expectedTurnId"] = turn,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = observation }) }, deadline.Token);
            DiagLog.Write("[agent_danger] steering accepted turn=" + turn);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        { DiagLog.Write("[agent_danger] steering not accepted: " + error.Message); return false; }
    }

    public CodexAgent(Func<string, string, CodexConnection> connect = null) => _connect = connect ?? Launch;

    public static string ResolveExecutable(string configured)
    {
        configured = string.IsNullOrWhiteSpace(configured) ? "codex" : configured.Trim();
        if (configured != "codex") return configured;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] known = { Environment.GetEnvironmentVariable("CODEX_HUD_CODEX_BIN"),
            Path.Combine(home, ".npm-global", "bin", "codex"), "/opt/homebrew/bin/codex", "/usr/local/bin/codex" };
        foreach (string path in known) if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string path = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("未找到 Codex。请先安装并执行 codex login，或填写 Codex 可执行文件路径。");
    }

    static CodexConnection Launch(string executable, string cwd)
    {
        Directory.CreateDirectory(cwd);
        var start = new ProcessStartInfo(ResolveExecutable(executable))
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("features.goals=true");
        foreach (string feature in new[] { "shell_tool", "unified_exec", "plugins", "apps", "multi_agent", "multi_agent_v2", "memories",
            "memory_tool", "code_mode", "view_image", "skill_search" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add($"features.{feature}=false"); }
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("features.skip_host_skill_discovery=true");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("skills.include_instructions=false");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("project_doc_max_bytes=0");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("web_search=\"disabled\"");
        if (!OperatingSystem.IsWindows()) start.Environment["PATH"] = (start.Environment["PATH"] ?? "") + ":/usr/local/bin:/opt/homebrew/bin";
        ApplySystemProxy(start, HttpClient.DefaultProxy);
        var process = Process.Start(start) ?? throw new IOException("无法启动 Codex。");
        _ = process.StandardError.ReadToEndAsync();
        return new CodexConnection(process.StandardOutput, process.StandardInput, () =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
        });
    }

    internal static void ApplySystemProxy(ProcessStartInfo start, IWebProxy proxy)
    {
        if (new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }
            .Any(key => start.Environment.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))) return;
        foreach (string scheme in new[] { "http", "https" })
        {
            var destination = new Uri(scheme + "://chatgpt.com");
            if (proxy.IsBypassed(destination)) continue;
            var address = proxy.GetProxy(destination);
            if (address != null && address != destination)
                start.Environment[scheme.ToUpperInvariant() + "_PROXY"] = address.AbsoluteUri;
        }
    }

    async Task Ensure(string executable, string cwd, string model, string system, JsonArray tools, CancellationToken token, bool managedGoal = false)
    {
        string signature = executable + "\n" + cwd + "\n" + model + "\n" + system + "\n" + tools.ToJsonString() + "\n" + managedGoal;
        if (_rpc?.Closed == false && _thread != null && _signature == signature) return;
        Dispose();
        _signature = signature;
        _usage = null; UsageUpdates = TurnsStarted = 0;
        lock (_goalGate) { _goal = null; _goalRequest = null; _goalHistory.Clear(); }
        _rpc = _connect(executable, cwd);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _rpc.Request("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "terrablind", ["title"] = "TerraBlind", ["version"] = "1.0" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
            }, deadline.Token);
            await _rpc.Send(new JsonObject { ["method"] = "initialized" }, deadline.Token);
            var account = await _rpc.Request("account/read", new JsonObject { ["refreshToken"] = false }, deadline.Token);
            if (account["account"] == null) throw new InvalidOperationException("Codex 尚未登录，请在终端执行 codex login。");
            var config = await _rpc.Request("config/read", new JsonObject { ["includeLayers"] = false }, deadline.Token);
            var overrides = new JsonObject();
            overrides["features.goals"] = false;
            if (config["config"]?["mcp_servers"] is JsonObject servers)
                foreach (var server in servers) overrides[$"mcp_servers.{server.Key}.enabled"] = false;
            var dynamicTools = new JsonArray();
            foreach (var tool in tools)
            {
                var function = tool["function"];
                string description = function["description"]?.ToString() ?? "";
                if (function["name"]?.ToString() is "get_state" or "game_api" or "run_script" or "ask")
                    description = "Codex exec调用返回JSON文本(string)，不是对象。直接text(await tools." + function["name"]
                        + "(参数))可读取全文；需在exec访问字段时先JSON.parse返回文本。不要对原始文本再JSON.stringify。\n" + description;
                dynamicTools.Add(new JsonObject { ["type"] = "function", ["name"] = function["name"].DeepClone(),
                    ["description"] = description,
                    ["inputSchema"] = function["parameters"].DeepClone() });
            }
            var args = new JsonObject
            {
                ["cwd"] = cwd, ["ephemeral"] = !managedGoal, ["environments"] = new JsonArray(),
                ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["baseInstructions"] = system,
                ["developerInstructions"] = "这是 Terraria 游戏会话。只用宿主提供的游戏工具完成任务，通过 ask 向玩家提问。允许通过 script_program 编写和修改游戏行为 JavaScript，通过 run_script 执行；遵守宿主提供的脚本接口和当前任务授权。不要修改模组源码、直接读写文件或使用终端。"
                    + "外层Codex exec与游戏run_script是两个执行环境：外层只有注册的tools函数，get_state/game_api/run_script返回JSON字符串。例：const s=JSON.parse(await tools.get_state({})); text(s.player.hp); 不要把字符串当对象。"
                    + "game_api列出的其余函数仅能在run_script代码里用tool(name,args)调用；不能写tools.inspect_terrain等未注册函数。游戏脚本内的tool已经返回解析后的对象，无需JSON.parse。",
                ["dynamicTools"] = dynamicTools, ["config"] = overrides
            };
            if (!string.IsNullOrWhiteSpace(model)) args["model"] = model;
            var thread = await _rpc.Request("thread/start", args, deadline.Token);
            _thread = thread["thread"]?["id"]?.GetValue<string>() ?? throw new IOException("Codex 没有返回会话编号。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { Dispose(); throw new TimeoutException("Codex 初始化超时。"); }
        catch { Dispose(); throw; }
    }

    public async Task RunAsync(string input, string executable, string cwd, string model, string system, JsonArray tools,
        Func<string, JsonObject, CancellationToken, Task<string>> execute, Action<string> say, CancellationToken token,
        JsonObject goalOptions = null)
    {
        string turn = null;
        var elapsed = Stopwatch.StartNew();
        long lastToolFinished = 0;
        try
        {
            await Ensure(executable, cwd, model, system, tools, token, goalOptions != null);
            DiagLog.Write($"[agent-timing] phase=connection_ready elapsed_ms={elapsed.ElapsedMilliseconds}");
            if (goalOptions != null)
            {
                if (_goalRequest != goalOptions["request"]?.ToString())
                {
                    var args = new JsonObject { ["objective"] = goalOptions["objective"]?.DeepClone(), ["status"] = "active", ["origin"] = "user" };
                    if (goalOptions["tokenBudget"] != null) args["tokenBudget"] = goalOptions["tokenBudget"].DeepClone();
                    if (_goal == null || _goal["objective"]?.ToString() != goalOptions["objective"]?.ToString()
                        || _goal["status"]?.ToString() == "complete")
                        _goalTokenBaseline = _usage?["total"]?["totalTokens"]?.GetValue<long>() ?? 0;
                    await MutateGoal(args, token);
                    _goalRequest = goalOptions["request"]?.ToString() ?? throw new ArgumentException("goal需要request标识。");
                }
                await ReadGoal(token);
                if (_goal?["status"]?.ToString() != "active") throw new InvalidOperationException("Codex目标当前不是active：" + _goal?["status"]);
                input += "\n游戏宿主持有的Codex goal：" + GoalState.ToJsonString()
                    + "\n继续同一目标；完成只能由宿主按真实task_plan最终条件验收。不要另建或自行标完成目标。";
            }
            var started = await _rpc.Request("turn/start", new JsonObject
            {
                ["threadId"] = _thread, ["effort"] = "low",
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = input })
            }, token);
            turn = started["turn"]?["id"]?.GetValue<string>() ?? throw new IOException("Codex 没有返回执行编号。");
            _activeTurn = turn;
            TurnsStarted++;
            lastToolFinished = elapsed.ElapsedMilliseconds;
            DiagLog.Write($"[agent-timing] phase=turn_started turn={turn} elapsed_ms={lastToolFinished}");
            int toolCalls = 0;
            var delivered = new System.Collections.Generic.HashSet<string>();
            while (true)
            {
                var message = await _rpc.Next(token);
                string method = message["method"]?.GetValue<string>();
                var args = message["params"] as JsonObject;
                if (message["id"] != null)
                {
                    if (method == "item/tool/call" && args?["threadId"]?.ToString() == _thread && args?["turnId"]?.ToString() == turn)
                    {
                        long toolStarted = elapsed.ElapsedMilliseconds;
                        DiagLog.Write($"[agent-timing] phase=tool_received turn={turn} tool={args["tool"]} elapsed_ms={toolStarted} response_gap_ms={toolStarted - lastToolFinished}");
                        if (++toolCalls > 32)
                        {
                            if (goalOptions == null) throw new InvalidOperationException("本轮达到32次游戏工具调用上限；进度保留，目标未完成。");
                            await Tool(message, tools, (_, _, _) => Task.FromResult("{\"error\":\"host_turn_limit\",\"not_executed\":true,\"detail\":\"本轮工具预算已用完；目标保持active，由宿主继续下一轮，不重建计划。\"}"), token);
                            await Interrupt(turn);
                            if (_rpc == null) throw new IOException("Codex轮次交接连接已关闭。");
                            return;
                        }
                        await Tool(message, tools, execute, token);
                        lastToolFinished = elapsed.ElapsedMilliseconds;
                        DiagLog.Write($"[agent-timing] phase=tool_returned turn={turn} tool={args["tool"]} elapsed_ms={lastToolFinished} tool_ms={lastToolFinished - toolStarted}");
                    }
                    else await _rpc.Send(new JsonObject { ["id"] = message["id"].DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "该游戏会话不支持此请求。" } }, token);
                    continue;
                }
                if (args?["threadId"]?.ToString() != _thread) continue;
                if (args["turnId"] != null && args["turnId"].ToString() != turn) continue;
                if (method == "thread/tokenUsage/updated" && args["tokenUsage"] is JsonObject usage)
                {
                    if (usage["total"]?["totalTokens"]?.ToString() != _usage?["total"]?["totalTokens"]?.ToString()) UsageUpdates++;
                    _usage = (JsonObject)usage.DeepClone();
                    if (_goal?["tokenBudget"] is JsonValue limit && GoalTokens >= limit.GetValue<long>())
                    {
                        await Interrupt(turn);
                        await SuspendGoal("budgetLimited", token);
                        throw new GoalBudgetExceededException();
                    }
                }
                if (method == "error" && args["willRetry"]?.GetValue<bool>() == true)
                    say("Codex 请求暂时失败，后台正在重试。可用 /tb stop 停止。");
                if (method == "item/completed" && args["item"]?["type"]?.ToString() == "agentMessage")
                {
                    string id = args["item"]?["id"]?.ToString();
                    string text = args["item"]?["text"]?.GetValue<string>();
                    if (id != null && delivered.Add(id) && !string.IsNullOrWhiteSpace(text)) say(AgentConversation.VisibleText(text));
                }
                if (method == "turn/completed" && args["turn"]?["id"]?.ToString() == turn)
                {
                    string status = args["turn"]?["status"]?.ToString();
                    if (status != "completed") throw new InvalidOperationException("Codex 执行结束：" + status + " " + args["turn"]?["error"]?["message"]);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await Interrupt(turn);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await SuspendGoal("paused", deadline.Token); } catch { }
            throw;
        }
        catch (GoalBudgetExceededException) { throw; }
        catch
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await SuspendGoal("blocked", deadline.Token); } catch { }
            Dispose(); throw;
        }
        finally { if (_activeTurn == turn) _activeTurn = null; }
    }

    async Task<JsonObject> ReadGoal(CancellationToken token)
    {
        var result = await _rpc.Request("thread/goal/get", new JsonObject { ["threadId"] = _thread }, token);
        lock (_goalGate) _goal = result["goal"] as JsonObject;
        return GoalState;
    }

    async Task<JsonObject> MutateGoal(JsonObject args, CancellationToken token)
    {
        args["threadId"] = _thread;
        var result = await _rpc.Request("thread/goal/set", args, token);
        lock (_goalGate)
        {
            _goal = result["goal"] as JsonObject ?? throw new IOException("Codex未返回goal状态。");
            _goalHistory.Add(new JsonObject { ["status"] = _goal["status"]?.DeepClone(), ["host_tokens_used"] = GoalTokens,
                ["utc"] = DateTime.UtcNow.ToString("O") });
        }
        return GoalState;
    }

    public Task<JsonObject> SuspendGoal(string status, CancellationToken token)
    {
        if (_goal == null) return Task.FromResult<JsonObject>(null);
        if (status is not ("paused" or "blocked" or "budgetLimited")) throw new ArgumentException("无效目标停止状态。");
        if (_goal["status"]?.ToString() is "complete" or "budgetLimited" or "usageLimited") return Task.FromResult(GoalState);
        return MutateGoal(new JsonObject { ["status"] = status, ["origin"] = "automatic" }, token);
    }

    public async Task<JsonObject> CompleteGoal(Func<CancellationToken, Task<JsonObject>> verify, CancellationToken token)
    {
        if (_goal == null) return null;
        if (_goal["status"]?.ToString() != "active") return new JsonObject { ["error"] = "goal_not_active", ["goal"] = GoalState };
        var evidence = await verify(token);
        if (evidence?["verified"]?.GetValue<bool>() != true)
            return new JsonObject { ["error"] = "goal_not_verified", ["evidence"] = evidence, ["goal"] = GoalState };
        await MutateGoal(new JsonObject { ["status"] = "complete", ["origin"] = "automatic" }, token);
        return new JsonObject { ["verified"] = true, ["evidence"] = evidence, ["goal"] = GoalState };
    }

    async Task Tool(JsonObject request, JsonArray tools, Func<string, JsonObject, CancellationToken, Task<string>> execute, CancellationToken token)
    {
        string output;
        string image = null;
        bool success = true;
        try
        {
            string name = request["params"]?["tool"]?.GetValue<string>();
            var schema = tools.FirstOrDefault(t => t["function"]?["name"]?.ToString() == name);
            if (schema == null) throw new InvalidOperationException("未知游戏工具：" + name);
            var args = request["params"]?["arguments"] as JsonObject ?? throw new InvalidOperationException("工具参数必须是对象。");
            foreach (var field in schema["function"]?["parameters"]?["required"]?.AsArray() ?? new JsonArray())
                if (args[field.ToString()] == null) throw new InvalidOperationException("缺少参数：" + field);
            output = await execute(name, args, token);
            try
            {
                var result = JsonNode.Parse(output);
                success = !AgentTools.Rejected(result as JsonObject);
                if (success && name == "terrain_map") image = AgentImages.Take(result);
                output = result?.ToJsonString(AgentTools.ModelJson) ?? "null";
            }
            catch (System.Text.Json.JsonException) { }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { success = false; output = new JsonObject { ["error"] = ex.Message }.ToJsonString(AgentTools.ModelJson); }
        var content = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = output });
        if (image != null) content.Add(new JsonObject { ["type"] = "inputImage", ["imageUrl"] = image });
        await _rpc.Send(new JsonObject { ["id"] = request["id"].DeepClone(), ["result"] = new JsonObject
        {
            ["success"] = success, ["contentItems"] = content
        } }, token);
    }

    async Task Interrupt(string turn)
    {
        if (_rpc == null || turn == null) { Dispose(); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await _rpc.Request("turn/interrupt", new JsonObject { ["threadId"] = _thread, ["turnId"] = turn }, timeout.Token);
            while (true)
            {
                var message = await _rpc.Next(timeout.Token);
                if (message["method"]?.ToString() == "turn/completed" && message["params"]?["turn"]?["id"]?.ToString() == turn) return;
                if (message["id"] != null) await _rpc.Send(new JsonObject { ["id"] = message["id"].DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32800, ["message"] = "任务已取消。" } }, timeout.Token);
            }
        }
        catch { Dispose(); }
    }

    public void Dispose()
    {
        _rpc?.Dispose(); _rpc = null; _thread = null; _signature = null;
    }
}

public sealed class GoalBudgetExceededException : Exception
{
    public GoalBudgetExceededException() : base("Codex目标已达到token预算，进度保留，未标记完成。") { }
}
