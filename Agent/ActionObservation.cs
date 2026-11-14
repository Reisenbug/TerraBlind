using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class ActionObservation : ModSystem
{
    sealed class Trial
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly long Tick = Main.GameUpdateCount;
        public readonly int Hp = Main.LocalPlayer.statLife;
        public readonly JsonArray Inputs = new(), Frames = new(), Hits = new();
        public readonly Dictionary<int, JsonObject> Shots = new();
        public JsonObject LastInput;
        public int Samples;
    }

    static Trial active;

    public override void Load()
    {
        AgentTools.BeginActObservation = Begin;
        AgentTools.EndActObservation = End;
    }

    public override void Unload()
    {
        active = null;
        AgentTools.BeginActObservation = AgentTools.EndActObservation = null;
    }

    static JsonObject Begin()
    {
        if (active != null) return new JsonObject { ["error"] = "observation_busy" };
        active = new Trial();
        return new JsonObject { ["id"] = active.Id };
    }

    public static void RecordInput(Player player)
    {
        if (active == null || player != Main.LocalPlayer) return;
        var input = new JsonObject { ["left"] = player.controlLeft, ["right"] = player.controlRight,
            ["jump"] = player.controlJump, ["use"] = player.controlUseItem, ["use_tile"] = player.controlUseTile,
            ["slot"] = player.selectedItem, ["aim_x"] = Main.mouseX + Main.screenPosition.X,
            ["aim_y"] = Main.mouseY + Main.screenPosition.Y };
        if (active.LastInput != null && JsonNode.DeepEquals(active.LastInput, input)) return;
        active.LastInput = input.DeepClone().AsObject();
        input["frame"] = (long)Main.GameUpdateCount - active.Tick;
        active.Inputs.Add(input);
    }

    internal static void Hit(NPC npc, int owner, int projectile, int damage)
    {
        if (active == null || owner != Main.myPlayer) return;
        active.Hits.Add(new JsonObject { ["frame"] = (long)Main.GameUpdateCount - active.Tick,
            ["npc"] = npc.whoAmI, ["npc_type"] = npc.type, ["projectile_identity"] = projectile,
            ["damage"] = damage, ["hp_after"] = npc.life });
    }

    public override void PostUpdateEverything()
    {
        if (active == null || Main.gameMenu || active.Samples >= 1800) return;
        var player = Main.LocalPlayer;
        long frame = (long)Main.GameUpdateCount - active.Tick;
        var shots = new JsonArray();
        foreach (var shot in Main.projectile)
        {
            if (shot == null || !shot.active || shot.owner != player.whoAmI) continue;
            var point = new JsonObject { ["frame"] = frame, ["x"] = shot.Center.X, ["y"] = shot.Center.Y,
                ["vx"] = shot.velocity.X, ["vy"] = shot.velocity.Y,
                ["ai"] = new JsonArray(shot.ai.Select(value => JsonValue.Create(value)).ToArray()) };
            shots.Add(new JsonObject { ["identity"] = shot.identity, ["state"] = point.DeepClone() });
            if (!active.Shots.TryGetValue(shot.identity, out var history))
            {
                history = new JsonObject { ["identity"] = shot.identity, ["type"] = shot.type,
                    ["first_frame"] = frame, ["transitions"] = new JsonArray(), ["path"] = new JsonArray(),
                    ["max_distance_pixels"] = 0f };
                active.Shots.Add(shot.identity, history);
            }
            history["last_frame"] = frame;
            history["max_distance_pixels"] = Math.Max(history["max_distance_pixels"].GetValue<float>(),
                Vector2.Distance(shot.Center, player.MountedCenter));
            var changes = history["transitions"].AsArray();
            if (changes.Count == 0 || !JsonNode.DeepEquals(changes[^1]["ai"]?[0], point["ai"]?[0]))
                if (changes.Count < 48) changes.Add(point.DeepClone());
            if (frame % 6 == 0 && history["path"].AsArray().Count < 60) history["path"].AsArray().Add(point.DeepClone());
        }
        active.Frames.Add(new JsonObject { ["frame"] = frame, ["x"] = player.position.X, ["y"] = player.position.Y,
            ["vx"] = player.velocity.X, ["vy"] = player.velocity.Y, ["hp"] = player.statLife,
            ["channel"] = player.channel, ["use"] = player.controlUseItem, ["shots"] = shots });
        active.Samples++;
    }

    static JsonObject End()
    {
        var trial = active; active = null;
        if (trial == null) return new JsonObject { ["error"] = "observation_not_running" };
        var report = new JsonObject { ["id"] = trial.Id, ["ticks"] = (long)Main.GameUpdateCount - trial.Tick,
            ["sampled_frames"] = trial.Samples, ["sample_limit"] = 1800,
            ["hp_before"] = trial.Hp, ["hp_after"] = Main.LocalPlayer.statLife,
            ["inputs"] = trial.Inputs, ["projectiles"] = new JsonArray(trial.Shots.Values.Select(value => value.DeepClone()).ToArray()),
            ["hits"] = trial.Hits, ["damage"] = trial.Hits.Sum(hit => hit["damage"].GetValue<int>()) };
        string directory = Path.Combine(Main.SavePath, "TerraBlindAgent", "experiments");
        Directory.CreateDirectory(directory);
        var trace = report.DeepClone().AsObject(); trace["frames"] = trial.Frames;
        File.WriteAllText(Path.Combine(directory, trial.Id + ".json"), trace.ToJsonString());
        DiagLog.Write("[agent_experiment] " + new JsonObject { ["id"] = trial.Id,
            ["ticks"] = report["ticks"].DeepClone(), ["damage"] = report["damage"].DeepClone() }.ToJsonString());
        return report;
    }
}

public sealed class ActionObservationHits : GlobalNPC
{
    public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone)
        => ActionObservation.Hit(npc, projectile.owner, projectile.identity, damageDone);
    public override void OnHitByItem(NPC npc, Player player, Item item, NPC.HitInfo hit, int damageDone)
        => ActionObservation.Hit(npc, player.whoAmI, -1, damageDone);
}
