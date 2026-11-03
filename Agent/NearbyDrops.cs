using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class NearbyDrops
{
    public static JsonArray Read(int radius)
    {
        if (radius is < 1 or > 60) throw new ArgumentException("radius需要1..60格。");
        var player = Main.LocalPlayer;
        var drops = new JsonArray();
        for (int i = 0; i < Main.maxItems; i++)
        {
            var item = Main.item[i];
            if (!item.active || item.IsAir) continue;
            float distance = Vector2.Distance(item.Center, player.Center) / 16;
            if (distance > radius) continue;
            int x = (int)(item.Center.X / 16), y = (int)(item.Center.Y / 16);
            var drop = new JsonObject { ["who"] = i, ["item_type"] = item.type, ["name"] = item.Name,
                ["stack"] = item.stack, ["x"] = x, ["y"] = y, ["distance_tiles"] = distance,
                ["velocity_x"] = item.velocity.X, ["velocity_y"] = item.velocity.Y,
                ["inventory_count"] = player.inventory.Where(held => !held.IsAir && held.type == item.type).Sum(held => held.stack),
                ["route_safety_assessed"] = false };
            if (WorldGen.InWorld(x, y))
            {
                var tile = Main.tile[x, y];
                drop["liquid_amount"] = tile.LiquidAmount;
                drop["liquid_type"] = tile.LiquidType;
            }
            drops.Add(drop);
        }
        return drops;
    }
}
