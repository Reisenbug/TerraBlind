using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed class AgentConversation
{
    readonly HttpClient _http;
    readonly List<JsonObject> _history = new();
    static readonly HashSet<string> Bulky = new() { "get_state", "find_tiles", "wiki_page", "item_info", "combat_observe", "combat_start", "duel_player", "combat_update" };

    public AgentConversation(HttpClient http) => _http = http;

    public void Reset() => _history.Clear();

    public async Task RunAsync(string input, string baseUrl, string apiKey, string model,
        string system, JsonArray tools, Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Action<string> say, CancellationToken token)
    {
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = input });
        try
        {
            for (int turn = 0; turn < 40; turn++)
            {
                token.ThrowIfCancellationRequested();
                var messages = Compact(system);
                var payload = new JsonObject
                {
                    ["model"] = model, ["messages"] = messages, ["tools"] = tools.DeepClone()
                };
                string url = baseUrl.TrimEnd('/');
                if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    url += "/chat/completions";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string error = text.Replace(apiKey, "[redacted]");
                    throw new InvalidOperationException($"模型 HTTP {(int)response.StatusCode}: " + error[..Math.Min(error.Length, 1200)]);
                }
                var root = JsonNode.Parse(text);
                var message = root?["choices"]?[0]?["message"] as JsonObject
                    ?? throw new InvalidOperationException("模型响应缺少 choices[0].message。");
                var entry = new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.DeepClone() };
                var calls = message["tool_calls"] as JsonArray;
                if (calls?.Count > 0) entry["tool_calls"] = calls.DeepClone();
                _history.Add(entry);
                bool spokeViaTool = calls?.Any(call => call?["function"]?["name"]?.GetValue<string>() is "say" or "ask") == true;
                if (!spokeViaTool && message["content"] is JsonValue content && content.TryGetValue<string>(out string spoken)
                    && !string.IsNullOrWhiteSpace(VisibleText(spoken))) say(VisibleText(spoken));
                if (calls == null || calls.Count == 0) return;
                foreach (var call in calls)
                {
                    token.ThrowIfCancellationRequested();
                    string id = call?["id"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("工具调用缺少 id。");
                    string result;
                    try
                    {
                        string name = call["function"]?["name"]?.GetValue<string>();
                        var schema = tools.FirstOrDefault(tool => tool?["function"]?["name"]?.GetValue<string>() == name);
                        if (schema == null) throw new InvalidOperationException($"未知工具: {name}");
                        var args = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "{}") as JsonObject
                            ?? throw new InvalidOperationException("工具参数必须是对象。");
                        foreach (var field in schema["function"]?["parameters"]?["required"]?.AsArray() ?? new JsonArray())
                            if (args[field.GetValue<string>()] == null)
                                throw new InvalidOperationException($"缺少参数: {field.GetValue<string>()}");
                        result = await execute(name, args, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = new JsonObject { ["error"] = ex.Message }.ToJsonString(); }
                    _history.Add(ToolResult(id, result));
                }
            }
            say("本轮已执行 40 次判断，将返回宿主检查任务状态。");
        }
        finally
        {
            CompleteInterruptedCalls();
        }
    }

    public static string VisibleText(string text)
    {
        foreach (string tag in new[] { "</think>", "</thinking>" })
        {
            int end = text.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (end >= 0) text = text[(end + tag.Length)..];
        }
        return System.Text.RegularExpressions.Regex.Replace(text, "<think(?:ing)?>.*?</think(?:ing)?>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
    }

    static JsonObject ToolResult(string id, string result) => new()
    {
        ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result
    };

    void CompleteInterruptedCalls()
    {
        var answered = _history.Where(m => m["role"]?.GetValue<string>() == "tool")
            .Select(m => m["tool_call_id"]?.GetValue<string>()).ToHashSet();
        var missing = _history.Where(m => m["tool_calls"] is JsonArray)
            .SelectMany(m => m["tool_calls"].AsArray())
            .Select(call => call?["id"]?.GetValue<string>())
            .Where(id => id != null && !answered.Contains(id)).ToArray();
        foreach (string id in missing)
            _history.Add(ToolResult(id, "{\"status\":\"interrupted\"}"));
    }

    JsonArray Compact(string system)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        var names = new Dictionary<string, string>();
        var latest = new Dictionary<string, int>();
        for (int i = 0; i < _history.Count; i++)
        {
            var message = _history[i];
            if (message["tool_calls"] is JsonArray calls)
                foreach (var call in calls)
                    names[call["id"].GetValue<string>()] = call["function"]["name"].GetValue<string>();
            if (message["role"]?.GetValue<string>() == "tool" &&
                names.TryGetValue(message["tool_call_id"].GetValue<string>(), out string name) && Bulky.Contains(name))
                latest[name] = i;
        }
        for (int i = 0; i < _history.Count; i++)
        {
            var message = (JsonObject)_history[i].DeepClone();
            if (message["role"]?.GetValue<string>() == "tool" &&
                names.TryGetValue(message["tool_call_id"].GetValue<string>(), out string name) &&
                latest.TryGetValue(name, out int last) && i != last)
                message["content"] = "[旧结果已省略，需要就重新查询]";
            messages.Add(message);
        }
        return messages;
    }
}
