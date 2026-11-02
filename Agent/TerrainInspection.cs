using System;
using System.Text;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class TerrainInspection
{
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
        var rows = new JsonArray(); var nearby = new JsonArray(); var hazards = new JsonArray();
        for (int y = top; y <= bottom; y++)
        {
            var row = new StringBuilder();
            for (int x = left; x <= right; x++)
            {
                var tile = Main.tile[x, y];
                bool active = tile.HasUnactuatedTile;
                bool falling = active && TileID.Sets.Falling[tile.TileType];
                bool solid = active && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
                if (tile.HasTile && tile.TileType is TileID.PressurePlates or TileID.Traps or TileID.Boulder or TileID.Explosives
                    or TileID.RollingCactus or TileID.BouncyBoulder or TileID.LifeCrystalBoulder)
                    hazards.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["active"] = active, ["frame_x"] = tile.TileFrameX, ["frame_y"] = tile.TileFrameY,
                        ["red_wire"] = tile.RedWire, ["blue_wire"] = tile.BlueWire,
                        ["green_wire"] = tile.GreenWire, ["yellow_wire"] = tile.YellowWire });
                row.Append(falling ? 'F' : solid ? '#' : active && Main.tileSolidTop[tile.TileType] ? '='
                    : tile.LiquidAmount > 0 ? tile.LiquidType == LiquidID.Lava ? 'L' : '~' : active ? '*' : '.');
                if (tile.HasTile && x >= bodyLeft - 1 && x <= bodyRight + 1 && y >= bodyTop - 1 && y <= bodyBottom + 1)
                    nearby.Add(new JsonObject { ["x"] = x, ["y"] = y, ["type"] = tile.TileType,
                        ["solid"] = solid, ["falling"] = falling, ["suffocates"] = TileID.Sets.Suffocate[tile.TileType],
                        ["slope"] = (int)tile.Slope, ["half_block"] = tile.IsHalfBlock,
                        ["in_body"] = x >= bodyLeft && x <= bodyRight && y >= bodyTop && y <= bodyBottom });
            }
            rows.Add(row.ToString());
        }
        return new JsonObject { ["tick"] = (long)Main.GameUpdateCount, ["hp"] = player.statLife,
            ["suffocating"] = player.suffocating, ["origin"] = new JsonObject { ["x"] = left, ["y"] = top },
            ["player_bounds"] = new JsonObject { ["left"] = bodyLeft, ["top"] = bodyTop, ["right"] = bodyRight, ["bottom"] = bodyBottom },
            ["rows"] = rows, ["near_body_tiles"] = nearby, ["trap_tiles"] = hazards,
            ["legend"] = "rows从上往下，每行从左往右，坐标=origin+下标；F掉落方块，#实心，=平台/实心顶面，L岩浆，~其他液体，*非实心物件，.空气。人物范围见player_bounds；不是未来碰撞预测。" };
    }
}
