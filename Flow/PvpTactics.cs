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
    public sealed record Decision(string Move, string WeaponMode);

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

    public static async Task<Decision> Choose(HttpClient http, string key, JsonObject scene, CancellationToken token)
    {
        var criteria = scene["movement_options"].AsObject();
        var weapons = scene["weapon_modes"].AsObject();
        var weaponCriteria = new JsonObject();
        foreach (var pair in weapons) weaponCriteria[pair.Key] = pair.Value.ToJsonString();
        var body = new JsonObject
        {
            ["model"] = "jev-latest", ["state"] = scene.DeepClone(),
            ["questions"] = new JsonObject { ["move"] = new JsonObject
            {
                ["type"] = "choice", ["criteria"] = criteria.DeepClone(),
                ["instructions"] = "Terraria 一对一 PvP，选择短期移动的方向和幅度。遵循 strategy，根据 weapon_modes、当前距离和射界决定接近还是拉开，不要只躲不打。近战追击要缩短实际距离和高差；目标在下面就不要无意义往上跳。也可以远程消耗，不必为近战一直追人。对手不是有接触伤害的 Boss，拉开距离并非总是有利。movement_state 是自身机动、威胁弹幕和地形余量。小幅向上会正常跳跃/飞行并使用可用额外跳；中幅可能用钩爪，大幅竖直可能用坐骑。需要升高越过遮挡或跟上对手时主动选向上。留意临近弹幕、hp_change、上次动作是否真有位移，不重复被挡的动作。坐标y向下。只知道对手当前运动，不知道其未来按键。"
            }, ["weapon"] = new JsonObject
            {
                ["type"] = "choice", ["criteria"] = weaponCriteria,
                ["instructions"] = "Terraria PvP，选择此刻实际用于攻击的武器，不是进入持续近战追击或远程状态。优先选择 can_attack_now=true 的模式；近战够不到而远程可打时，继续远程消耗，进入 contact_reach_pixels 再考虑切近战。strategy 说近距离用剑，并不等于隔几百像素也拿剑空追。若都暂时打不到，根据地形和 strategy 选择准备使用的武器。不要只比较面板伤害。每轮根据实际距离重新判断，不因 current_mode 是 melee 就延续近战。只选择提供的模式。"
            } }
        };
        foreach (var question in body["questions"].AsObject())
            question.Value["instructions"] = question.Value["instructions"].ToString()
                + " Follow directive.player_goal and directive.instructions (general and the relevant section). Player constraints override Agent preferences. Movement toward an opponent may be combined with ranged fire; closing distance never requires holding a melee weapon.";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Jev HTTP " + (int)response.StatusCode);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        string selected = result?["answers"]?["move"]?["choice"]?.GetValue<string>();
        string weapon = result?["answers"]?["weapon"]?["choice"]?.GetValue<string>();
        if (selected == null || !criteria.ContainsKey(selected)) throw new InvalidOperationException("Jev 未返回有效移动候选。");
        if (weapon == null || !weapons.ContainsKey(weapon)) throw new InvalidOperationException("Jev 未返回有效武器模式。");
        return new Decision(selected, weapon);
    }
}
