using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class VanillaTerrainPlan
{
    public static JsonObject Build(JsonObject request)
    {
        VanillaFrame.RequireWorker();
        var state = request["snapshot"].AsObject();
        try
        {
            var player = VanillaFrame.RestoreInWorker(state);
            var anchor = VanillaPrediction.Observe(player, false);
            int x = request["tile_x"].GetValue<int>(), y = request["tile_y"].GetValue<int>();
            int type = request["tile_type"].GetValue<int>();
            if (type < 0)
            {
                var targets = request["tiles"] as JsonArray ?? new JsonArray(new JsonArray(x, y));
                foreach (var target in targets)
                {
                    x = target[0].GetValue<int>(); y = target[1].GetValue<int>();
                    if (targets.Count > 1 && y >= (int)Math.Floor(player.Bottom.Y / 16))
                        return new JsonObject { ["status"] = "batch_changes_support" };
                    if (!LavaSafety.CanMine(x, y) || !WorldGen.CanKillTile(x, y))
                        return new JsonObject { ["status"] = "unsafe_mining" };
                    int originalType = Main.tile[x, y].TileType;
                    WorldGen.KillTile(x, y, noItem: true);
                    if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType != originalType)
                        WorldGen.KillTile(x, y, noItem: true);
                    if (Main.tile[x, y].HasTile) return new JsonObject { ["status"] = "mining_rejected" };
                }
            }
            else
            {
                if (LavaSafety.IsLava(x, y)) return new JsonObject { ["status"] = "unsafe_placement" };
                WorldGen.PlaceTile(x, y, type, mute: true, plr: player.whoAmI);
                if (!Main.tile[x, y].HasTile || Main.tile[x, y].TileType != type)
                    return new JsonObject { ["status"] = "placement_rejected" };
            }
            var area = state["area"].AsArray();
            var edited = VanillaFrame.Capture(player, new Rectangle(area[0].GetValue<int>(), area[1].GetValue<int>(),
                area[2].GetValue<int>(), area[3].GetValue<int>()), true);
            int wait = request["wait_frames"].GetValue<int>();
            if (wait < 1 || wait > 90) throw new ArgumentOutOfRangeException("wait_frames");
            var prefix = new VanillaPrediction(edited).Predict(new int[wait], false, true);
            if (prefix["outcome"].GetValue<string>() != "complete"
                || prefix["frames"].AsArray().Any(frame => frame["dead"].GetValue<bool>()
                    || frame["hp"].GetValue<int>() < player.statLife))
                return new JsonObject { ["status"] = "unsafe_edit_wait" };
            var plan = new VanillaLocalPlanner().Plan(prefix["end_snapshot"].AsObject(),
                new Vector2(request["x"].GetValue<float>(), request["y"].GetValue<float>()),
                guidance: request["guidance"], routeDirection: request["route_direction"].GetValue<int>());
            if (plan["status"].GetValue<string>() != "planned") return plan;
            var controls = new JsonArray();
            for (int i = 0; i < wait; i++) controls.Add(0);
            foreach (var control in plan["controls"].AsArray()) controls.Add(control.DeepClone());
            var frames = new JsonArray();
            foreach (var frame in prefix["frames"].AsArray()) frames.Add(frame.DeepClone());
            foreach (var frame in plan["forecast"]["frames"].AsArray()) frames.Add(frame.DeepClone());
            plan["controls"] = controls;
            plan["forecast"] = new JsonObject { ["frames"] = frames };
            plan["anchor"] = anchor;
            plan["context"] = edited;
            plan["wait_frames"] = wait;
            plan["selected"] = "terrain:" + plan["selected"].GetValue<string>();
            return plan;
        }
        finally { VanillaFrame.RestoreInWorker(state); }
    }
}
