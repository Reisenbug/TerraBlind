using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class VanillaTerrainPlan
{
    public static bool IsRouteObstacle(Tile tile, bool belowFeet)
        => tile.HasTile && !tile.IsActuated
            && (Terraria.ID.TileID.Sets.TouchDamageImmediate[tile.TileType] > 0
                || !belowFeet && (Main.tileSolid[tile.TileType] || Main.tileCut[tile.TileType]));

    public static (int x, int y)[] MiningBatch((int x, int y) first, (int x, int y)[] available,
        float left, float right, int supportY)
    {
        if (first.y != supportY) return new[] { first };
        bool Supports((int x, int y) cell) => cell.y == supportY && cell.x * 16 < right && (cell.x + 1) * 16 > left;
        var sides = available.Where(cell => cell.y <= supportY && !Supports(cell)
            && cell.x * 16 < right + 16 && (cell.x + 1) * 16 > left - 16);
        var supports = available.Where(Supports);
        return sides.Concat(new[] { first }).Concat(supports).Distinct().ToArray();
    }

    internal static bool HasSupport(Player player)
    {
        if (Collision.TileCollision(player.position, Vector2.UnitY, player.width, player.height).Y == 0) return true;
        return Collision.SlopeCollision(player.position + Vector2.UnitY, Vector2.UnitY,
            player.width, player.height, player.gravDir).Y <= player.position.Y;
    }

    public static JsonObject Build(JsonObject request)
    {
        VanillaFrame.RequireWorker();
        var state = request["snapshot"].AsObject();
        try
        {
            var player = VanillaFrame.RestoreInWorker(state);
            if (request["arrival"] is JsonArray arrival)
            {
                if (arrival.Count < 1 || arrival.Count > 90) throw new ArgumentOutOfRangeException("arrival");
                var approach = new VanillaPrediction(state).Predict(arrival.Select(key => key.GetValue<int>()).ToArray(),
                    false, true, stopOnDamage: true);
                if (approach["outcome"].GetValue<string>() != "complete")
                    return new JsonObject { ["status"] = "unsafe_arrival" };
                player = VanillaFrame.RestoreInWorker(approach["end_snapshot"].AsObject());
                if (player.velocity != Vector2.Zero) return new JsonObject { ["status"] = "arrival_not_settled" };
                var arrivalState = approach["end_snapshot"].AsObject();
                var movement = new VanillaLocalPlanner().Plan(arrivalState,
                    new Vector2(request["x"].GetValue<float>(), request["y"].GetValue<float>()),
                    guidance: request["guidance"], routeDirection: request["route_direction"].GetValue<int>());
                if (movement["status"].GetValue<string>() == "planned" && movement["route_progress"].GetValue<double>() > 0)
                {
                    movement["terrain_required"] = false;
                    movement["context"] = arrivalState.DeepClone();
                    movement["anchor"] = VanillaPrediction.Observe(player, false);
                    movement["wait_frames"] = 0;
                    return movement;
                }
            }
            var anchor = VanillaPrediction.Observe(player, false);
            int x = request["tile_x"].GetValue<int>(), y = request["tile_y"].GetValue<int>();
            int type = request["tile_type"].GetValue<int>();
            if (type < 0)
            {
                var targets = request["tiles"] as JsonArray ?? new JsonArray(new JsonArray(x, y));
                for (int index = 0; index < targets.Count; index++)
                {
                    var target = targets[index];
                    x = target[0].GetValue<int>(); y = target[1].GetValue<int>();
                    if (!Main.tile[x, y].HasTile) continue;
                    bool solid = Main.tileSolid[Main.tile[x, y].TileType] || Main.tileSolidTop[Main.tile[x, y].TileType];
                    if (targets.Count > 1 && solid && y > (int)Math.Floor(player.Bottom.Y / 16))
                        return new JsonObject { ["status"] = "batch_changes_multiple_support_rows" };
                    bool lavaSafe = LavaSafety.CanMine(x, y), killable = WorldGen.CanKillTile(x, y);
                    if (!lavaSafe || !killable)
                        return new JsonObject { ["status"] = "unsafe_mining", ["x"] = x, ["y"] = y,
                            ["lava_safe"] = lavaSafe, ["killable"] = killable, ["tile_type"] = Main.tile[x, y].TileType };
                    int originalType = Main.tile[x, y].TileType;
                    WorldGen.KillTile(x, y, noItem: true);
                    if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType != originalType)
                        WorldGen.KillTile(x, y, noItem: true);
                    if (Main.tile[x, y].HasTile) return new JsonObject { ["status"] = "mining_rejected" };
                    if (solid && index < targets.Count - 1 && !HasSupport(player))
                        return new JsonObject { ["status"] = "batch_loses_support_before_final_edit" };
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
            double movementProgress = plan["route_progress"].GetValue<double>(), editProgress = 0;
            var field = VanillaLocalPlanner.ReadGuidance(request["guidance"]);
            var waitEnd = prefix["frames"].AsArray()[^1];
            var startCell = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
            var waitCell = VanillaLocalPlanner.FootCell(new Vector2(waitEnd["x"].GetValue<float>(), waitEnd["y"].GetValue<float>()),
                player.width, player.height);
            if (field != null && field.TryGetValue(startCell, out int startCost) && field.TryGetValue(waitCell, out int waitCost))
                editProgress = (startCost - waitCost) * 16d;
            plan["movement_progress"] = movementProgress;
            plan["edit_progress"] = editProgress;
            plan["route_progress"] = movementProgress + editProgress;
            if (request["guidance"] != null && plan["route_progress"].GetValue<double>() <= 0)
            {
                plan["status"] = "edit_does_not_open_route";
                return plan;
            }
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
