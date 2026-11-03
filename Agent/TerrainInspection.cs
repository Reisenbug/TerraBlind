using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class TerrainInspection
{
    public static JsonObject Opportunity(int x, int y, JsonObject destination)
    {
        var clock = Stopwatch.StartNew();
        if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1)
            throw new ArgumentException("目标须在世界边界内，坐标单位为格。");
        var player = Main.LocalPlayer;
        double sx = player.Center.X / 16, sy = player.Center.Y / 16;
        double tx = x + 0.5, ty = y + 0.5;
        double Distance(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        double approach = Distance(sx, sy, tx, ty);
        if (approach > 60) return new JsonObject
        {
            ["ok"] = false,
            ["error"] = "target_out_of_observation_range",
            ["tick"] = (long)Main.GameUpdateCount, ["elapsed_ms"] = clock.Elapsed.TotalMilliseconds,
            ["target"] = new JsonObject { ["x"] = x, ["y"] = y },
            ["approach_direct_distance"] = approach, ["observation_radius_tiles"] = 60,
            ["sampled_cells"] = 0, ["hazards"] = null, ["reachable"] = null,
            ["detail"] = "未检查目标或沿途方块，危险与可达性未知；由Agent决定接近后重查、选择其他目标或跳过。"
        };
        var solids = new JsonArray(); var hazards = new JsonArray();
        var sampled = new HashSet<(int, int)>();
        var approachCells = new HashSet<(int, int)>();
        double halfWidth = player.width / 32.0, halfHeight = player.height / 32.0;
        int steps = Math.Max(1, (int)Math.Ceiling(approach * 2));
        for (int step = 0; step <= steps; step++)
        {
            double cx = sx + (tx - sx) * step / steps, cy = sy + (ty - sy) * step / steps;
            for (int ix = (int)Math.Floor(cx - halfWidth); ix <= (int)Math.Floor(cx + halfWidth - 0.001); ix++)
                for (int iy = (int)Math.Floor(cy - halfHeight); iy <= (int)Math.Floor(cy + halfHeight - 0.001); iy++)
                    if (ix >= 1 && iy >= 1 && ix < Main.maxTilesX - 1 && iy < Main.maxTilesY - 1)
                        approachCells.Add((ix, iy));
        }
        sampled.UnionWith(approachCells);
        for (int ix = Math.Max(1, x - 4); ix <= Math.Min(Main.maxTilesX - 2, x + 4); ix++)
            for (int iy = Math.Max(1, y - 4); iy <= Math.Min(Main.maxTilesY - 2, y + 4); iy++) sampled.Add((ix, iy));
        foreach (var (ix, iy) in sampled)
        {
            var tile = Main.tile[ix, iy];
            bool active = tile.HasUnactuatedTile;
            JsonObject Fact(string kind) => new() { ["x"] = ix, ["y"] = iy, ["kind"] = kind,
                ["tile_type"] = active ? (int)tile.TileType : null, ["on_direct_corridor"] = approachCells.Contains((ix, iy)) };
            if (active && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType] && approachCells.Contains((ix, iy)))
                solids.Add(Fact("solid"));
            if (active && TileID.Sets.Falling[tile.TileType]) hazards.Add(Fact("falling_block"));
            if (active && TileID.Sets.TouchDamageImmediate[tile.TileType] > 0) hazards.Add(Fact("contact_damage"));
            if (tile.LiquidAmount > 0)
            {
                var liquid = Fact(tile.LiquidType == LiquidID.Lava ? "lava" : "liquid");
                liquid["liquid_type"] = tile.LiquidType; liquid["amount"] = (int)tile.LiquidAmount;
                hazards.Add(liquid);
            }
            if (active && tile.TileType is TileID.PressurePlates or TileID.Traps or TileID.Boulder or TileID.Explosives
                or TileID.RollingCactus or TileID.BouncyBoulder or TileID.LifeCrystalBoulder)
                hazards.Add(Fact("trap_tile"));
        }
        JsonObject relation = null;
        if (destination != null)
        {
            int gx = destination["x"]?.GetValue<int>() ?? throw new ArgumentException("destination需要x/y。");
            int gy = destination["y"]?.GetValue<int>() ?? throw new ArgumentException("destination需要x/y。");
            if (gx < 0 || gy < 0 || gx >= Main.maxTilesX || gy >= Main.maxTilesY)
                throw new ArgumentException("destination须在世界内。");
            double dx = gx + 0.5 - sx, dy = gy + 0.5 - sy, lengthSquared = dx * dx + dy * dy;
            double projection = lengthSquared == 0 ? 0 : ((tx - sx) * dx + (ty - sy) * dy) / lengthSquared;
            double nearest = Math.Clamp(projection, 0, 1);
            double remaining = Distance(tx, ty, gx + 0.5, gy + 0.5);
            relation = new JsonObject { ["destination"] = destination.DeepClone(),
                ["original_direct_distance"] = Math.Sqrt(lengthSquared), ["target_to_destination_direct_distance"] = remaining,
                ["extra_direct_distance"] = Math.Max(0, approach + remaining - Math.Sqrt(lengthSquared)),
                ["distance_to_goal_segment"] = Distance(tx, ty, sx + nearest * dx, sy + nearest * dy),
                ["projection_fraction"] = projection, ["behind_player"] = projection < 0,
                ["basis"] = "straight_line_geometry_not_navigation_route" };
        }
        var target = Main.tile[x, y];
        return new JsonObject { ["ok"] = true, ["tick"] = (long)Main.GameUpdateCount, ["elapsed_ms"] = clock.Elapsed.TotalMilliseconds,
            ["target"] = new JsonObject { ["x"] = x, ["y"] = y, ["has_tile"] = target.HasTile,
                ["tile_type"] = target.HasTile ? (int)target.TileType : null },
            ["approach_direct_distance"] = approach, ["direct_corridor_solid_cells"] = solids,
            ["hazards"] = hazards, ["sampled_cells"] = sampled.Count, ["route_relation"] = relation,
            ["reachable"] = null, ["travel_time_ms"] = null, ["required_mining_cells"] = null,
            ["limitations"] = "只检查当前方块、直线人物扫掠范围及目标周边4格。挡路格数不是必挖数量；未模拟跳跃、落差、绕路、敌人、射出的机关或返程，不证明安全可达。由Agent/JS决定接近、跳过或进一步调查。" };
    }

    internal static JsonObject NavigationHazard()
    {
        var player = Main.LocalPlayer;
        int px = (int)player.Center.X / 16, py = (int)player.Bottom.Y / 16;
        var hazards = new JsonArray();
        for (int x = Math.Max(1, px - 4); x <= Math.Min(Main.maxTilesX - 2, px + 4); x++)
        for (int y = Math.Max(1, py - 6); y <= Math.Min(Main.maxTilesY - 2, py + 2); y++)
        {
            var tile = Main.tile[x, y];
            if (!tile.HasUnactuatedTile) continue;
            bool trigger = tile.TileType == TileID.PressurePlates;
            bool explosive = tile.TileType == TileID.Explosives;
            bool rollingCactus = tile.TileType == TileID.RollingCactus;
            bool overheadBoulder = tile.TileType is TileID.Boulder or TileID.BouncyBoulder or TileID.LifeCrystalBoulder
                && y < py && Math.Abs(x - px) <= 2;
            if (trigger || explosive || overheadBoulder || rollingCactus)
                hazards.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                    ["kind"] = trigger ? "pressure_plate" : explosive ? "explosive" : rollingCactus ? "rolling_cactus" : "overhead_boulder" });
        }
        return hazards.Count == 0 ? null : new JsonObject { ["error"] = "navigation_near_trap", ["outcome"] = "unsafe",
            ["trap_tiles"] = hazards, ["detail"] = "已停止接近机关。先用inspect_terrain检查并拆除可达触发器；不要挖落头顶巨石或原样重试路线。" };
    }

    public static JsonObject Read(int radius)
    {
        if (radius is < 2 or > 12) throw new ArgumentException("radius须2..12格。");
        var player = Main.LocalPlayer;
        int cx = (int)player.Center.X / 16, cy = (int)player.Center.Y / 16;
        int left = Math.Max(1, cx - radius), top = Math.Max(1, cy - radius);
        int right = Math.Min(Main.maxTilesX - 2, cx + radius), bottom = Math.Min(Main.maxTilesY - 2, cy + radius);
        int bodyLeft = (int)Math.Floor(player.position.X / 16), bodyTop = (int)Math.Floor(player.position.Y / 16);
        int bodyRight = (int)Math.Floor((player.position.X + player.width - 1) / 16);
        int bodyBottom = (int)Math.Floor((player.position.Y + player.height - 1) / 16);
        var rows = new JsonArray(); var nearby = new JsonArray(); var hazards = new JsonArray(); var shapes = new JsonArray();
        for (int y = top; y <= bottom; y++)
        {
            var row = new StringBuilder();
            for (int x = left; x <= right; x++)
            {
                var tile = Main.tile[x, y];
                bool active = tile.HasUnactuatedTile;
                bool falling = active && TileID.Sets.Falling[tile.TileType];
                bool solid = active && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
                int touchDamage = active ? TileID.Sets.TouchDamageImmediate[tile.TileType] : 0;
                if (active && ((int)tile.Slope != 0 || tile.IsHalfBlock))
                    shapes.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["solid"] = solid, ["slope"] = (int)tile.Slope, ["half_block"] = tile.IsHalfBlock });
                if (tile.HasTile && tile.TileType is TileID.PressurePlates or TileID.Traps or TileID.Boulder or TileID.Explosives
                    or TileID.RollingCactus or TileID.BouncyBoulder or TileID.LifeCrystalBoulder)
                    hazards.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["active"] = active, ["frame_x"] = tile.TileFrameX, ["frame_y"] = tile.TileFrameY,
                        ["red_wire"] = tile.RedWire, ["blue_wire"] = tile.BlueWire,
                        ["green_wire"] = tile.GreenWire, ["yellow_wire"] = tile.YellowWire });
                row.Append(touchDamage > 0 ? 'H' : falling ? 'F' : solid ? '#' : active && Main.tileSolidTop[tile.TileType] ? '='
                    : tile.LiquidAmount > 0 ? tile.LiquidType == LiquidID.Lava ? 'L' : '~' : active ? '*' : '.');
                if (tile.HasTile && x >= bodyLeft - 1 && x <= bodyRight + 1 && y >= bodyTop - 1 && y <= bodyBottom + 1)
                    nearby.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["solid"] = solid, ["falling"] = falling, ["suffocates"] = TileID.Sets.Suffocate[tile.TileType], ["touch_damage"] = touchDamage,
                        ["slope"] = (int)tile.Slope, ["half_block"] = tile.IsHalfBlock,
                        ["in_body"] = x >= bodyLeft && x <= bodyRight && y >= bodyTop && y <= bodyBottom });
            }
            rows.Add(row.ToString());
        }
        return new JsonObject { ["tick"] = (long)Main.GameUpdateCount, ["hp"] = player.statLife,
            ["suffocating"] = player.suffocating, ["origin"] = new JsonObject { ["x"] = left, ["y"] = top },
            ["player_bounds"] = new JsonObject { ["left"] = bodyLeft, ["top"] = bodyTop, ["right"] = bodyRight, ["bottom"] = bodyBottom },
            ["rows"] = rows, ["near_body_tiles"] = nearby, ["trap_tiles"] = hazards, ["shaped_tiles"] = shapes,
            ["legend"] = "rows从上往下，每行从左往右，坐标=origin+下标；H原生接触伤害方块，F掉落方块，#实心，=平台/实心顶面，L岩浆，~其他液体，*非实心物件，.空气。#不保证完整平顶，整个观察范围的斜砖/半砖见shaped_tiles；slope是游戏原始枚举值，0无斜坡。人物范围见player_bounds；不是未来碰撞预测或家具放置许可。" };
    }
}
