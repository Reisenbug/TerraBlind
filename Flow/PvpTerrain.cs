using System;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class PvpTerrain
{
    static bool InWorld(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;
    static bool Support(int x, int y)
    {
        if (!InWorld(x, y)) return false;
        var tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && (Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]);
    }

    public static JsonObject Trace(Vector2 from, Vector2 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) / 4f));
        Vector2 previous = from;
        for (int i = 1; i <= steps; i++)
        {
            Vector2 point = Vector2.Lerp(from, to, i / (float)steps);
            if (!Collision.CanHitLine(previous, 4, 4, point, 4, 4))
            {
                int x = (int)(point.X / 16), y = (int)(point.Y / 16);
                var result = new JsonObject { ["clear"] = false, ["sample_x"] = point.X, ["sample_y"] = point.Y,
                    ["collision_model"] = "four_pixel_line_probe" };
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (Support(x + dx, y + dy))
                    {
                        var tile = Main.tile[x + dx, y + dy];
                        result["blocker"] = new JsonObject { ["x"] = x + dx, ["y"] = y + dy,
                            ["type"] = (int)tile.TileType, ["slope"] = (int)tile.Slope, ["half_block"] = tile.IsHalfBlock,
                            ["location_precision"] = "tile_near_first_failed_probe" };
                        return result;
                    }
                result["blocker_unknown"] = "collision_failed_without_identified_support_tile";
                return result;
            }
            previous = point;
        }
        return new JsonObject { ["clear"] = true, ["collision_model"] = "four_pixel_line_probe" };
    }

    public static JsonObject Observe(Player self, Player target, bool detailed = false)
    {
        var probes = new JsonArray();
        foreach (int dx in new[] { -12, -6, 0, 6, 12 })
        foreach (int dy in new[] { -8, -4, 0, 4, 8 })
        {
            Vector2 start = self.Center + new Vector2(dx * 16, dy * 16);
            int x = (int)(start.X / 16), firstY = (int)((start.Y + self.height / 2f) / 16);
            for (int y = firstY; y <= firstY + 8; y++)
            {
                if (!Support(x, y)) continue;
                Vector2 center = new(start.X, y * 16 - self.height / 2f);
                Vector2 topLeft = center - new Vector2(self.width / 2f, self.height / 2f);
                if (!Collision.SolidCollision(topLeft, self.width, self.height))
                    probes.Add(new JsonObject { ["x"] = center.X, ["y"] = center.Y, ["support_x"] = x, ["support_y"] = y,
                        ["body_fits"] = true, ["shot_line"] = Trace(center, target.Center),
                        ["reachability"] = "unknown_not_a_computed_path" });
                break;
            }
        }
        var result = new JsonObject { ["captured_tick"] = Main.GameUpdateCount,
            ["direct_line"] = Trace(self.Center, target.Center), ["stance_probes"] = probes,
            ["warning"] = "A clear line is not a valid curved projectile trajectory or proof of reachability." };
        if (detailed)
            result["tile_windows"] = new JsonArray(Window(self.Center, 32, 20), Window(target.Center, 16, 12));
        return result;
    }

    static JsonObject Window(Vector2 center, int radiusX, int radiusY)
    {
        int originX = (int)(center.X / 16) - radiusX, originY = (int)(center.Y / 16) - radiusY;
        var rows = new JsonArray();
        for (int dy = 0; dy <= radiusY * 2; dy++)
        {
            var row = new JsonArray();
            string last = null;
            JsonArray run = null;
            for (int dx = 0; dx <= radiusX * 2; dx++)
            {
                int x = originX + dx, y = originY + dy;
                int flags = 16, type = 0, shape = 0, liquidType = 0, liquid = 0;
                if (InWorld(x, y))
                {
                    var tile = Main.tile[x, y]; type = tile.TileType;
                    flags = (tile.HasTile ? 1 : 0) | (Main.tileSolid[type] ? 2 : 0)
                        | (Main.tileSolidTop[type] ? 4 : 0) | (tile.IsActuated ? 8 : 0);
                    shape = tile.IsHalfBlock ? 1 : tile.Slope == 0 ? 0 : (int)tile.Slope + 1;
                    liquidType = tile.LiquidType; liquid = tile.LiquidAmount;
                }
                string signature = $"{flags},{type},{shape},{liquidType},{liquid}";
                if (signature == last) run[0] = run[0].GetValue<int>() + 1;
                else { run = new JsonArray(1, flags, type, shape, liquidType, liquid); row.Add(run); last = signature; }
            }
            rows.Add(row);
        }
        return new JsonObject { ["origin_x"] = originX, ["origin_y"] = originY,
            ["width"] = radiusX * 2 + 1, ["height"] = radiusY * 2 + 1, ["rows"] = rows,
            ["encoding"] = "runs:[length,flags,type,shape,liquid_type,liquid_amount]; flags:1=tile,2=solid,4=platform,8=actuated,16=unknown; shape:0=full,1=half,2..5=slope" };
    }
}
