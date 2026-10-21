using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public static class PvpTactics
{
    public static readonly string[] Choices = { "hold", "approach", "retreat", "jump_approach", "jump_retreat", "left", "right" };

    public static Vector2? Aim(Vector2 origin, Vector2 target, Vector2 targetVelocity, float speed,
        float gravity, Func<Vector2, Vector2, bool> clear, float gravityDelay = 0)
    {
        if (speed < 1 || !float.IsFinite(speed) || !float.IsFinite(gravity)) return null;
        for (float time = 2; time <= 90; time += .5f)
        {
            Vector2 destination = target + targetVelocity * Math.Min(time, 20);
            float falling = Math.Max(0, time - gravityDelay);
            Vector2 launch = (destination - origin - new Vector2(0, gravity * falling * falling / 2)) / time;
            if (Math.Abs(launch.Length() - speed) > speed * .025f) continue;
            Vector2 previous = origin;
            bool blocked = false;
            for (float t = 1; t <= Math.Ceiling(time); t++)
            {
                float tick = Math.Min(t, time);
                float drop = Math.Max(0, tick - gravityDelay);
                Vector2 point = origin + launch * tick + new Vector2(0, gravity * drop * drop / 2);
                if (!clear(previous, point)) { blocked = true; break; }
                previous = point;
            }
            if (!blocked) return origin + Vector2.Normalize(launch) * 400;
        }
        return null;
    }

    public static int Direction(string choice, float dx) => choice switch
    {
        "approach" or "jump_approach" => Math.Sign(dx),
        "retreat" or "jump_retreat" => -Math.Sign(dx),
        "left" => -1, "right" => 1, _ => 0
    };

    public static async Task<string> Choose(HttpClient http, string key, JsonObject scene, CancellationToken token)
    {
        var criteria = new JsonObject
        {
            ["hold"] = "保持位置，利用已有射界；不值得移动或没有安全移动候选时选择。",
            ["approach"] = "向对手接近，缩短距离或寻求射界。",
            ["retreat"] = "远离对手，拉开弓的射击距离。",
            ["jump_approach"] = "向对手移动并尝试跳起，跨越小障碍或改变射击高度。",
            ["jump_retreat"] = "远离对手并尝试跳起，避免近距离压迫。",
            ["left"] = "向左换侧，尝试绕开遮挡或避让临近弹幕。",
            ["right"] = "向右换侧，尝试绕开遮挡或避让临近弹幕。"
        };
        var allowed = scene["allowed"].AsArray();
        foreach (string choice in Choices)
            if (!System.Linq.Enumerable.Any(allowed, node => node.ToString() == choice)) criteria.Remove(choice);
        var body = new JsonObject
        {
            ["model"] = "jev-latest", ["state"] = scene.DeepClone(),
            ["questions"] = new JsonObject { ["move"] = new JsonObject
            {
                ["type"] = "choice", ["criteria"] = criteria,
                ["instructions"] = "Terraria 一对一弓箭 PvP，选择接下来短时间的移动意图。遵循 strategy。代码负责弹道瞄准和碰撞检查。坐标单位像素，y向下。aim_clear 表示当前预测弹道可通行。避免贴脸、跌落和近处来弹；被遮挡时尝试换位，而不是长期停着射墙。根据 hp_change、目标移动和上次意图的进展调整，别反复选择没有位移的动作。你不知道对手未来输入，不能假设当前速度会一直保持。"
            } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Jev HTTP " + (int)response.StatusCode);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        string selected = result?["answers"]?["move"]?["choice"]?.GetValue<string>();
        if (selected == null || !criteria.ContainsKey(selected)) throw new InvalidOperationException("Jev 未返回有效移动候选。");
        return selected;
    }
}
