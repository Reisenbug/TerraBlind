using System;
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
            if (type == "choice" && (value["criteria"] is not JsonObject criteria || criteria.Count is < 2 or > 16))
                throw new ArgumentException("choice需要2..16个命名候选criteria。");
        }
        if (args["state"] is not JsonObject) throw new ArgumentException("state需要包含本次判断事实的对象。");
        var payload = new JsonObject { ["model"] = "jev-latest", ["state"] = args["state"].DeepClone(), ["questions"] = questions };
        if (payload.ToJsonString().Length > 24000) throw new ArgumentException("Jev判断输入超过24000字符，请缩小场景。");
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
            return new JsonObject { ["answers"] = answers.DeepClone(), ["elapsed_ms"] = clock.ElapsedMilliseconds,
                ["model"] = body["model"]?.DeepClone() };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new JsonObject { ["error"] = "jev_timeout", ["elapsed_ms"] = clock.ElapsedMilliseconds }; }
    }
}
