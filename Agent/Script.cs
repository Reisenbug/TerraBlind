using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jint;

namespace TerraBlind;

public static class AgentScript
{
    public static async Task<JsonObject> Run(string source, int seconds,
        Action<string, JsonObject> validate,
        Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<CancellationToken, Task> stop, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体，使用return返回结果。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 180)));
        var clock = Stopwatch.StartNew();
        var trace = new Queue<JsonNode>();
        var logs = new JsonArray();
        int calls = 0;
        bool inTool = false;
        Exception hostFailure = null;
        string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
        JsonObject Report(string error, string detail, JsonNode value = null) => new()
        {
            ["ok"] = error == null, ["error"] = error, ["detail"] = detail,
            ["value"] = value, ["tool_calls"] = calls, ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["trace"] = new JsonArray(trace.ToArray()), ["logs"] = logs
        };
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
                string wrapper = """
                    (function(call, log) {
                        delete globalThis.__gameTool;
                        delete globalThis.__gamePrint;
                        const stringify = JSON.stringify;
                        const parse = JSON.parse;
                        const tool = (name, args = {}) => parse(call(name, stringify(args)));
                        const print = value => log(stringify(value));
                        const value = (function() {
                    """ + source + """

                        })();
                        if (value && typeof value.then === 'function') throw new Error('使用同步tool调用，不支持Promise返回');
                        const result = stringify({value});
                        if (result.length > 16000) throw new Error('返回结果超过16000字符');
                        return result;
                    })(__gameTool, __gamePrint)
                    """;
                var output = engine.Evaluate(wrapper).AsString();
                if (hostFailure != null) throw hostFailure;
                budget.Token.ThrowIfCancellationRequested();
                return Report(null, "脚本执行结束；任务是否完成仍由task_step verify检查。", JsonNode.Parse(output)?["value"]?.DeepClone());
            }, token);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception error)
        {
            string cleanup = null;
            if (inTool)
            {
                try { await stop(token); }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (Exception failure) { cleanup = failure.Message; }
            }
            var result = Report(budget.IsCancellationRequested || error is TimeoutException ? "script_timeout" : "script_failed",
                Clip(error.GetType().Name + ": " + error.Message, 2000));
            if (cleanup != null) result["cleanup_error"] = cleanup;
            return result;
        }
    }
}
