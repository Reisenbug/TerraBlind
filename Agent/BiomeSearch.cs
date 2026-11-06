using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class BiomeSearch
{
    public static JsonObject Jungle()
    {
        var clock = Stopwatch.StartNew();
        int origin = (int)(Main.LocalPlayer.Center.X / 16);
        var weights = Main.remixWorld ? TileID.Sets.RemixJungleBiome : TileID.Sets.JungleBiome;
        var entrance = SurfaceBiomeSearch.Find(Main.maxTilesX, Main.maxTilesY, (int)Main.worldSurface, origin,
            Main.buffScanAreaWidth, Main.buffScanAreaHeight, SceneMetrics.JungleTileThreshold, (x, y) =>
            {
                var tile = Main.tile[x, y];
                return new BiomeCell(tile.HasUnactuatedTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType],
                    tile.HasTile ? weights[tile.TileType] : 0, tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava);
            });
        var result = new JsonObject { ["found"] = entrance.HasValue, ["biome"] = "jungle", ["target_kind"] = "verified_surface_biome_reference",
            ["purpose"] = "群系方向和大致位置参考；实际路线结合特殊知识、当前位置和现场环境选择。",
            ["route_knowledge"] = "find_descent/descent_route提供既有算法计算的丛林高效下降主道，可用于进入地下丛林。",
            ["threshold"] = SceneMetrics.JungleTileThreshold, ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["validation"] = "first solid ground from world sky; clear body footprint; native jungle tile weights and scan area" };
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
