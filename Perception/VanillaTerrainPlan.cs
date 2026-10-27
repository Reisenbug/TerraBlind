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
        if (first.y > supportY) return new[] { first };
        bool Supports((int x, int y) cell) => cell.y == supportY && cell.x * 16 < right && (cell.x + 1) * 16 > left;
        var sides = available.Where(cell => cell.y <= supportY && !Supports(cell)
            && cell.x * 16 < right + 16 && (cell.x + 1) * 16 > left - 16);
        var supports = available.Where(Supports);
        if (!Supports(first))
        {
            if (first.x * 16 >= right + 16 || (first.x + 1) * 16 <= left - 16) return new[] { first };
            return new[] { first }.Concat(sides).Distinct().ToArray();
        }
        return sides.Concat(new[] { first }).Concat(supports).Distinct().ToArray();
    }

    internal static bool HasSupport(Player player)
    {
        if (Collision.TileCollision(player.position, Vector2.UnitY, player.width, player.height).Y == 0) return true;
        return Collision.SlopeCollision(player.position + Vector2.UnitY, Vector2.UnitY,
            player.width, player.height, player.gravDir).Y <= player.position.Y;
    }

    static JsonObject SearchJumpPlacement(JsonObject request)
    {
        var state = request["snapshot"].AsObject();
        var player = VanillaFrame.RestoreInWorker(state);
        float startY = player.position.Y;
        int width = player.width, height = player.height;
        var schedules = new[] { 6, 18, 60, -18, -30, -45 };
        JsonObject best = null;
        foreach (int schedule in schedules)
        {
            var keys = Enumerable.Range(0, 180)
                .Select(i => schedule > 0 ? (i < schedule ? 4 : 0)
                    : (i < 90 && i != -schedule ? 4 : 0)).ToArray();
            var free = new VanillaPrediction(state).Predict(keys, false, stopOnDamage: true);
            if (free["outcome"]?.GetValue<string>() != "complete") continue;
            var frames = free["frames"].AsArray();
            var apex = frames.OrderBy(frame => frame["y"].GetValue<float>()).First();
            int x = (int)((apex["x"].GetValue<float>() + width / 2f) / 16);
            int firstY = (int)Math.Ceiling((apex["y"].GetValue<float>() + height) / 16);
            int lastY = (int)((startY + height) / 16) - 1;
            for (int y = firstY; y <= lastY; y++)
            {
                if (best != null && startY - (y * 16 - height) <= best["rise"].GetValue<float>()) break;
                player = VanillaFrame.RestoreInWorker(state);
                if (Main.tile[x, y].HasTile || LavaSafety.IsLava(x, y)) continue;
                WorldGen.PlaceTile(x, y, Terraria.ID.TileID.Platforms, mute: true, plr: player.whoAmI);
                if (!Main.tile[x, y].HasTile || Main.tile[x, y].TileType != Terraria.ID.TileID.Platforms) continue;
                var candidate = request.DeepClone().AsObject();
                candidate["tile_x"] = x; candidate["tile_y"] = y;
                var plan = JumpPlacement(candidate, keys);
                if (plan["status"]?.GetValue<string>() != "planned") continue;
                best = plan;
                break;
            }
        }
        return best ?? JumpPlacement(request);
    }

    static JsonObject JumpPlacement(JsonObject request, int[] controls = null)
    {
        var state = request["snapshot"].AsObject();
        var player = VanillaFrame.RestoreInWorker(state);
        int x = request["tile_x"].GetValue<int>(), y = request["tile_y"].GetValue<int>();
        int type = request["tile_type"].GetValue<int>();
        if (type != Terraria.ID.TileID.Platforms || player.velocity != Vector2.Zero || player.gravDir != 1
            || !player.releaseJump || !VanillaContactRegion.Supported(player.position, player.width, player.height))
            return new JsonObject { ["status"] = "unsafe_jump_place_start" };
        var anchor = VanillaPrediction.Observe(player, false);
        int hp = player.statLife;
        float startY = player.position.Y;
        var keys = controls ?? Enumerable.Range(0, 120).Select(i => i < 6 ? 4 : 0).ToArray();
        var fallback = new VanillaPrediction(state).Predict(keys, false, stopOnDamage: true);
        if (fallback["outcome"].GetValue<string>() != "complete")
            return new JsonObject { ["status"] = "unsafe_unplaced_jump" };
        var fallbackEnd = fallback["frames"].AsArray()[^1];
        if (fallbackEnd["vy"].GetValue<float>() != 0)
            return new JsonObject { ["status"] = "unsettled_unplaced_jump" };
        player = VanillaFrame.RestoreInWorker(state);
        int placedAt = -1;
        var validation = fallback["frames"].DeepClone().AsArray();
        for (int frame = 0; frame < keys.Length; frame++)
        {
            if (placedAt < 0 && player.velocity.Y < 0
                && !player.Hitbox.Intersects(new Rectangle(x * 16, y * 16, 16, 16)))
            {
                if (Main.tile[x, y].HasTile || LavaSafety.IsLava(x, y)) break;
                WorldGen.PlaceTile(x, y, type, mute: true, plr: player.whoAmI);
                if (!Main.tile[x, y].HasTile || Main.tile[x, y].TileType != type) break;
                placedAt = frame;
            }
            VanillaPrediction.Advance(player, (VanillaKeys)keys[frame]);
            validation.Add(VanillaPrediction.Observe(player, false));
            if (player.dead || player.statLife < hp)
                return new JsonObject { ["status"] = "unsafe_jump_place" };
        }
        if (placedAt < 0 || player.position.Y >= startY || player.velocity.Y != 0
            || Math.Abs(player.Bottom.Y - y * 16) > 0.1f
            || !VanillaContactRegion.Supported(player.position, player.width, player.height))
            return new JsonObject { ["status"] = "jump_place_does_not_raise_support" };
        return new JsonObject { ["status"] = "planned", ["jump_place"] = true,
            ["jump_frames"] = keys.TakeWhile(key => (key & 4) != 0).Count(),
            ["tile_x"] = x, ["tile_y"] = y,
            ["controls"] = new JsonArray(keys.Select(key => JsonValue.Create(key)).ToArray()),
            ["place_after"] = placedAt, ["anchor"] = anchor, ["rise"] = startY - player.position.Y,
            ["validation_frames"] = validation };
    }

    public static JsonObject Build(JsonObject request)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timing = new JsonObject();
        var result = BuildTimed(request, timing, clock);
        timing["total_ms"] = clock.Elapsed.TotalMilliseconds;
        result["terrain_timing"] = timing;
        return result;
    }

    static JsonObject BuildTimed(JsonObject request, JsonObject timing, System.Diagnostics.Stopwatch clock)
    {
        VanillaFrame.RequireWorker();
        var state = request["snapshot"].AsObject();
        double previous = 0;
        void Mark(string name)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            timing[name] = now - previous;
            previous = now;
        }
        try
        {
            if (request["jump_place"]?.GetValue<bool>() == true)
                return request["search_jump_place"]?.GetValue<bool>() == true ? SearchJumpPlacement(request) : JumpPlacement(request);
            var player = VanillaFrame.RestoreInWorker(state);
            Mark("restore_ms");
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
                Mark("arrival_ms");
                if (request["skip_unedited"]?.GetValue<bool>() != true)
                {
                    var movement = new VanillaLocalPlanner().Plan(arrivalState,
                        new Vector2(request["x"].GetValue<float>(), request["y"].GetValue<float>()),
                        guidance: request["guidance"], routeDirection: request["route_direction"].GetValue<int>());
                    Mark("unedited_plan_ms");
                    if (movement["status"].GetValue<string>() == "planned" && movement["route_progress"].GetValue<double>() > 0)
                    {
                        movement["terrain_required"] = false;
                        movement["context"] = arrivalState.DeepClone();
                        movement["anchor"] = VanillaPrediction.Observe(player, false);
                        movement["wait_frames"] = 0;
                        return movement;
                    }
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
            Mark("edit_and_wait_ms");
            if (prefix["outcome"].GetValue<string>() != "complete"
                || prefix["frames"].AsArray().Any(frame => frame["dead"].GetValue<bool>()
                    || frame["hp"].GetValue<int>() < player.statLife))
                return new JsonObject { ["status"] = "unsafe_edit_wait" };
            JsonArray nextPlacement = null;
            var settled = prefix["end_snapshot"].AsObject();
            bool TryNextPlacement()
            {
                double start = clock.Elapsed.TotalMilliseconds;
                var standing = VanillaFrame.RestoreInWorker(settled);
                try
                {
                    if (type >= 0 || request["y"].GetValue<float>() >= standing.position.Y
                        || !standing.inventory.Any(item => item.stack > 0 && item.createTile == Terraria.ID.TileID.Platforms))
                        return false;
                    int supportY = (int)(standing.Bottom.Y / 16) - 1;
                    int left = (int)(standing.Left.X / 16), right = (int)((standing.Right.X - 1) / 16);
                    for (int column = left; column <= right; column++)
                    {
                        var step = JumpPlacement(new JsonObject { ["snapshot"] = settled.DeepClone(),
                            ["tile_x"] = column, ["tile_y"] = supportY, ["tile_type"] = (int)Terraria.ID.TileID.Platforms });
                        if (step["status"]?.GetValue<string>() != "planned") continue;
                        nextPlacement = new JsonArray(column, supportY);
                        return true;
                    }
                    return false;
                }
                finally
                {
                    VanillaFrame.RestoreInWorker(settled);
                    timing["next_placement_ms"] = clock.Elapsed.TotalMilliseconds - start;
                }
            }
            var plan = new VanillaLocalPlanner().Plan(settled,
                new Vector2(request["x"].GetValue<float>(), request["y"].GetValue<float>()),
                guidance: request["guidance"], routeDirection: request["route_direction"].GetValue<int>(),
                terrainContinuation: TryNextPlacement);
            Mark("edited_plan_ms");
            if (plan["status"].GetValue<string>() != "planned") return plan;
            double movementProgress = plan["route_progress"].GetValue<double>(), editProgress = 0;
            var field = VanillaLocalPlanner.ReadGuidance(request["guidance"]);
            var waitEnd = prefix["frames"].AsArray()[^1];
            var startCell = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
            var waitCell = VanillaLocalPlanner.FootCell(new Vector2(waitEnd["x"].GetValue<float>(), waitEnd["y"].GetValue<float>()),
                player.width, player.height);
            if (field != null && field.TryGetValue(startCell, out int startCost) && field.TryGetValue(waitCell, out int waitCost))
            {
                editProgress = (startCost - waitCost) * 16d;
                if (startCell == waitCell && field.TryGetValue((startCell.x, startCell.y + 1), out int belowCost)
                    && belowCost < startCost)
                    editProgress += (waitEnd["y"].GetValue<float>() - player.position.Y) * (startCost - belowCost);
            }
            plan["movement_progress"] = movementProgress;
            plan["edit_progress"] = editProgress;
            plan["route_progress"] = movementProgress + editProgress;
            if (request["guidance"] != null && plan["route_progress"].GetValue<double>() <= 0)
            {
                if (nextPlacement == null && timing["next_placement_ms"] == null) TryNextPlacement();
                if (nextPlacement == null)
                {
                    plan["status"] = "edit_does_not_open_route";
                    return plan;
                }
                plan["next_jump_place"] = nextPlacement;
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
        finally
        {
            double start = clock.Elapsed.TotalMilliseconds;
            VanillaFrame.RestoreInWorker(state);
            timing["cleanup_ms"] = clock.Elapsed.TotalMilliseconds - start;
        }
    }
}
