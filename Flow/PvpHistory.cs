using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public sealed class PvpHistory
{
    public readonly record struct Sample(ulong Tick, Vector2 Self, Vector2 Target, Vector2 Velocity,
        int SelfHp, int TargetHp, bool Firing, bool Clear, int Projectiles);
    readonly Queue<Sample> samples = new();
    readonly HashSet<(int Slot, int Identity, int Owner)> knownShots = new();
    public void Reset()
    {
        samples.Clear(); knownShots.Clear();
        foreach (var shot in Main.projectile)
            if (shot.active) knownShots.Add((shot.whoAmI, shot.identity, shot.owner));
    }

    public void Record(Player self, Player target, bool clear)
    {
        ulong tick = Main.GameUpdateCount;
        if (samples.Count > 0 && samples.Last().Tick == tick) return;
        int created = 0;
        var active = new HashSet<(int Slot, int Identity, int Owner)>();
        foreach (var shot in Main.projectile)
        {
            if (!shot.active) continue;
            var id = (shot.whoAmI, shot.identity, shot.owner); active.Add(id);
            if (shot.owner == self.whoAmI && shot.damage > 0 && !knownShots.Contains(id)) created++;
        }
        knownShots.Clear(); knownShots.UnionWith(active);
        samples.Enqueue(new(tick, self.Center, target.Center, target.velocity, self.statLife, target.statLife,
            self.controlUseItem, clear, created));
        while (samples.Count > 0 && tick - samples.Peek().Tick >= 180) samples.Dequeue();
    }

    public JsonObject ToJson()
    {
        var data = samples.ToArray();
        if (data.Length == 0) return new JsonObject { ["samples"] = new JsonArray(), ["confirmed_hits"] = null };
        var changes = new HashSet<int>();
        int reversals = 0;
        for (int i = 1; i < data.Length; i++)
        {
            bool reverse = Math.Abs(data[i - 1].Velocity.X) > .5f && Math.Abs(data[i].Velocity.X) > .5f
                && Math.Sign(data[i - 1].Velocity.X) != Math.Sign(data[i].Velocity.X);
            bool verticalChange = Math.Sign(data[i - 1].Velocity.Y) != Math.Sign(data[i].Velocity.Y);
            if (reverse) reversals++;
            if (reverse || verticalChange) { changes.Add(i - 1); changes.Add(i); }
        }
        return new JsonObject
        {
            ["from_tick"] = data[0].Tick, ["to_tick"] = data[^1].Tick, ["sampled_frames"] = data.Length,
            ["self_hp_delta"] = data[^1].SelfHp - data[0].SelfHp, ["target_hp_delta"] = data[^1].TargetHp - data[0].TargetHp,
            ["fire_input_frames"] = data.Count(sample => sample.Firing), ["clear_shot_frames"] = data.Count(sample => sample.Clear),
            ["observed_own_projectiles"] = data.Sum(sample => sample.Projectiles), ["confirmed_hits"] = null,
            ["self_displacement"] = Vector2.Distance(data[0].Self, data[^1].Self), ["horizontal_reversals"] = reversals,
            ["prediction_warning"] = "An unannounced dash or instantaneous reversal cannot be predicted from past motion.",
            ["samples"] = new JsonArray(data.Select((sample, index) => (sample, index))
                .Where(pair => pair.index % 6 == 0 || pair.index == data.Length - 1 || changes.Contains(pair.index))
                .Select(pair => (JsonNode)new JsonObject { ["tick"] = pair.sample.Tick,
                    ["self_x"] = pair.sample.Self.X, ["self_y"] = pair.sample.Self.Y,
                    ["target_x"] = pair.sample.Target.X, ["target_y"] = pair.sample.Target.Y,
                    ["target_vx"] = pair.sample.Velocity.X, ["target_vy"] = pair.sample.Velocity.Y }).ToArray())
        };
    }

    public float? FallingAcceleration()
    {
        var recent = samples.TakeLast(4).ToArray();
        if (recent.Length < 4 || recent.Any(sample => sample.Velocity.Y <= 0)) return null;
        var acceleration = recent.Zip(recent.Skip(1), (a, b) => b.Tick > a.Tick
            ? (b.Velocity.Y - a.Velocity.Y) / (b.Tick - a.Tick) : 0).ToArray();
        float average = acceleration.Average();
        return average > .01f && acceleration.All(value => Math.Abs(value - average) < .05f) ? average : null;
    }
}
