using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public sealed class VanillaLocalPlanner
{
    public const int Horizon = 90;
    public const int ExecuteFrames = 8;

    public static Rectangle SnapshotArea(Player player, Vector2 goal)
    {
        if (!float.IsFinite(goal.X) || !float.IsFinite(goal.Y)) throw new ArgumentException("invalid_goal");
        int left = Math.Max(0, (int)Math.Floor(Math.Min(player.position.X, goal.X) / 16) - 8);
        int right = Math.Min(Main.maxTilesX, (int)Math.Ceiling(Math.Max(player.position.X, goal.X) / 16) + 10);
        int top = Math.Max(0, (int)Math.Floor(Math.Min(player.position.Y, goal.Y) / 16) - 30);
        int bottom = Math.Min(Main.maxTilesY, (int)Math.Ceiling(Math.Max(player.position.Y, goal.Y) / 16) + 24);
        if (right - left > 160 || bottom - top > 160 || right <= left || bottom <= top)
            throw new ArgumentException("goal_requires_larger_than_local_snapshot");
        return new Rectangle(left, top, right - left, bottom - top);
    }

    public JsonObject Plan(JsonObject snapshot, Vector2 goal, int[] continuation = null)
    {
        VanillaFrame.RequireWorker();
        if (!float.IsFinite(goal.X) || !float.IsFinite(goal.Y)) throw new ArgumentException("invalid_goal");
        var timer = Stopwatch.StartNew();
        var player = VanillaFrame.RestoreInWorker(snapshot);
        int hp = player.statLife;
        float wings = player.wingTime;
        int jumps = VanillaPrediction.Observe(player, false)["available_jumps"].GetValue<int>();
        int direction = goal.X >= player.position.X ? 2 : 1;
        bool gravity = player.buffType.Contains(Terraria.ID.BuffID.Gravitation);
        var prediction = new VanillaPrediction(snapshot);
        var candidates = new List<(string name, int[] keys)>();
        if (continuation?.Length > 0)
            candidates.Add(("continue", Enumerable.Range(0, Horizon).Select(i => i < continuation.Length ? continuation[i] : 0).ToArray()));
        candidates.Add(("wait", new int[Horizon]));
        candidates.Add(("walk", Enumerable.Repeat(direction, Horizon).ToArray()));
        foreach (int delay in new[] { 0, 12, 24, 36 })
        foreach (int hold in new[] { 6, 18, 60 })
        {
            int start = delay;
            candidates.Add(($"jump_{delay}_{hold}", Enumerable.Range(0, Horizon)
                .Select(i => direction | (i >= start && i < start + hold ? 4 : 0)).ToArray()));
        }
        foreach (int delay in new[] { 1, 12, 24 })
        {
            int start = delay;
            candidates.Add(($"repeat_{delay}", Enumerable.Range(0, Horizon)
                .Select(i => direction | (i >= start && (i - start) % 14 < 12 ? 4 : 0)).ToArray()));
        }
        foreach (int release in new[] { 1, 12, 18, 30, 45 })
            candidates.Add(($"relaunch_{release}", Enumerable.Range(0, Horizon)
                .Select(i => direction | (i == release ? 0 : 4)).ToArray()));
        foreach (int stop in new[] { 12, 36, 60 })
            candidates.Add(($"brake_{stop}", Enumerable.Range(0, Horizon).Select(i => i < stop ? direction : 0).ToArray()));
        if (gravity)
            candidates.Add(("invert", Enumerable.Range(0, Horizon).Select(i => direction | (i == 0 ? 8 : 0)).ToArray()));
        var diagnostics = new JsonArray();
        double best = double.PositiveInfinity;
        int[] chosen = Array.Empty<int>();
        string selected = "none";
        JsonObject selectedForecast = null;
        foreach (var candidate in candidates)
        {
            var forecast = prediction.Predict(candidate.keys, false);
            var frames = forecast["frames"].AsArray();
            int damage = Math.Max(0, hp - frames.Min(frame => frame["hp"].GetValue<int>()));
            bool safe = forecast["outcome"].GetValue<string>() == "complete"
                && damage == 0 && frames.All(frame => !frame["dead"].GetValue<bool>());
            double score = double.PositiveInfinity;
            if (safe)
            {
                double distance(JsonNode frame) => Vector2.Distance(goal, new Vector2(frame["x"].GetValue<float>(), frame["y"].GetValue<float>()));
                score = frames.Min(distance) + .25 * distance(frames[^1]);
                score += Math.Max(0, wings - frames[^1]["wing_time"].GetValue<float>()) * .02;
                score += Math.Max(0, jumps - frames[^1]["available_jumps"].GetValue<int>()) * 2;
                if (score < best - .01)
                {
                    best = score; selected = candidate.name; chosen = candidate.keys; selectedForecast = forecast;
                }
            }
            diagnostics.Add(new JsonObject { ["name"] = candidate.name, ["safe"] = safe, ["hp_loss"] = damage,
                ["score"] = safe ? JsonValue.Create(score) : null, ["outcome"] = forecast["outcome"].DeepClone(),
                ["elapsed_ms"] = forecast["elapsed_ms"].DeepClone(), ["timing"] = forecast["timing"].DeepClone() });
        }
        return new JsonObject { ["status"] = chosen.Length == 0 ? "no_safe_candidate" : "planned", ["selected"] = selected,
            ["controls"] = new JsonArray(chosen.Select(key => JsonValue.Create(key)).ToArray()),
            ["forecast"] = selectedForecast, ["candidates"] = diagnostics, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds };
    }
}
