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
        float reach = Math.Max(Math.Abs(player.velocity.X), Math.Max(player.maxRunSpeed, player.accRunSpeed)) * Horizon + player.width + 32;
        left = Math.Min(left, Math.Max(0, (int)Math.Floor((player.position.X - reach) / 16)));
        right = Math.Max(right, Math.Min(Main.maxTilesX, (int)Math.Ceiling((player.position.X + reach) / 16)));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(player.position.Y, goal.Y) / 16) - 30);
        int bottom = Math.Min(Main.maxTilesY, (int)Math.Ceiling(Math.Max(player.position.Y, goal.Y) / 16) + 24);
        if (right - left > 160 || bottom - top > 160 || right <= left || bottom <= top)
            throw new ArgumentException("goal_requires_larger_than_local_snapshot");
        return new Rectangle(left, top, right - left, bottom - top);
    }

    public JsonObject Plan(JsonObject snapshot, Vector2 goal, int[] continuation = null, JsonNode guidance = null, bool terminal = false)
    {
        VanillaFrame.RequireWorker();
        if (!float.IsFinite(goal.X) || !float.IsFinite(goal.Y)) throw new ArgumentException("invalid_goal");
        var timer = Stopwatch.StartNew();
        var player = VanillaFrame.RestoreInWorker(snapshot);
        int hp = player.statLife;
        float wings = player.wingTime;
        int jumps = VanillaPrediction.Observe(player, false)["available_jumps"].GetValue<int>();
        Dictionary<(int, int), int> field = null;
        if (!terminal && guidance is JsonArray rows)
            field = rows.ToDictionary(row => (row[0].GetValue<int>(), row[1].GetValue<int>()), row => row[2].GetValue<int>());
        else if (!terminal && guidance != null)
        {
            field = new();
            var values = VanillaFrame.UnpackInts(guidance.GetValue<string>());
            for (int i = 0; i < values.Length; i += 3) field.Add((values[i], values[i + 1]), values[i + 2]);
        }
        double missing = field?.Count > 0 ? field.Values.Max() * 16d + 10000 : 0;
        int direction = goal.X >= player.position.X ? 2 : 1;
        bool gravity = player.buffType.Contains(Terraria.ID.BuffID.Gravitation);
        bool transit = !terminal && snapshot["navigation"]?.GetValue<bool>() == true && field?.Count > 0;
        var prediction = new VanillaPrediction(snapshot);
        var candidates = new List<(string name, int[] keys)>();
        if (continuation?.Length > 0)
            candidates.Add(("continue", Enumerable.Range(0, Horizon)
                .Select(i => i < continuation.Length ? continuation[i] : transit ? continuation[^1] : 0).ToArray()));
        candidates.Add(("wait", new int[Horizon]));
        if (jumps > 0 || wings > 0)
            foreach (int release in new[] { 6, 18, 30 })
                candidates.Add(($"travel_{release}", Enumerable.Range(0, Horizon)
                    .Select(i => direction | (i == release ? 0 : 4)).ToArray()));
        candidates.Add(("walk", Enumerable.Repeat(direction, Horizon).ToArray()));
        candidates.Add(("drop", Enumerable.Repeat(direction | 16, Horizon).ToArray()));
        candidates.Add(("vertical_drop", Enumerable.Repeat(16, Horizon).ToArray()));
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
        if (!transit)
            foreach (int stop in new[] { 12, 36, 60 })
                candidates.Add(($"brake_{stop}", Enumerable.Range(0, Horizon).Select(i => i < stop ? direction : 0).ToArray()));
        int reverse = direction == 2 ? 1 : 2;
        foreach (int retreat in new[] { 12, 30, 48 })
        foreach (int hold in new[] { 18, 60 })
            candidates.Add(($"retreat_{retreat}_{hold}", Enumerable.Range(0, Horizon)
                .Select(i => (i < retreat ? reverse : direction) | (i >= retreat && i < retreat + hold ? 4 : 0)).ToArray()));
        foreach (int delay in new[] { 6, 18, 36, 54, 72 })
            candidates.Add(($"landing_jump_{delay}", Enumerable.Range(0, Horizon)
                .Select(i => direction | (i >= delay && i < delay + 12 ? 4 : 0)).ToArray()));
        if (gravity)
            candidates.Add(("invert", Enumerable.Range(0, Horizon).Select(i => direction | (i == 0 ? 8 : 0)).ToArray()));
        var diagnostics = new JsonArray();
        double best = double.PositiveInfinity;
        int[] chosen = Array.Empty<int>();
        string selected = "none";
        JsonObject selectedForecast = null;
        double restoreMs = 0, updateMs = 0, observeMs = 0, cleanupMs = 0;
        var frames = new List<VanillaPrediction.MovementSample>(Horizon);
        try
        {
        foreach (var candidate in candidates)
        {
            var forecast = prediction.Predict(candidate.keys, false, restoreAfter: false, samples: frames);
            var timing = forecast["timing"];
            restoreMs += timing["restore_ms"].GetValue<double>();
            updateMs += timing["update_ms"].GetValue<double>();
            observeMs += timing["observe_ms"].GetValue<double>();
            cleanupMs += timing["cleanup_ms"].GetValue<double>();
            int damage = Math.Max(0, hp - frames.Min(frame => frame.Hp));
            bool safe = forecast["outcome"].GetValue<string>() == "complete"
                && damage == 0 && frames.All(frame => !frame.Dead);
            double score = double.PositiveInfinity;
            if (safe)
            {
                double distance(VanillaPrediction.MovementSample frame)
                {
                    var position = frame.Position;
                    if (field == null) return Vector2.Distance(goal, position);
                    var cell = ((int)((position.X + player.width / 2f) / 16), (int)((position.Y + player.height) / 16) - 1);
                    return field.TryGetValue(cell, out int cost) ? cost * 16d : missing + Vector2.Distance(goal, position);
                }
                score = transit ? distance(frames[^1]) + .1 * frames.Average(distance)
                    : frames.Average(distance) + .25 * distance(frames[^1]);
                score += Math.Max(0, wings - frames[^1].WingTime) * .02;
                score += Math.Max(0, jumps - frames[^1].AvailableJumps) * 2;
                if (score < best - .01)
                {
                    best = score; selected = candidate.name; chosen = candidate.keys;
                }
            }
            diagnostics.Add(new JsonObject { ["name"] = candidate.name, ["safe"] = safe, ["hp_loss"] = damage,
                ["score"] = safe ? JsonValue.Create(score) : null, ["outcome"] = forecast["outcome"].DeepClone(),
                ["elapsed_ms"] = forecast["elapsed_ms"].DeepClone(), ["timing"] = forecast["timing"].DeepClone() });
            if (safe && candidate.name is "walk" or "continue"
                && (candidate.name != "continue" || jumps == 0 && wings == 0)
                && frames.All(frame => frame.Grounded)
                && frames.Skip(6).All(frame => Math.Abs(frame.Velocity.X) > .1f))
            {
                var end = frames[^1];
                var endPosition = end.Position;
                bool progress = Vector2.Distance(goal, endPosition) < Vector2.Distance(goal, player.position) - 16;
                if (field != null)
                {
                    var startCell = ((int)(player.Center.X / 16), (int)(player.Bottom.Y / 16) - 1);
                    var endCell = ((int)((endPosition.X + player.width / 2f) / 16), (int)((endPosition.Y + player.height) / 16) - 1);
                    progress = field.TryGetValue(startCell, out int startCost) && field.TryGetValue(endCell, out int endCost)
                        && endCost < startCost;
                }
                if (progress) break;
            }
        }
        if (chosen.Length > 0)
        {
            selectedForecast = prediction.Predict(chosen, false, restoreAfter: false);
            var timing = selectedForecast["timing"];
            restoreMs += timing["restore_ms"].GetValue<double>();
            updateMs += timing["update_ms"].GetValue<double>();
            observeMs += timing["observe_ms"].GetValue<double>();
            cleanupMs += timing["cleanup_ms"].GetValue<double>();
        }
        }
        finally
        {
            long cleanupStart = Stopwatch.GetTimestamp();
            VanillaFrame.RestoreInWorker(snapshot);
            cleanupMs += Stopwatch.GetElapsedTime(cleanupStart).TotalMilliseconds;
        }
        return new JsonObject { ["status"] = chosen.Length == 0 ? "no_safe_candidate" : "planned", ["selected"] = selected,
            ["controls"] = new JsonArray(chosen.Select(key => JsonValue.Create(key)).ToArray()),
            ["forecast"] = selectedForecast, ["candidates"] = diagnostics, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds,
            ["timing"] = new JsonObject { ["restore_ms"] = restoreMs, ["update_ms"] = updateMs,
                ["observe_ms"] = observeMs, ["cleanup_ms"] = cleanupMs } };
    }
}
