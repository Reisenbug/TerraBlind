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
    public sealed record Decision(string Move, string WeaponMode, string Aim = "velocity", bool Fire = true, bool Cover = false);
    public readonly record struct ShotSolution(Vector2 Point, float FlightFrames)
    {
        public Vector2 Impact { get; init; }
    }
    internal readonly record struct ShotFlight(float Speed, int Updates, int Steps, int GravityStart = 1,
        float Gravity = 0, float DragX = 1, float DragY = 1, float FallLimit = float.PositiveInfinity);

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
            if (!blocked) return new ShotSolution(origin + Vector2.Normalize(launch) * 400, time) { Impact = destination };
        }
        return null;
    }

    internal static ShotSolution? SolveFlight(Vector2 origin, ShotFlight flight, Vector2 reach,
        Func<float, Vector2> targetAt, Func<Vector2, Vector2, bool> clear, float earliestFrame = 0)
    {
        if (!float.IsFinite(flight.Speed) || flight.Speed <= 0 || flight.Updates < 1) return null;
        float dragX = 1, dragY = 1, dx = 0, dy = 0, fall = 0, drop = 0;
        ShotSolution? best = null;
        float bestError = float.PositiveInfinity;
        for (int step = 1; step <= flight.Steps; step++)
        {
            if (step >= flight.GravityStart)
            {
                dragX *= flight.DragX; dragY *= flight.DragY;
                fall = fall * flight.DragY + flight.Gravity;
            }
            dx += dragX; dy += dragY; drop += fall;
            float frames = step / (float)flight.Updates;
            if (frames < earliestFrame) continue;
            Vector2 offset = targetAt(frames) - origin;
            Vector2 ideal = new(offset.X / dx, (offset.Y - drop) / dy);
            if (ideal.LengthSquared() < .0001f) continue;
            Vector2 launch = Vector2.Normalize(ideal) * flight.Speed;
            float y = launch.Y * dy + drop;
            if (Math.Abs(launch.X * dx - offset.X) > reach.X || y < offset.Y - reach.Y
                || (launch.Y * dragY + fall <= flight.FallLimit && y > offset.Y + reach.Y)) continue;
            Vector2 velocity = launch, point = origin;
            int advanced = 0;
            while (advanced < step)
            {
                if (advanced + 1 >= flight.GravityStart)
                    velocity = new(velocity.X * flight.DragX, velocity.Y * flight.DragY + flight.Gravity);
                velocity.Y = Math.Min(velocity.Y, flight.FallLimit);
                Vector2 next = point + velocity;
                if (clear != null && !clear(point, next)) break;
                point = next;
                advanced++;
            }
            Vector2 miss = point - origin - offset;
            if (advanced == step && Math.Abs(miss.X) <= reach.X && Math.Abs(miss.Y) <= reach.Y)
            {
                float error = (miss / reach).LengthSquared() + frames * .004f;
                if (error >= bestError) continue;
                bestError = error;
                best = new ShotSolution(origin + Vector2.Normalize(launch) * 400, frames) { Impact = point };
                if (error <= (frames + 1) * .004f) break;
            }
        }
        return best;
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
        var cover = scene["cover_option"] as JsonObject;
        if (cover != null && criteria.Count > 0)
            candidates.Add(new(criteria.First().Key, "", "body", false, Cover: true));
        var chosen = new List<Decision>();
        void Add(Decision decision) { if (decision != null && !chosen.Contains(decision) && chosen.Count < 64) chosen.Add(decision); }
        Add(candidates.FirstOrDefault(candidate => candidate.Cover));
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
            plans[id] = new JsonObject { ["move"] = plan.Move, ["weapon"] = plan.WeaponMode, ["aim"] = plan.Aim, ["fire"] = plan.Fire,
                ["place_cover"] = plan.Cover };
            if (plan.Cover)
            {
                planCriteria[id] = "Place the one checked solid cover tile in state.cover_option, then resume combat.";
                continue;
            }
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
                ["instructions"] = "Select a short-term Terraria PvP plan. Follow strategy and directive.instructions; player constraints take precedence. Pursue while shooting when possible. Check the verified short walk probes for positions where the direct line opens or closes. When hard cover blocks the opponent's straight shots, consider staying behind it and using an aim with through_tiles=true. Place the offered cover tile only when protecting against a plausible straight shot is useful. Compare flight time, incoming projectiles and health. Reposition when blocked. Opponents have no contact damage. A future reversal or dash remains uncertain. Aim is recalculated at execution."
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
