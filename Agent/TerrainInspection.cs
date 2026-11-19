using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class TerrainInspection
{
    internal static JsonObject MovementExposure(JsonObject scene, JsonObject movement)
    {
        var player = scene["player"];
        var end = movement["end"];
        int frames = movement["observed_comparison_frames"].GetValue<int>();
        int width = player["size_pixels"]["width"].GetValue<int>(), height = player["size_pixels"]["height"].GetValue<int>();
        var start = new Vector2(player["pos"]["x"].GetValue<float>(), player["pos"]["y"].GetValue<float>());
        var finish = end == null ? start : new Vector2(end["x"].GetValue<float>(), end["y"].GetValue<float>());
        var center = new Vector2(width / 2f, height / 2f);
        var targets = new JsonArray();
        foreach (var enemy in scene["targets"].AsArray().Where(n => n["contact_damage"].GetValue<int>() > 0))
        {
            var position = new Vector2(enemy["x"].GetValue<float>() * 16, enemy["y"].GetValue<float>() * 16);
            var predicted = position + new Vector2(enemy["vx_pixels_per_tick"].GetValue<float>(), enemy["vy_pixels_per_tick"].GetValue<float>()) * frames;
            int ew = Math.Max(1, (int)(enemy["width_tiles"].GetValue<float>() * 16)),
                eh = Math.Max(1, (int)(enemy["height_tiles"].GetValue<float>() * 16));
            var halfEnemy = new Vector2(ew / 2f, eh / 2f);
            targets.Add(new JsonObject { ["who"] = enemy["who"].DeepClone(),
                ["start_distance_tiles"] = Vector2.Distance(start + center, position) / 16,
                ["end_distance_tiles"] = Vector2.Distance(finish + center, predicted) / 16,
                ["start_terrain_line_clear"] = Collision.CanHitLine(position - halfEnemy, ew, eh, start, width, height),
                ["end_terrain_line_clear"] = Collision.CanHitLine(predicted - halfEnemy, ew, eh, finish, width, height) });
        }
        return new JsonObject { ["frames"] = frames, ["targets"] = targets,
            ["assumption"] = "Current world terrain; enemy motion uses constant velocity. Distances use actor centers. A blocked terrain line is geometric occlusion; attack penetration and enemy navigation remain unknown." };
    }

    internal static int ContactDamage(Player player, int x, int y)
    {
        var tile = Main.tile[x, y];
        int damage = tile.HasUnactuatedTile ? TileID.Sets.TouchDamageImmediate[tile.TileType] : 0;
        return damage > 0 && Collision.CanTileHurt(tile.TileType, x, y, player) ? damage : 0;
    }

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
            ["sampled_cells"] = 0, ["hazards"] = null, ["liquids"] = null, ["reachable"] = null,
            ["detail"] = "未检查目标或沿途方块，危险与可达性未知；由Agent决定接近后重查、选择其他目标或跳过。"
        };
        var solids = new JsonArray(); var hazards = new JsonArray(); var liquids = new JsonArray();
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
            if (active && TileID.Sets.CrackedBricks[tile.TileType]) hazards.Add(Fact("breaks_on_contact"));
            if (ContactDamage(player, ix, iy) > 0) hazards.Add(Fact("contact_damage"));
            if (tile.LiquidAmount > 0)
            {
                var liquid = Fact(tile.LiquidType switch
                {
                    LiquidID.Water => "water", LiquidID.Lava => "lava",
                    LiquidID.Honey => "honey", LiquidID.Shimmer => "shimmer", _ => "unknown_liquid"
                });
                liquid["liquid_type"] = tile.LiquidType; liquid["amount"] = (int)tile.LiquidAmount;
                liquids.Add(liquid);
                if (tile.LiquidType == LiquidID.Lava) hazards.Add(liquid.DeepClone());
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
            ["hazards"] = hazards, ["liquids"] = liquids, ["sampled_cells"] = sampled.Count, ["route_relation"] = relation,
            ["reachable"] = null, ["travel_time_ms"] = null, ["required_mining_cells"] = null,
            ["limitations"] = "只检查当前方块、直线人物扫掠范围及目标周边4格。liquids报告种类及0..255水量，不等于危险判定；未判断头部浸没、呼吸余量、停留时长或液体对当前装备的影响。附近有水不等于必须涉水或溺水。挡路格数不是必挖数量；未模拟跳跃、落差、绕路、敌人、射出的机关或返程，不证明安全可达。由Agent/JS决定接近、跳过或进一步调查。" };
    }

    internal static JsonObject BlockRegion(int x, int y, int width, int height)
    {
        if (width is < 1 or > 8 || height is < 1 or > 8 || !WorldGen.InWorld(x, y)
            || !WorldGen.InWorld(x + width - 1, y + height - 1))
            return new JsonObject { ["error"] = "invalid_block_region" };
        var player = Main.LocalPlayer;
        if (Math.Abs((int)(player.Center.X / 16) - x) > 32 || Math.Abs((int)(player.Center.Y / 16) - y) > 32)
            return new JsonObject { ["error"] = "block_region_out_of_range" };
        var cells = new JsonArray();
        for (int dy = 0; dy < height; dy++)
        for (int dx = 0; dx < width; dx++)
        {
            var tile = Main.tile[x + dx, y + dy];
            if (!tile.HasTile) continue;
            int type = tile.TileType;
            bool supported = tile.HasUnactuatedTile && Main.tileSolid[type] && !Main.tileSolidTop[type]
                && !Main.tileFrameImportant[type] && (int)tile.Slope == 0;
            cells.Add(new JsonObject { ["dx"] = dx, ["dy"] = dy, ["type"] = type,
                ["shape"] = !supported ? "unsupported" : tile.IsHalfBlock ? "half" : "full" });
        }
        return new JsonObject { ["ok"] = true, ["width"] = width, ["height"] = height, ["cells"] = cells };
    }

    internal static JsonObject NavigationHazard(JsonArray acceptedTraps = null, bool pressureOnly = false, bool deferPressure = false)
    {
        var player = Main.LocalPlayer;
        int px = (int)player.Center.X / 16, py = (int)player.Bottom.Y / 16;
        var hazards = new JsonArray();
        int dx = pressureOnly ? (int)Math.Clamp(player.velocity.X * 6 / 16, -64, 64) : 0;
        int dy = pressureOnly ? (int)Math.Clamp(player.velocity.Y * 6 / 16, -64, 64) : 0;
        int side = pressureOnly ? 2 : 4;
        for (int x = Math.Max(1, px - side + Math.Min(dx, 0)); x <= Math.Min(Main.maxTilesX - 2, px + side + Math.Max(dx, 0)); x++)
        for (int y = Math.Max(1, py - 6 + Math.Min(dy, 0)); y <= Math.Min(Main.maxTilesY - 2, py + 2 + Math.Max(dy, 0)); y++)
        {
            var tile = Main.tile[x, y];
            if (!tile.HasUnactuatedTile) continue;
            bool trigger = tile.TileType == TileID.PressurePlates;
            if (pressureOnly && !trigger || deferPressure && trigger) continue;
            bool explosive = tile.TileType == TileID.Explosives;
            bool rollingCactus = tile.TileType == TileID.RollingCactus;
            bool overheadBoulder = tile.TileType is TileID.Boulder or TileID.BouncyBoulder or TileID.LifeCrystalBoulder
                && y < py && Math.Abs(x - px) <= 2;
            if (trigger || explosive || overheadBoulder || rollingCactus)
            {
                if (acceptedTraps?.Any(t => t?["x"]?.GetValue<int>() == x && t?["y"]?.GetValue<int>() == y
                    && t?["type"]?.GetValue<int>() == tile.TileType
                    && t?["frame_x"]?.GetValue<int>() == tile.TileFrameX
                    && t?["frame_y"]?.GetValue<int>() == tile.TileFrameY) == true) continue;
                hazards.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                    ["frame_x"] = (int)tile.TileFrameX, ["frame_y"] = (int)tile.TileFrameY,
                    ["kind"] = trigger ? "pressure_plate" : explosive ? "explosive" : rollingCactus ? "rolling_cactus" : "overhead_boulder" });
            }
        }
        return hazards.Count == 0 ? null : new JsonObject { ["error"] = "navigation_near_trap", ["outcome"] = "unsafe",
            ["trap_tiles"] = hazards, ["detail"] = "附近存在机关，导航已停止；这不证明所有离开方向都危险。用inspect_terrain检查，选择绕行、拆除或换目标。若决定接受这些已观察机关的风险，可将其x/y/type/frame_x/frame_y放入本次nav_to的accepted_traps；只影响此次导航的附近机关检查，不关闭其他安全检查。不要原样重试或挖落头顶巨石。" };
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
        var unstable = new JsonArray(); var contacts = new JsonArray();
        for (int y = top; y <= bottom; y++)
        {
            var row = new StringBuilder();
            for (int x = left; x <= right; x++)
            {
                var tile = Main.tile[x, y];
                bool active = tile.HasUnactuatedTile;
                bool falling = active && TileID.Sets.Falling[tile.TileType];
                bool solid = active && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
                int touchDamage = ContactDamage(player, x, y);
                bool fragile = active && TileID.Sets.CrackedBricks[tile.TileType];
                if (fragile) unstable.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                    ["kind"] = "breaks_on_contact" });
                if (touchDamage > 0) contacts.Add(new JsonObject { ["x"] = x, ["y"] = y,
                    ["type"] = tile.TileType, ["damage"] = touchDamage });
                if (active && ((int)tile.Slope != 0 || tile.IsHalfBlock))
                    shapes.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["solid"] = solid, ["slope"] = (int)tile.Slope, ["half_block"] = tile.IsHalfBlock });
                if (tile.HasTile && tile.TileType is TileID.PressurePlates or TileID.Traps or TileID.Boulder or TileID.Explosives
                    or TileID.RollingCactus or TileID.BouncyBoulder or TileID.LifeCrystalBoulder)
                    hazards.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["active"] = active, ["frame_x"] = tile.TileFrameX, ["frame_y"] = tile.TileFrameY,
                        ["red_wire"] = tile.RedWire, ["blue_wire"] = tile.BlueWire,
                        ["green_wire"] = tile.GreenWire, ["yellow_wire"] = tile.YellowWire });
                row.Append(touchDamage > 0 ? 'H' : fragile ? 'C' : falling ? 'F' : solid ? '#' : active && Main.tileSolidTop[tile.TileType] ? '='
                    : tile.LiquidAmount > 0 ? tile.LiquidType == LiquidID.Lava ? 'L' : '~' : active ? '*' : '.');
                if (tile.HasTile && x >= bodyLeft - 1 && x <= bodyRight + 1 && y >= bodyTop - 1 && y <= bodyBottom + 1)
                    nearby.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["solid"] = solid, ["falling"] = falling, ["breaks_on_contact"] = fragile,
                        ["suffocates"] = TileID.Sets.Suffocate[tile.TileType], ["touch_damage"] = touchDamage,
                        ["slope"] = (int)tile.Slope, ["half_block"] = tile.IsHalfBlock,
                        ["in_body"] = x >= bodyLeft && x <= bodyRight && y >= bodyTop && y <= bodyBottom });
            }
            rows.Add(row.ToString());
        }
        return new JsonObject { ["tick"] = (long)Main.GameUpdateCount, ["hp"] = player.statLife,
            ["suffocating"] = player.suffocating, ["origin"] = new JsonObject { ["x"] = left, ["y"] = top },
            ["player_bounds"] = new JsonObject { ["left"] = bodyLeft, ["top"] = bodyTop, ["right"] = bodyRight, ["bottom"] = bodyBottom },
            ["rows"] = rows, ["near_body_tiles"] = nearby, ["trap_tiles"] = hazards, ["shaped_tiles"] = shapes,
            ["unstable_tiles"] = unstable, ["contact_damage_tiles"] = contacts,
            ["legend"] = "rows从上往下，每行从左往右，坐标=origin+下标；H原生接触伤害方块，C接触后碎裂方块，F掉落方块，#实心，=平台/实心顶面，L岩浆，~其他液体，*非实心物件，.空气。#不保证完整平顶，整个观察范围的斜砖/半砖见shaped_tiles；slope是游戏原始枚举值，0无斜坡。人物范围见player_bounds；不是未来碰撞预测或家具放置许可。" };
    }
}
