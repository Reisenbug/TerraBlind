using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jint;

namespace TerraBlind;

public static class AgentScript
{
    public static JsonArray EntryTools(JsonArray tools) => new(tools.Where(t =>
        t["function"]?["name"]?.ToString() is "run_script" or "get_state" or "say" or "ask")
        .Select(t => t.DeepClone()).ToArray());

    public static string Instructions(string system, JsonArray tools) =>
        "游戏操作通过run_script提交JavaScript代码，代码里用tool(name,args)调用下面的游戏API。"
        + "顶层只提供run_script、get_state、say、ask；其余API不是顶层工具，不要直接发起函数调用。"
        + "逐次行为、条件和循环应写在代码里，读取真实返回后推进；不能只登记计划或口头承诺。\n"
        + system + "\n以下是脚本内tool可调用的完整游戏API定义（name、description、parameters），不是执行结果：\n"
        + new JsonArray(tools.Where(t => t["function"]?["name"]?.ToString() is not ("run_script" or "ask"))
            .Select(t => t["function"].DeepClone()).ToArray()).ToJsonString();

    public static async Task<JsonObject> Run(string source, int seconds,
        Action<string, JsonObject> validate,
        Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<CancellationToken, Task> stop, CancellationToken token, ScriptProgress progress = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体，使用return返回结果。");
        progress?.Begin();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 180)));
        var clock = Stopwatch.StartNew();
        var trace = new Queue<JsonNode>();
        var logs = new JsonArray();
        int calls = 0;
        bool inTool = false;
        Exception hostFailure = null;
        string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
        JsonObject Report(string error, string detail, JsonNode value = null)
        {
            var result = new JsonObject
            {
                ["ok"] = error == null, ["error"] = error, ["detail"] = detail,
                ["value"] = value, ["tool_calls"] = calls, ["elapsed_ms"] = clock.ElapsedMilliseconds,
                ["trace"] = new JsonArray(trace.ToArray()), ["logs"] = logs
            };
            progress?.End(result);
            if (progress != null) result["workflow"] = progress.Snapshot();
            return result;
        }
        try
        {
            return await Task.Run(() =>
            {
                using var engine = new Engine(options =>
                {
                    options.Strict().LimitMemory(32_000_000).MaxStatements(50000)
                        .TimeoutInterval(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 180)))
                        .CancellationToken(budget.Token);
                    options.Host.StringCompilationAllowed = false;
                    options.Constraints.MaxRecursionDepth = 64;
                    options.Constraints.MaxArraySize = 100000;
                    options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
                });
                engine.SetValue("__gameTool", new Func<string, string, string>((name, json) =>
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (name is "run_script" or "ask") throw new InvalidOperationException("脚本内禁止嵌套run_script或ask；需要决策时return交回模型。");
                    if (json == null || json.Length > 32000) throw new ArgumentException("工具参数超过32000字符。");
                    var args = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("工具参数必须是JSON对象。");
                    validate(name, args);
                    if (calls >= 100) throw hostFailure = new InvalidOperationException("脚本达到100次工具调用上限。");
                    calls++;
                    var entry = new JsonObject { ["step"] = calls, ["tool"] = name, ["args"] = Clip(json, 400) };
                    trace.Enqueue(entry);
                    if (trace.Count > 12) trace.Dequeue();
                    long started = clock.ElapsedMilliseconds;
                    inTool = true;
                    string result = execute(name, args, budget.Token).GetAwaiter().GetResult();
                    progress?.Record(name, args, result);
                    inTool = false;
                    budget.Token.ThrowIfCancellationRequested();
                    entry["elapsed_ms"] = clock.ElapsedMilliseconds - started;
                    entry["result"] = Clip(result, 800);
                    if (result.Length > 256000) throw new InvalidOperationException("工具结果超过256000字符，请缩小查询范围。");
                    try { JsonNode.Parse(result); return result; }
                    catch (System.Text.Json.JsonException) { return JsonValue.Create(result).ToJsonString(); }
                }));
                engine.SetValue("__gamePrint", new Action<string>(text =>
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (logs.Count >= 20) throw new InvalidOperationException("脚本最多print 20次。");
                    logs.Add(Clip(text ?? "null", 1000));
                }));
                engine.SetValue("__stepEnter", new Func<string, string>(id =>
                    (progress ?? throw new InvalidOperationException("step需要run_script.workflow。 ")).Enter(id).ToJsonString()));
                engine.SetValue("__stepComplete", new Action<string, string>((id, json) =>
                    progress.Complete(id, JsonNode.Parse(json)?["value"])));
                string wrapper = """
                    (function(call, log, enter, complete) {
                        delete globalThis.__gameTool;
                        delete globalThis.__gamePrint;
                        delete globalThis.__stepEnter;
                        delete globalThis.__stepComplete;
                        const stringify = JSON.stringify;
                        const parse = JSON.parse;
                        const tool = (name, args = {}) => parse(call(name, stringify(args)));
                        const print = value => log(stringify(value));
                        const step = (id, action) => {
                            const saved = parse(enter(id));
                            if (!saved.execute) return saved.value;
                            const value = action();
                            if (value && typeof value.then === 'function') throw new Error('step不支持Promise');
                            const json = stringify({value});
                            if (json.length > 8000) throw new Error('step返回值超过8000字符');
                            complete(id, json);
                            return value;
                        };
                        const value = (function() {
                    """ + source + """

                        })();
                        if (value && typeof value.then === 'function') throw new Error('使用同步tool调用，不支持Promise返回');
                        const result = stringify({value});
                        if (result.length > 16000) throw new Error('返回结果超过16000字符');
                        return result;
                    })(__gameTool, __gamePrint, __stepEnter, __stepComplete)
                    """;
                var output = engine.Evaluate(wrapper).AsString();
                if (hostFailure != null) throw hostFailure;
                budget.Token.ThrowIfCancellationRequested();
                return Report(null, "脚本执行结束；任务是否完成仍由task_step verify检查。", JsonNode.Parse(output)?["value"]?.DeepClone());
            }, token);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            progress?.End(new JsonObject { ["error"] = "cancelled", ["detail"] = "执行被取消，未完成步骤需要核对实际状态。" });
            throw new OperationCanceledException(token);
        }
        catch (Exception error)
        {
            string cleanup = null;
            if (inTool)
            {
                try { await stop(token); }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    progress?.End(new JsonObject { ["error"] = "cancelled", ["detail"] = "清理期间被取消，步骤需要核对。" });
                    throw new OperationCanceledException(token);
                }
                catch (Exception failure) { cleanup = failure.Message; }
            }
            var result = Report(budget.IsCancellationRequested || error is TimeoutException ? "script_timeout" : "script_failed",
                Clip(error.GetType().Name + ": " + error.Message, 2000));
            if (cleanup != null) result["cleanup_error"] = cleanup;
            return result;
        }
    }
}
