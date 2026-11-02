using System;
using System.Text;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class TerrainInspection
{
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
        var rows = new JsonArray(); var nearby = new JsonArray();
        for (int y = top; y <= bottom; y++)
        {
            var row = new StringBuilder();
            for (int x = left; x <= right; x++)
            {
                var tile = Main.tile[x, y];
                bool active = tile.HasUnactuatedTile;
                bool falling = active && TileID.Sets.Falling[tile.TileType];
                bool solid = active && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
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
            ["rows"] = rows, ["near_body_tiles"] = nearby,
            ["legend"] = "rows从上往下，每行从左往右，坐标=origin+下标；F掉落方块，#实心，=平台/实心顶面，L岩浆，~其他液体，*非实心物件，.空气。人物范围见player_bounds；不是未来碰撞预测。" };
    }
}
