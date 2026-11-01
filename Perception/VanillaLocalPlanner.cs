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
    public const int Horizon = 180;
    public const int ExecuteFrames = 8;
    public JsonObject PlanMovement(JsonObject snapshot, Vector2 goal, JsonNode guidance = null)
    {
        VanillaFrame.RequireWorker();
        var timer = Stopwatch.StartNew();
        var player = VanillaFrame.RestoreInWorker(snapshot);
        var origin = player.position;
        int width = player.width, height = player.height, hp = player.statLife;
        bool extraJump = player.AnyExtraJumpUsable();
        var field = ReadGuidance(guidance);
        double Cost(Vector2 position)
        {
            if (field == null) return Vector2.Distance(position, goal);
            return field.TryGetValue(FootCell(position, width, height), out int value) ? value : double.PositiveInfinity;
        }
        double initialCost = Cost(origin), bestScore = double.PositiveInfinity;
        var prediction = new VanillaPrediction(snapshot);
        var samples = new List<VanillaPrediction.MovementSample>();
        var stopping = new List<VanillaPrediction.MovementSample>();
        var diagnostics = new JsonArray();
        int[] chosen = null;
        bool incomplete = false;
        double restoreMs = 0, updateMs = 0;
        var candidates = new List<(int direction, int pattern, int[] keys)>();
        var rescueTargets = new Dictionary<int, (int jumpAt, float x)>();
        foreach (int direction in new[] { 1, 2 })
        foreach (int pattern in new[] { 0, 1, 2, 3, 4, 5 })
            candidates.Add((direction, pattern, Enumerable.Range(0, 90).Select(i => direction | (pattern switch {
                1 => i < 18 ? 4 : 0,
                2 => i >= 12 && i < 30 ? 4 : 0,
                3 => i != 20 ? 4 : 0,
                4 => i != 40 ? 4 : 0,
                5 => 16,
                _ => 0
            })).ToArray()));
        foreach (int direction in new[] { 1, 2 })
            candidates.Add((direction, 8, Enumerable.Range(0, 90)
                .Select(i => direction | (i < 12 ? 16 : 0)).ToArray()));
        int primaryCount = candidates.Count;
        try
        {
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                var (direction, pattern, keys) = candidates[candidateIndex];
                Func<Player, int, int> policy = null;
                if (rescueTargets.TryGetValue(candidateIndex, out var rescue))
                    policy = (moving, i) => i < rescue.jumpAt ? keys[i]
                        : (moving.position.X < rescue.x - 4 ? 2 : moving.position.X > rescue.x + 4 ? 1 : 0)
                            | (i < rescue.jumpAt + 12 ? 4 : 0);
                var result = prediction.Predict(keys, false, restoreAfter: false, samples: samples, stopOnDamage: true,
                    maxFrames: 405, controlPolicy: policy);
                string outcome = result["outcome"].GetValue<string>();
                if (pattern is 6 or 9 && extraJump && (outcome is "damaged" or "dead") && samples.Count > 8)
                {
                    int jumpAt = samples.Count - 8;
                    candidates.Add((direction, 7, Enumerable.Range(0, keys.Length)
                        .Select(i => direction | (i < jumpAt ? 16 : i < jumpAt + 12 ? 4 : 0)).ToArray()));
                    candidates.Add((direction, 10, Enumerable.Range(0, keys.Length)
                        .Select(i => (i < jumpAt ? direction | 16 : i < jumpAt + 12 ? 4 : 0)).ToArray()));
                    rescueTargets[candidates.Count - 1] = (jumpAt, samples[^1].Position.X);
                }
                if (pattern == 6 && outcome == "complete" && !samples[^1].Grounded
                    && Cost(samples[^1].Position) < initialCost)
                    candidates.Add((direction, 9, Enumerable.Repeat(direction | 16, 360).ToArray()));
                if (pattern is 7 or 9 or 10 && (outcome is "complete" or "outside_observed_world")
                    && !samples[^1].Grounded && Cost(samples[^1].Position) < initialCost)
                    incomplete = true;
                restoreMs += result["timing"]["restore_ms"].GetValue<double>();
                updateMs += result["timing"]["update_ms"].GetValue<double>();
                int selectedLength = 0;
                double candidateScore = double.PositiveInfinity;
                for (int i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    if (sample.Dead || sample.Hp < hp) break;
                    if (!sample.Grounded || sample.Velocity.Y != 0 || Vector2.DistanceSquared(origin, sample.Position) < 256) continue;
                    double cost = Cost(sample.Position);
                    if (cost >= initialCost) continue;
                    double score = cost + (i + 1) * .1;
                    if (score >= candidateScore) continue;
                    candidateScore = score;
                    selectedLength = i + 1;
                }
                bool stoppedSafely = false;
                if (selectedLength > 0 && candidateScore < bestScore)
                {
                    var stoppedKeys = keys.Take(selectedLength).Concat(new int[45]).ToArray();
                    var stopped = prediction.Predict(stoppedKeys, false, restoreAfter: false,
                        samples: stopping, stopOnDamage: true, maxFrames: 405);
                    restoreMs += stopped["timing"]["restore_ms"].GetValue<double>();
                    updateMs += stopped["timing"]["update_ms"].GetValue<double>();
                    var end = stopping[^1];
                    stoppedSafely = stopped["outcome"].GetValue<string>() == "complete"
                        && end.Grounded && end.Velocity == Vector2.Zero && end.Hp >= hp && !end.Dead;
                    if (stoppedSafely)
                    {
                        int length = stoppedKeys.Length;
                        while (length > selectedLength + 2 && stopping[length - 1].Velocity == Vector2.Zero
                            && stopping[length - 2].Velocity == Vector2.Zero
                            && stopping[length - 1].Position == stopping[length - 2].Position) length--;
                        double cost = Cost(end.Position), score = cost + length * .1;
                        if (cost < initialCost && score < bestScore)
                        { bestScore = score; chosen = stoppedKeys.Take(length).ToArray(); }
                    }
                }
                diagnostics.Add(new JsonObject { ["direction"] = direction, ["pattern"] = pattern,
                    ["frames"] = samples.Count, ["selected_prefix"] = selectedLength,
                    ["stopped_safely"] = stoppedSafely,
                    ["grounded_frames"] = samples.Count(frame => frame.Grounded),
                    ["initial_cost"] = double.IsFinite(initialCost) ? JsonValue.Create(initialCost) : null,
                    ["minimum_cost"] = samples.Select(frame => Cost(frame.Position)).Where(double.IsFinite)
                        .Select(value => (double?)value).Min(),
                    ["end_x"] = samples[^1].Position.X, ["end_y"] = samples[^1].Position.Y,
                    ["end_vy"] = samples[^1].Velocity.Y, ["end_wet"] = samples[^1].Wet,
                    ["outcome"] = result["outcome"].DeepClone() });
                if (candidateIndex == primaryCount - 1 && chosen == null)
                    foreach (int dropDirection in new[] { 1, 2 })
                        candidates.Add((dropDirection, 6, Enumerable.Repeat(dropDirection | 16, 180).ToArray()));
            }
            JsonObject forecast = chosen == null ? null : prediction.Predict(chosen, false, captureEnd: true, stopOnDamage: true, maxFrames: 405);
            if (forecast != null && forecast["outcome"].GetValue<string>() != "complete")
            { chosen = null; forecast = null; }
            return new JsonObject { ["status"] = chosen == null ? incomplete ? "incomplete_horizon" : "no_safe_progress" : "planned",
                ["controls"] = new JsonArray((chosen ?? Array.Empty<int>()).Select(key => JsonValue.Create(key)).ToArray()),
                ["forecast"] = forecast, ["candidates"] = diagnostics, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds,
                ["restore_ms"] = restoreMs, ["update_ms"] = updateMs };
        }
        finally { VanillaFrame.RestoreInWorker(snapshot); }
    }

    static bool RepeatsCeilingContact(List<VanillaPrediction.MovementSample> frames)
    {
        var contacts = new List<Vector2>();
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i - 1].Velocity.Y >= -1 || frames[i].Velocity.Y < 0) continue;
            var point = frames[i].Position;
            if (contacts.Any(previous => Vector2.DistanceSquared(previous, point) <= 4)) return true;
            contacts.Add(point);
        }
        return false;
    }
    public static (int x, int y) FootCell(Vector2 position, int width, int height)
        => ((int)Math.Floor((position.X + width / 2f) / 16), (int)Math.Floor((position.Y + height - 1) / 16));

    public static Rectangle SnapshotArea(Player player, Vector2 goal, int leadFrames = 0)
    {
        if (!float.IsFinite(goal.X) || !float.IsFinite(goal.Y)) throw new ArgumentException("invalid_goal");
        int left = Math.Max(0, (int)Math.Floor(Math.Min(player.position.X, goal.X) / 16) - 8);
        int right = Math.Min(Main.maxTilesX, (int)Math.Ceiling(Math.Max(player.position.X, goal.X) / 16) + 10);
        float reach = Math.Max(Math.Abs(player.velocity.X), Math.Max(player.maxRunSpeed, player.accRunSpeed))
            * (Horizon + leadFrames) + player.width + 32;
        left = Math.Min(left, Math.Max(0, (int)Math.Floor((player.position.X - reach) / 16)));
        right = Math.Max(right, Math.Min(Main.maxTilesX, (int)Math.Ceiling((player.position.X + reach) / 16)));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(player.position.Y, goal.Y) / 16) - 30);
        int bottom = Math.Min(Main.maxTilesY, (int)Math.Ceiling(Math.Max(player.position.Y, goal.Y) / 16) + 24);
        bottom = Math.Max(bottom, Math.Min(Main.maxTilesY,
            (int)Math.Ceiling((player.position.Y + player.maxFallSpeed * (Horizon + leadFrames) + player.height + 32) / 16)));
        top = Math.Min(top, Math.Max(0, (int)Math.Floor((player.position.Y + Math.Min(0, player.velocity.Y) * (Horizon + leadFrames) - 32) / 16)));
        if (right - left > 256 || bottom - top > 256 || right <= left || bottom <= top)
            throw new ArgumentException("goal_requires_larger_than_local_snapshot");
        return new Rectangle(left, top, right - left, bottom - top);
    }

    public JsonObject Plan(JsonObject snapshot, Vector2 goal, int[] continuation = null, JsonNode guidance = null, bool terminal = false,
        int routeDirection = 0, bool preferContinuation = false, bool continuationOnly = false,
        Func<bool> terrainContinuation = null)
    {
        VanillaFrame.RequireWorker();
        if (!float.IsFinite(goal.X) || !float.IsFinite(goal.Y)) throw new ArgumentException("invalid_goal");
        var timer = Stopwatch.StartNew();
        var player = VanillaFrame.RestoreInWorker(snapshot);
        int hp = player.statLife;
        float wings = player.wingTime;
        int jumps = VanillaPrediction.Observe(player, false)["available_jumps"].GetValue<int>();
        var field = terminal ? null : ReadGuidance(guidance);
        double missing = field?.Count > 0 ? field.Values.Max() * 16d + 10000 : 0;
        int direction = !terminal && routeDirection != 0 ? (routeDirection > 0 ? 2 : 1)
            : goal.X >= player.position.X ? 2 : 1;
        bool gravity = player.buffType.Contains(Terraria.ID.BuffID.Gravitation);
        bool transit = !terminal && snapshot["navigation"]?.GetValue<bool>() == true && field?.Count > 0;
        var prediction = new VanillaPrediction(snapshot);
        VanillaPrediction dryPrediction = null;
        var dryFrames = new List<VanillaPrediction.MovementSample>();
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
        if (player.slowFall)
        {
            candidates.Add(("feather_glide", Enumerable.Repeat(direction | 8, Horizon).ToArray()));
            foreach (int stop in new[] { 12, 36, 60 })
                candidates.Add(($"feather_brake_{stop}", Enumerable.Range(0, Horizon)
                    .Select(i => (i < stop ? direction : 0) | 8).ToArray()));
        }
        int travelCandidates = candidates.Count;
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
        foreach (int hold in new[] { 6, 18, 60 })
        foreach (int stop in new[] { 12, 36, 60 })
            candidates.Add(($"jump_brake_{hold}_{stop}", Enumerable.Range(0, Horizon)
                .Select(i => (i < stop ? direction : 0) | (i < hold ? 4 : 0)).ToArray()));
        int reverse = direction == 2 ? 1 : 2;
        foreach (int stop in new[] { 12, 36, 60 })
            candidates.Add(($"reverse_brake_{stop}", Enumerable.Range(0, Horizon).Select(i => i < stop ? reverse : 0).ToArray()));
        foreach (int retreat in new[] { 12, 30, 48 })
        foreach (int hold in new[] { 18, 60 })
            candidates.Add(($"retreat_{retreat}_{hold}", Enumerable.Range(0, Horizon)
                .Select(i => (i < retreat ? reverse : direction) | (i >= retreat && i < retreat + hold ? 4 : 0)).ToArray()));
        foreach (int delay in new[] { 6, 18, 36, 54, 72, 96, 120, 144, 162 })
            candidates.Add(($"landing_jump_{delay}", Enumerable.Range(0, Horizon)
                .Select(i => direction | (i >= delay && i < delay + 12 ? 4 : 0)).ToArray()));
        if (gravity)
            candidates.Add(("invert", Enumerable.Range(0, Horizon).Select(i => direction | (i == 0 ? 8 : 0)).ToArray()));
        var diagnostics = new JsonArray();
        double best = double.PositiveInfinity;
        int[] chosen = Array.Empty<int>();
        string selected = "none";
        JsonObject selectedForecast = null;
        double chosenProgress = 0;
        bool selectedAdvances = false;
        bool selectedContact = false;
        bool selectedFlows = false;
        Vector2 selectedEnd = player.position;
        string searchEnd = "exhausted";
        bool goalSupported = player.gravDir == 1 && VanillaContactRegion.Supported(goal, player.width, player.height);
        bool seekContact = transit && goalSupported
            && !VanillaContactRegion.ConnectedSupport(player.position, goal, player.width, player.height);
        double Distance(Vector2 position)
        {
            if (field == null) return Vector2.Distance(goal, position);
            var cell = FootCell(position, player.width, player.height);
            return field.TryGetValue(cell, out int cost) ? cost * 16d : missing + Vector2.Distance(goal, position);
        }
        double startDistance = Distance(player.position);
        double restoreMs = 0, updateMs = 0, observeMs = 0, cleanupMs = 0;
        var frames = new List<VanillaPrediction.MovementSample>(Horizon);
        bool Forward((string name, int[] keys) candidate) => candidate.keys.All(key => (key & 3) == direction);
        try
        {
        int primaryCount = candidates.Count;
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            bool forward = Forward(candidate);
            var forecast = prediction.Predict(candidate.keys, false, restoreAfter: false, samples: frames, stopOnDamage: true);
            var timing = forecast["timing"];
            restoreMs += timing["restore_ms"].GetValue<double>();
            updateMs += timing["update_ms"].GetValue<double>();
            observeMs += timing["observe_ms"].GetValue<double>();
            cleanupMs += timing["cleanup_ms"].GetValue<double>();
            int damage = Math.Max(0, hp - frames.Min(frame => frame.Hp));
            bool ceilingLoop = RepeatsCeilingContact(frames);
            bool safe = forecast["outcome"].GetValue<string>() == "complete"
                && damage == 0 && frames.All(frame => !frame.Dead) && !ceilingLoop;
            double settlementMs = 0;
            int settlementFrames = 0;
            double dryCheckMs = 0;
            int dryDamage = 0;
            int coastDamage = 0;
            double coastMs = 0;
            if (safe && (frames[^1].Grounded || frames[^1].Velocity.Y == 0))
            {
                long settlementStart = Stopwatch.GetTimestamp();
                var settled = Main.player[player.whoAmI];
                VanillaPrediction.Advance(settled, VanillaKeys.None);
                settlementMs = Stopwatch.GetElapsedTime(settlementStart).TotalMilliseconds;
                updateMs += settlementMs; settlementFrames = 1;
                damage = Math.Max(damage, hp - settled.statLife);
                safe = damage == 0 && !settled.dead;
            }
            if (transit && safe)
            {
                var end = frames[^1];
                safe = end.Grounded || end.Velocity.Y == 0 || end.WingTime > 0 || end.AvailableJumps > 0
                    || Main.player[player.whoAmI].slowFall;
            }
            var scoreEnd = frames[^1].Position;
            int landingFrames = 0;
            if (safe && snapshot["navigation"]?.GetValue<bool>() == true
                && wings <= 0 && !gravity && !Main.player[player.whoAmI].slowFall)
            {
                long landingStart = Stopwatch.GetTimestamp();
                var landing = Main.player[player.whoAmI];
                while (landingFrames < Horizon && (landing.velocity.Y != 0 || !VanillaTerrainPlan.HasSupport(landing)))
                {
                    VanillaPrediction.Advance(landing, VanillaKeys.None);
                    landingFrames++;
                    if (landing.dead || landing.statLife < hp) { safe = false; break; }
                }
                updateMs += Stopwatch.GetElapsedTime(landingStart).TotalMilliseconds;
                safe &= landing.velocity.Y == 0 && VanillaTerrainPlan.HasSupport(landing);
                scoreEnd = landing.position;
            }
            bool progress = safe && forward && (frames[^1].Position.X - player.position.X) * (direction == 2 ? 1 : -1) >= 16;
            bool reachedContact = seekContact && safe && frames[^1].Velocity.Y == 0
                && VanillaContactRegion.ConnectedSupport(frames[^1].Position, goal, player.width, player.height);
            double score = double.PositiveInfinity;
            if (safe)
            {
                double advance = startDistance - Distance(scoreEnd);
                bool advances = !transit || advance > .01 || reachedContact;
                score = reachedContact ? Vector2.Distance(goal, frames[^1].Position)
                    : transit ? Distance(scoreEnd) + .1 * frames.Average(frame => Distance(frame.Position))
                    : Distance(scoreEnd);
                score += Math.Max(0, wings - frames[^1].WingTime) * .02;
                score += Math.Max(0, jumps - frames[^1].AvailableJumps) * 2;
                bool sameEnd = Vector2.DistanceSquared(scoreEnd, selectedEnd) <= 4;
                bool idleDominates = sameEnd && !advances && !selectedAdvances && selected == "wait"
                    && advance <= chosenProgress + .01;
                if (!idleDominates && (reachedContact && !selectedContact || reachedContact == selectedContact
                    && (advances && !selectedAdvances || advances == selectedAdvances && score < best - .01
                        || sameEnd && Math.Abs(score - best) <= .01
                            && candidate.keys.Count(key => (key & 4) != 0) < chosen.Count(key => (key & 4) != 0)
                        || sameEnd && !advances && !selectedAdvances && candidate.name == "wait" && advance >= chosenProgress - .01)))
                {
                    if (transit)
                    {
                        long coastStart = Stopwatch.GetTimestamp();
                        var coasting = Main.player[player.whoAmI];
                        for (int i = 0; i < 90; i++)
                        {
                            VanillaPrediction.Advance(coasting, VanillaKeys.None);
                            coastDamage = Math.Max(coastDamage, hp - coasting.statLife);
                            if (coastDamage > 0 || coasting.dead) { safe = false; break; }
                        }
                        coastMs = Stopwatch.GetElapsedTime(coastStart).TotalMilliseconds;
                        updateMs += coastMs;
                    }
                    if (safe && snapshot["navigation"]?.GetValue<bool>() == true && !player.noFallDmg && frames.Any(frame => frame.Wet))
                    {
                        long dryStart = Stopwatch.GetTimestamp();
                        if (dryPrediction == null)
                        {
                            var dry = snapshot.DeepClone().AsObject();
                            var tiles = VanillaFrame.ReadTiles(dry);
                            for (int i = 0; i < tiles.Length; i += 10)
                                if (tiles[i + 8] == Terraria.ID.LiquidID.Water) tiles[i + 9] = 0;
                            dry["tiles"] = VanillaFrame.PackInts(tiles);
                            dryPrediction = new VanillaPrediction(dry);
                        }
                        var dryResult = dryPrediction.Predict(candidate.keys, false, restoreAfter: false,
                            samples: dryFrames, stopOnDamage: true);
                        dryDamage = Math.Max(0, hp - dryFrames.Min(frame => frame.Hp));
                        safe = dryResult["outcome"].GetValue<string>() == "complete" && dryDamage == 0;
                        if (transit && safe)
                        {
                            var dryEnd = dryFrames[^1];
                            safe = dryEnd.Grounded || dryEnd.Velocity.Y == 0 || dryEnd.WingTime > 0 || dryEnd.AvailableJumps > 0
                                || Main.player[player.whoAmI].slowFall;
                        }
                        if (safe && (dryFrames[^1].Grounded || dryFrames[^1].Velocity.Y == 0))
                        {
                            var landed = Main.player[player.whoAmI];
                            VanillaPrediction.Advance(landed, VanillaKeys.None);
                            dryDamage = Math.Max(dryDamage, hp - landed.statLife);
                            safe = dryDamage == 0 && !landed.dead;
                        }
                        dryCheckMs = Stopwatch.GetElapsedTime(dryStart).TotalMilliseconds;
                        if (!safe) progress = false;
                    }
                    if (safe)
                    {
                        best = score; selected = candidate.name; chosen = candidate.keys;
                        selectedEnd = scoreEnd;
                        selectedAdvances = advances; selectedContact = reachedContact;
                        chosenProgress = reachedContact ? Math.Max(advance, Vector2.Distance(player.position, frames[^1].Position)) : advance;
                        selectedFlows = frames.TakeLast(12).All(frame =>
                            frame.Velocity.X * (direction == 2 ? 1 : -1) > .1f);
                    }
                }
            }
            diagnostics.Add(new JsonObject { ["name"] = candidate.name, ["safe"] = safe, ["hp_loss"] = damage,
                ["repeated_ceiling_contact"] = ceilingLoop,
                ["score_end_x"] = scoreEnd.X, ["score_end_y"] = scoreEnd.Y, ["landing_frames"] = landingFrames,
                ["score_progress"] = startDistance - Distance(scoreEnd),
                ["end_x"] = frames[^1].Position.X, ["end_y"] = frames[^1].Position.Y,
                ["field_progress"] = startDistance - Distance(frames[^1].Position),
                ["goal_distance"] = Vector2.Distance(goal, frames[^1].Position),
                ["goal_contact"] = reachedContact && safe,
                ["coast_ms"] = coastMs, ["coast_hp_loss"] = coastDamage,
                ["dry_check_ms"] = dryCheckMs, ["dry_hp_loss"] = dryDamage,
                ["simulated_frames"] = frames.Count,
                ["settlement_frames"] = settlementFrames, ["settlement_ms"] = settlementMs,
                ["score"] = safe ? JsonValue.Create(score) : null, ["forward_progress"] = progress, ["outcome"] = forecast["outcome"].DeepClone(),
                ["elapsed_ms"] = forecast["elapsed_ms"].DeepClone(), ["timing"] = forecast["timing"].DeepClone() });
            if (forecast["dynamic_spawn"] != null) diagnostics[^1]["dynamic_spawn"] = forecast["dynamic_spawn"].DeepClone();
            if (continuationOnly)
            {
                if (!selectedAdvances) { chosen = Array.Empty<int>(); selected = "none"; }
                break;
            }
            if (preferContinuation && candidate.name == "continue" && safe && chosenProgress > 0) break;
            if (transit && candidateIndex >= travelCandidates - 1 && selectedAdvances && selectedFlows
                && chosenProgress >= player.width * 4)
            {
                searchEnd = "flowing_route";
                break;
            }
            if (candidateIndex >= primaryCount && selectedAdvances) break;
            if (safe && candidate.name is "walk" or "continue"
                && (candidate.name != "continue" || jumps == 0 && wings == 0)
                && frames.All(frame => frame.Grounded)
                && frames.Skip(6).All(frame => Math.Abs(frame.Velocity.X) > .1f))
            {
                var end = frames[^1];
                var endPosition = end.Position;
                bool towardGoal = Vector2.Distance(goal, endPosition) < Vector2.Distance(goal, player.position) - 16;
                if (field != null)
                {
                    var startCell = FootCell(player.position, player.width, player.height);
                    var endCell = FootCell(endPosition, player.width, player.height);
                    towardGoal = field.TryGetValue(startCell, out int startCost) && field.TryGetValue(endCell, out int endCost)
                        && endCost < startCost;
                }
                if (towardGoal) break;
            }
            if (transit && candidateIndex == primaryCount - 1 && !selectedAdvances)
            {
                if (chosen.Length > 0 && terrainContinuation?.Invoke() == true) break;
                foreach (int stop in new[] { 24, 48 })
                    candidates.Add(($"brake_refined_{stop}", Enumerable.Range(0, Horizon)
                        .Select(i => i < stop ? direction : 0).ToArray()));
                if (jumps > 0)
                foreach (int release in new[] { 12, 30 })
                foreach (int delay in new[] { 0, 12 })
                foreach (int travel in new[] { 36, 60, 84 })
                    candidates.Add(($"relaunch_brake_{release}_{delay}_{travel}", Enumerable.Range(0, Horizon)
                        .Select(i => (i >= delay && i < delay + travel ? direction : 0)
                            | (i < 60 && i != release ? 4 : 0)).ToArray()));
                foreach (int hold in new[] { 6, 18, 60 })
                foreach (int stop in new[] { 12, 36, 60 })
                    candidates.Add(($"reverse_jump_brake_{hold}_{stop}", Enumerable.Range(0, Horizon)
                        .Select(i => (i < stop ? reverse : 0) | (i < hold ? 4 : 0)).ToArray()));
                if (jumps > 0)
                foreach (int travelDirection in new[] { direction, reverse })
                foreach (int stop in new[] { 12, 36, 60 })
                foreach (int delay in new[] { 36, 72, 108, 144 })
                    candidates.Add(($"landing_brake_{travelDirection}_{stop}_{delay}", Enumerable.Range(0, Horizon)
                        .Select(i => (i < stop ? travelDirection : 0) | (i >= delay && i < delay + 12 ? 4 : 0)).ToArray()));
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
        var goalRange = goalSupported ? VanillaContactRegion.SupportRange(goal, player.width, player.height) : Vector2.Zero;
        return new JsonObject { ["status"] = chosen.Length == 0 ? "no_safe_candidate" : "planned", ["selected"] = selected,
            ["goal_supported"] = goalSupported, ["goal_contact"] = selectedContact,
            ["goal_contact_region"] = goalSupported ? new JsonArray(goalRange.X, goalRange.Y, goal.Y) : null,
            ["route_progress"] = chosenProgress, ["search_end"] = searchEnd,
            ["controls"] = new JsonArray(chosen.Select(key => JsonValue.Create(key)).ToArray()),
            ["forecast"] = selectedForecast, ["candidates"] = diagnostics, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds,
            ["timing"] = new JsonObject { ["restore_ms"] = restoreMs, ["update_ms"] = updateMs,
                ["observe_ms"] = observeMs, ["cleanup_ms"] = cleanupMs } };
    }

    internal static Dictionary<(int, int), int> ReadGuidance(JsonNode guidance)
    {
        if (guidance == null) return null;
        if (guidance is JsonArray rows)
            return rows.ToDictionary(row => (row[0].GetValue<int>(), row[1].GetValue<int>()), row => row[2].GetValue<int>());
        var field = new Dictionary<(int, int), int>();
        var values = VanillaFrame.UnpackInts(guidance.GetValue<string>());
        for (int i = 0; i < values.Length; i += 3) field.Add((values[i], values[i + 1]), values[i + 2]);
        return field;
    }
}
