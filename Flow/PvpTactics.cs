using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public static class PvpTactics
{
    public sealed record Decision(string Move, string WeaponMode, string Aim = "velocity", bool Fire = true);
    public readonly record struct ShotSolution(Vector2 Point, float FlightFrames);

    public static Vector2? Aim(Vector2 origin, Vector2 target, Vector2 targetVelocity, float speed,
        float gravity, Func<Vector2, Vector2, bool> clear, float gravityDelay = 0)
        => Solve(origin, target, targetVelocity, speed, gravity, clear, gravityDelay)?.Point;

    public static ShotSolution? Solve(Vector2 origin, Vector2 target, Vector2 targetVelocity, float speed,
        float gravity, Func<Vector2, Vector2, bool> clear, float gravityDelay = 0, string kind = "velocity")
    {
        if (speed < 1 || !float.IsFinite(speed) || !float.IsFinite(gravity)) return null;
        for (float time = 2; time <= 90; time += .5f)
        {
            Vector2 destination = kind == "body" ? target : target + targetVelocity * (kind == "brake" ? Math.Min(time, 6) : time);
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
            if (!blocked) return new ShotSolution(origin + Vector2.Normalize(launch) * 400, time);
        }
        return null;
    }

    public static async Task<Decision> Choose(HttpClient http, string key, JsonObject scene, CancellationToken token)
    {
        var criteria = scene["movement_options"].AsObject();
        var weapons = scene["weapon_modes"].AsObject();
        var candidates = new List<Decision>();
        foreach (var move in criteria)
        {
            foreach (var weapon in weapons)
            {
                if (weapon.Value is JsonObject info && info["can_attack_now"]?.GetValue<bool>() == false
                    && weapon.Key.StartsWith("melee")) continue;
                if (weapon.Value is JsonObject details && details["aims"] is JsonObject aims)
                {
                    foreach (var aim in aims)
                        if (aim.Value["clear"]?.GetValue<bool>() == true) candidates.Add(new(move.Key, weapon.Key, aim.Key));
                }
                else candidates.Add(new(move.Key, weapon.Key));
            }
            candidates.Add(new(move.Key, "", "body", false));
        }
        var chosen = new List<Decision>();
        void Add(Decision decision) { if (decision != null && !chosen.Contains(decision) && chosen.Count < 64) chosen.Add(decision); }
        foreach (string move in criteria.Select(pair => pair.Key)) Add(candidates.First(candidate => candidate.Move == move));
        foreach (string weapon in weapons.Select(pair => pair.Key)) Add(candidates.FirstOrDefault(candidate => candidate.WeaponMode == weapon));
        foreach (string aim in candidates.Select(candidate => candidate.Aim).Distinct()) Add(candidates.First(candidate => candidate.Aim == aim));
        Add(candidates.FirstOrDefault(candidate => !candidate.Fire));
        int offset = (int)((scene["candidate_rotation"]?.GetValue<ulong>() ?? 0) % (ulong)candidates.Count);
        for (int i = 0; i < candidates.Count && chosen.Count < 64; i++) Add(candidates[(i + offset) % candidates.Count]);
        var plans = new JsonObject();
        var planCriteria = new JsonObject();
        for (int i = 0; i < chosen.Count; i++)
        {
            var plan = chosen[i]; string id = "p" + i;
            plans[id] = new JsonObject { ["move"] = plan.Move, ["weapon"] = plan.WeaponMode, ["aim"] = plan.Aim, ["fire"] = plan.Fire };
            planCriteria[id] = $"Use movement {plan.Move} (see state.movement_options)" + (plan.Fire
                ? $" and attack with weapon {plan.WeaponMode}, aim {plan.Aim}; see its aim facts in state.weapon_modes."
                : "; reposition without firing. Approaching does not require melee.");
        }
        var state = (JsonObject)scene.DeepClone();
        state["plans"] = plans; state["plans_before_budget"] = candidates.Count;
        var body = new JsonObject
        {
            ["model"] = "jev-latest", ["state"] = state,
            ["questions"] = new JsonObject { ["plan"] = new JsonObject
            {
                ["type"] = "choice", ["criteria"] = planCriteria,
                ["instructions"] = "Select one short-term Terraria PvP plan, combining movement, a specific weapon and aiming intent. Follow strategy and directive.instructions; player constraints override Agent preferences. Approaching may be combined with ranged fire: never hold an out-of-reach sword just because you want to close distance. Compare current firing opportunities, flight times, terrain blockers, incoming projectiles, motion history and HP changes. Reposition when blocked, but a stance probe is not a computed path. Opponents have no Boss-like contact damage. A future dash or instantaneous reversal cannot be predicted; constant velocity and braking are alternative hypotheses, not facts. Prefer useful attacks while moving rather than standing or endlessly jumping. Aim is recalculated by code at execution. Choose no-fire only when repositioning or withholding a shot is useful."
            } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Jev HTTP " + (int)response.StatusCode);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        string selected = result?["answers"]?["plan"]?["choice"]?.GetValue<string>();
        if (selected == null || !plans.ContainsKey(selected)) throw new InvalidOperationException("Jev 未返回有效联合方案。");
        DiagLog.Write($"[pvp-plan] selected={selected} candidates={chosen.Count}/{candidates.Count} confidence={result?["answers"]?["plan"]?["confidence"]}");
        return chosen[int.Parse(selected.AsSpan(1))];
    }
}
