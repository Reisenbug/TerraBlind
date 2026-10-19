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
        foreach (string feature in new[] { "shell_tool", "unified_exec", "plugins", "apps", "multi_agent", "multi_agent_v2", "memories",
            "memory_tool", "code_mode", "view_image", "skill_search" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add($"features.{feature}=false"); }
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("features.skip_host_skill_discovery=true");
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

    async Task Ensure(string executable, string cwd, string model, string system, JsonArray tools, CancellationToken token)
    {
        string signature = executable + "\n" + cwd + "\n" + model + "\n" + system + "\n" + tools.ToJsonString();
        if (_rpc?.Closed == false && _thread != null && _signature == signature) return;
        Dispose();
        _signature = signature;
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
            if (config["config"]?["mcp_servers"] is JsonObject servers)
                foreach (var server in servers) overrides[$"mcp_servers.{server.Key}.enabled"] = false;
            var dynamicTools = new JsonArray();
            foreach (var tool in tools)
            {
                var function = tool["function"];
                dynamicTools.Add(new JsonObject { ["type"] = "function", ["name"] = function["name"].DeepClone(),
                    ["description"] = function["description"]?.DeepClone() ?? JsonValue.Create(""),
                    ["inputSchema"] = function["parameters"].DeepClone() });
            }
            var args = new JsonObject
            {
                ["cwd"] = cwd, ["ephemeral"] = true, ["environments"] = new JsonArray(),
                ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["baseInstructions"] = system,
                ["developerInstructions"] = "这是 Terraria 游戏会话。只用宿主提供的游戏工具完成任务，通过 ask 向玩家提问。不要开发代码、读写文件或使用终端。",
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
        Func<string, JsonObject, CancellationToken, Task<string>> execute, Action<string> say, CancellationToken token)
    {
        string turn = null;
        try
        {
            await Ensure(executable, cwd, model, system, tools, token);
            var started = await _rpc.Request("turn/start", new JsonObject
            {
                ["threadId"] = _thread, ["effort"] = "low",
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = input })
            }, token);
            turn = started["turn"]?["id"]?.GetValue<string>() ?? throw new IOException("Codex 没有返回执行编号。");
            var delivered = new System.Collections.Generic.HashSet<string>();
            while (true)
            {
                var message = await _rpc.Next(token);
                string method = message["method"]?.GetValue<string>();
                var args = message["params"] as JsonObject;
                if (message["id"] != null)
                {
                    if (method == "item/tool/call" && args?["threadId"]?.ToString() == _thread && args?["turnId"]?.ToString() == turn)
                        await Tool(message, tools, execute, token);
                    else await _rpc.Send(new JsonObject { ["id"] = message["id"].DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "该游戏会话不支持此请求。" } }, token);
                    continue;
                }
                if (args?["threadId"]?.ToString() != _thread) continue;
                if (args["turnId"] != null && args["turnId"].ToString() != turn) continue;
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
            throw;
        }
        catch { Dispose(); throw; }
    }

    async Task Tool(JsonObject request, JsonArray tools, Func<string, JsonObject, CancellationToken, Task<string>> execute, CancellationToken token)
    {
        string output;
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
            try { success = (JsonNode.Parse(output) as JsonObject)?["error"] == null; } catch (System.Text.Json.JsonException) { }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { success = false; output = new JsonObject { ["error"] = ex.Message }.ToJsonString(); }
        await _rpc.Send(new JsonObject { ["id"] = request["id"].DeepClone(), ["result"] = new JsonObject
        {
            ["success"] = success, ["contentItems"] = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = output })
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
