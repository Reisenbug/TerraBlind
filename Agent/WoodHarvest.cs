using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public interface IWoodHarvestWorld
{
    Task<JsonObject> Observe(CancellationToken token);
    Task<JsonObject> Approach(JsonObject tree, CancellationToken token);
    Task<JsonObject> Chop(JsonObject tree, CancellationToken token);
    Task<JsonObject> Pickup(JsonObject tree, CancellationToken token);
}

public static class WoodHarvest
{
    public static async Task<JsonObject> Run(int wanted, string constraints, IWoodHarvestWorld world,
        Func<JsonObject, CancellationToken, Task<string>> choose, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var attempts = new Dictionary<string, int>();
        var initial = await world.Observe(token);
        int baseline = initial["wood"].GetValue<int>();
        int gained = 0, felled = 0, decisions = 0;
        string outcome = "budget_exhausted";
        JsonObject failure = null;
        for (int step = 0; step < 16 && timer.Elapsed.TotalSeconds < 180; step++)
        {
            token.ThrowIfCancellationRequested();
            var scene = await world.Observe(token);
            gained = Math.Max(0, scene["wood"].GetValue<int>() - baseline);
            if (gained >= wanted) { outcome = "enough"; break; }
            if (scene["error"] != null) { outcome = scene["error"].ToString(); break; }
            var trees = scene["trees"].AsArray().OfType<JsonObject>()
                .Where(t => attempts.GetValueOrDefault(t["id"].ToString()) < 2).ToArray();
            if (trees.Length == 0) { outcome = "no_available_trees"; break; }
            scene["trees"] = new JsonArray(trees.Select(t => t.DeepClone()).ToArray());
            scene["wanted_additional_wood"] = wanted;
            scene["gained"] = gained;
            scene["constraints"] = constraints;
            scene["last_failure"] = failure?.DeepClone();
            string selected = await choose(scene, token);
            decisions++;
            if (selected == "stop") { outcome = "needs_agent_decision"; break; }
            var tree = trees.FirstOrDefault(t => t["id"].ToString() == selected)
                ?? throw new InvalidOperationException("Jev 选择了不存在的树。");
            attempts[selected] = attempts.GetValueOrDefault(selected) + 1;
            var result = await world.Approach(tree, token);
            if (result["error"] == null) result = await world.Chop(tree, token);
            if (result["outcome"]?.ToString() == "removed")
            {
                felled++;
                attempts[selected] = 2;
                result = await world.Pickup(tree, token);
                failure = result["error"] == null ? null : result;
            }
            else
            {
                failure = new JsonObject { ["tree"] = selected, ["result"] = result.DeepClone() };
                if (result["reason"]?.ToString() is "tool_weak" or "blocked" or "no_axe") attempts[selected] = 2;
            }
        }
        var final = await world.Observe(token);
        gained = Math.Max(0, final["wood"].GetValue<int>() - baseline);
        if (gained >= wanted) outcome = "enough";
        return new JsonObject { ["outcome"] = outcome, ["wanted"] = wanted, ["gained"] = gained,
            ["trees_felled"] = felled, ["jev_calls"] = decisions, ["elapsed_ms"] = timer.ElapsedMilliseconds,
            ["last_failure"] = failure?.DeepClone() };
    }
}

public sealed class WoodHarvestDecision
{
    readonly HttpClient _http;
    readonly string _key;
    public WoodHarvestDecision(HttpClient http, string key) { _http = http; _key = key; }

    public async Task<string> Choose(JsonObject scene, CancellationToken token)
    {
        var criteria = new JsonObject { ["stop"] = "没有候选符合玩家限制，或现有信息不足以安全继续，交还给上层 Agent。" };
        foreach (var tree in scene["trees"].AsArray().OfType<JsonObject>())
            criteria[tree["id"].ToString()] = tree.DeepClone();
        var payload = new JsonObject
        {
            ["model"] = "jev-latest", ["state"] = scene.DeepClone(),
            ["questions"] = new JsonObject
            {
                ["tree"] = new JsonObject { ["type"] = "choice",
                    ["instructions"] = "Terraria 自动采集普通木材。根据 constraints 和候选现场选择下一棵树。优先近且容易接近的树，遵守玩家的空间和保护限制。nearby_furniture 是附近家具坐标的事实，不是房屋归属证明。last_failure 存在时，判断值得重新接近同一棵还是换树；不要重复明显无效的尝试。找不到符合限制的候选时选 stop。代码负责移动、斧头、计数，不需要你生成坐标或动作。",
                    ["criteria"] = criteria }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"采木 Jev HTTP {(int)response.StatusCode}");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
            string choice = body?["answers"]?["tree"]?["choice"]?.GetValue<string>();
            if (choice == null || !criteria.ContainsKey(choice)) throw new InvalidOperationException("采木 Jev 未返回有效候选。");
            return choice;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("采木 Jev 请求超时。"); }
    }
}
