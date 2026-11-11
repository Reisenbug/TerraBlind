using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class BiomeSearch
{
    public static JsonObject Jungle() => Jungle((int)(Main.LocalPlayer.Center.X / 16));

    public static JsonObject Jungle(int origin)
    {
        var clock = Stopwatch.StartNew();
        var weights = Main.remixWorld ? TileID.Sets.RemixJungleBiome : TileID.Sets.JungleBiome;
        int width = Main.maxTilesX, limit = Math.Min(Main.maxTilesY, (int)Main.worldSurface + Main.buffScanAreaHeight / 2 + 1);
        var evil = new int[limit + 1, width + 1];
        for (int y = 0; y < limit; y++)
        for (int x = 0; x < width; x++)
        {
            var tile = Main.tile[x, y];
            int count = tile.HasTile && (TileID.Sets.Corrupt[tile.TileType] || TileID.Sets.Crimson[tile.TileType]) ? 1 : 0;
            evil[y + 1, x + 1] = count + evil[y, x + 1] + evil[y + 1, x] - evil[y, x];
        }
        bool Mixed(int x, int y)
        {
            int left = Math.Max(0, x - Main.buffScanAreaWidth / 2), right = Math.Min(width, x - Main.buffScanAreaWidth / 2 + Main.buffScanAreaWidth);
            int top = Math.Max(0, y - Main.buffScanAreaHeight / 2), bottom = Math.Min(limit, y - Main.buffScanAreaHeight / 2 + Main.buffScanAreaHeight);
            return evil[bottom, right] - evil[top, right] - evil[bottom, left] + evil[top, left] > 0;
        }
        var entrance = SurfaceBiomeSearch.Find(Main.maxTilesX, Main.maxTilesY, (int)Main.worldSurface, origin,
            Main.buffScanAreaWidth, Main.buffScanAreaHeight, SceneMetrics.JungleTileThreshold, (x, y) =>
            {
                var tile = Main.tile[x, y];
                return new BiomeCell(tile.HasUnactuatedTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType],
                    tile.HasTile ? weights[tile.TileType] : 0, tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava || Mixed(x, y));
            });
        var result = new JsonObject { ["found"] = entrance.HasValue, ["biome"] = "jungle", ["target_kind"] = "verified_surface_biome_reference",
            ["purpose"] = "群系方向和大致位置参考；实际路线结合特殊知识、当前位置和现场环境选择。",
            ["route_knowledge"] = "find_descent/descent_route提供既有算法计算的丛林高效下降主道，可用于进入地下丛林。",
            ["threshold"] = SceneMetrics.JungleTileThreshold, ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["validation"] = "sky-exposed jungle ground; clear body footprint; native jungle tile weights and scan area; surrounding biome scan contains no corrupt or crimson tiles" };
        if (entrance is { } point)
        {
            result["x"] = point.X; result["y"] = point.Y; result["local_biome_tiles"] = point.LocalCount;
            result["count"] = point.TotalCount; result["scanned_tiles"] = point.Scanned;
        }
        else result["reason"] = "no_sky_exposed_surface_with_sufficient_jungle_tiles";
        DiagLog.Write("[agent_biome] " + result.ToJsonString());
        return result;
    }
}
