using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public static class ScriptDecision
{
    public static async Task<JsonObject> Judge(HttpClient http, string key, JsonObject args, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) return new JsonObject { ["error"] = "jev_key_missing" };
        var questions = args["questions"]?.DeepClone() as JsonObject;
        if (questions == null || questions.Count is < 1 or > 8)
            throw new ArgumentException("questions需要1..8个命名判断。");
        foreach (var question in questions)
        {
            if (question.Value is not JsonObject value || string.IsNullOrWhiteSpace(value["instructions"]?.ToString()))
                throw new ArgumentException("每个判断需要instructions。");
            string type = value["type"]?.ToString();
            if (type is not ("choice" or "noul")) throw new ArgumentException("仅支持choice/noul判断。");
            if (type == "choice" && (value["criteria"] is not JsonObject criteria || criteria.Count is < 2 or > 64))
                throw new ArgumentException("choice需要2..64个命名候选criteria。");
        }
        if (args["state"] is not JsonObject) throw new ArgumentException("state需要包含本次判断事实的对象。");
        var payload = new JsonObject { ["model"] = "jev-latest", ["state"] = args["state"].DeepClone(), ["questions"] = questions };
        if (payload.ToJsonString().Length > 24000) throw new ArgumentException("Jev判断输入超过24000字符，请缩小场景。");
        DiagLog.Write("[script_decision_request] " + payload.ToJsonString());
        var clock = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                return new JsonObject { ["error"] = "jev_http_error", ["status"] = (int)response.StatusCode, ["elapsed_ms"] = clock.ElapsedMilliseconds };
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
            if (body?["answers"] is not JsonObject answers) throw new ArgumentException("Jev未返回answers。");
            foreach (var question in questions)
            {
                var answer = answers[question.Key] as JsonObject;
                string type = question.Value["type"].ToString();
                if (answer == null || answer["type"]?.ToString() != type) throw new ArgumentException("Jev缺少对应类型的回答。");
                if (type == "choice" && (answer["choice"] == null || !question.Value["criteria"].AsObject().ContainsKey(answer["choice"].ToString())))
                    throw new ArgumentException("Jev返回了候选集外的答案。");
                if (type == "noul" && (answer["noul"] is not JsonValue number || !number.TryGetValue<double>(out double probability)
                    || !double.IsFinite(probability) || probability < 0 || probability > 1))
                    throw new ArgumentException("Jev返回了无效概率。");
            }
            var result = new JsonObject { ["answers"] = answers.DeepClone(), ["elapsed_ms"] = clock.ElapsedMilliseconds,
                ["model"] = body["model"]?.DeepClone() };
            DiagLog.Write("[script_decision_result] " + result.ToJsonString());
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new JsonObject { ["error"] = "jev_timeout", ["elapsed_ms"] = clock.ElapsedMilliseconds }; }
    }
}

public sealed class ScriptDecisionChannels
{
    sealed class Channel
    {
        public string Context, Questions;
        public long Started, AnswerStarted, CompletedAt, AnswerCompletedAt, Sequence;
        public CancellationTokenSource Lifetime = new();
        public Task<JsonObject> Pending;
        public JsonObject Input, Answer;
    }

    readonly object gate = new();
    readonly Dictionary<string, Channel> channels = new();
    long sequence;

    public JsonObject Poll(JsonObject args, Func<JsonObject, CancellationToken, Task<JsonObject>> judge)
    {
        string name = args["channel"]?.GetValue<string>();
        string context = args["context"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || string.IsNullOrWhiteSpace(context) || context.Length > 512)
            throw new ArgumentException("后台判断需要channel(1..80字符)和context(1..512字符)。");
        if (args["state"] is not JsonObject || args["questions"] is not JsonObject questions)
            throw new ArgumentException("后台判断需要state和questions。");
        int interval = args["interval_ms"]?.GetValue<int>() ?? 500;
        int maxAge = args["max_age_ms"]?.GetValue<int>() ?? 2000;
        if (interval is < 100 or > 30000 || maxAge is < 100 or > 30000)
            throw new ArgumentException("interval_ms和max_age_ms需要100..30000。");
        string shape = questions.ToJsonString();
        lock (gate)
        {
            if (channels.TryGetValue(name, out var current) && (current.Context != context || current.Questions != shape))
            { Retire(current); channels.Remove(name); current = null; }
            if (current == null)
            {
                if (channels.Count >= 8) throw new InvalidOperationException("整项任务最多8个后台决策通道。");
                channels[name] = current = new Channel { Context = context, Questions = shape };
            }
            if (current.Pending?.IsCompleted == true)
            {
                JsonObject answer;
                try { answer = current.Pending.GetAwaiter().GetResult(); }
                catch (Exception error) { answer = new JsonObject { ["error"] = "jev_request_failed", ["detail"] = error.GetBaseException().Message }; }
                current.Answer = new JsonObject { ["request_id"] = current.Sequence,
                    ["request_age_ms"] = Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds,
                    ["input_state"] = current.Input.DeepClone(), ["result"] = answer.DeepClone() };
                current.AnswerStarted = current.Started;
                current.AnswerCompletedAt = current.CompletedAt;
                DiagLog.Write("[script_decision_channel] " + new JsonObject { ["channel"] = name, ["context"] = context,
                    ["request_id"] = current.Sequence, ["request_age_ms"] = current.Answer["request_age_ms"].DeepClone(),
                    ["ready_wait_ms"] = current.CompletedAt == 0 ? null : Stopwatch.GetElapsedTime(current.CompletedAt).TotalMilliseconds,
                    ["result"] = answer.DeepClone() }.ToJsonString());
                current.Pending = null;
            }
            JsonObject result = null;
            if (current.Answer != null)
            {
                double age = Stopwatch.GetElapsedTime(current.AnswerStarted).TotalMilliseconds;
                if (age <= maxAge || current.Answer["result"]?["error"] != null)
                {
                    result = (JsonObject)current.Answer.DeepClone(); result["request_age_ms"] = age;
                    result["ready_wait_ms"] = current.AnswerCompletedAt == 0 ? null : Stopwatch.GetElapsedTime(current.AnswerCompletedAt).TotalMilliseconds;
                    result["status"] = result["result"]?["error"] == null ? "ready" : "failed";
                }
            }
            if (current.Pending == null && (current.Answer == null || Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds >= interval))
            {
                var request = new JsonObject { ["state"] = args["state"].DeepClone(), ["questions"] = questions.DeepClone() };
                current.Input = (JsonObject)request["state"].DeepClone();
                current.Started = Stopwatch.GetTimestamp(); current.Sequence = ++sequence;
                var lifetime = current.Lifetime.Token;
                current.CompletedAt = 0;
                current.Pending = Task.Run(async () =>
                {
                    try { return await judge(request, lifetime); }
                    finally { current.CompletedAt = Stopwatch.GetTimestamp(); }
                }, lifetime);
            }
            result ??= new JsonObject { ["status"] = "pending" };
            result["channel"] = name; result["context"] = context;
            result["in_flight"] = current.Pending != null;
            return result;
        }
    }

    static void Retire(Channel channel)
    {
        channel.Lifetime.Cancel();
        if (channel.Pending == null) channel.Lifetime.Dispose();
        else _ = channel.Pending.ContinueWith(task =>
        { _ = task.Exception; channel.Lifetime.Dispose(); }, TaskScheduler.Default);
    }

    public void Reset()
    {
        lock (gate) { foreach (var channel in channels.Values) Retire(channel); channels.Clear(); }
    }
}
