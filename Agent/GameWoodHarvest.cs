using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public sealed class GameWoodHarvest : IWoodHarvestWorld
{
    static int X(JsonObject tree) => tree["x"].GetValue<int>();
    static int Y(JsonObject tree) => tree["y"].GetValue<int>();
    static int AxeSlot() => Enumerable.Range(0, 50).OrderByDescending(i => Main.LocalPlayer.inventory[i].axe)
        .FirstOrDefault(i => !Main.LocalPlayer.inventory[i].IsAir && Main.LocalPlayer.inventory[i].axe > 0, -1);

    public Task<JsonObject> Observe(CancellationToken token) => NativeAgent.OnMain(() =>
    {
        var player = Main.LocalPlayer;
        var trees = new Dictionary<(int, int), JsonObject>();
        int cx = ActExecutor.OriginCx(player), cy = ActExecutor.OriginCy(player);
        for (int x = Math.Max(1, cx - 120); x < Math.Min(Main.maxTilesX - 1, cx + 121); x++)
            for (int y = Math.Max(1, cy - 48); y < Math.Min(Main.maxTilesY - 1, cy + 49); y++)
            {
                var tile = Main.tile[x, y];
                if (!tile.HasTile || tile.TileType != TileID.Trees) continue;
                int tx = x, ty = y;
                if (!ItemUseCoordinator.TrySnapTree(ref tx, ref ty)) continue;
                if (Main.tile[tx, ty].TileType != TileID.Trees) continue;
                if (trees.ContainsKey((tx, ty))) break;
                var furniture = new JsonArray();
                for (int fx = Math.Max(1, tx - 12); fx < Math.Min(Main.maxTilesX - 1, tx + 13); fx++)
                    for (int fy = Math.Max(1, ty - 12); fy < Math.Min(Main.maxTilesY - 1, ty + 5); fy++)
                    {
                        var ft = Main.tile[fx, fy];
                        if (ft.HasTile && ft.TileType is TileID.ClosedDoor or TileID.OpenDoor or TileID.WorkBenches or TileID.Chairs or TileID.Tables)
                            if (furniture.Count < 16) furniture.Add(new JsonObject { ["x"] = fx, ["y"] = fy, ["type"] = ft.TileType });
                    }
                trees[(tx, ty)] = new JsonObject { ["id"] = $"tree_{tx}_{ty}", ["x"] = tx, ["y"] = ty,
                    ["distance"] = Math.Abs(cx - tx) + Math.Abs(cy - ty), ["nearby_furniture"] = furniture };
                break;
            }
        var scene = new JsonObject
        {
            ["wood"] = player.inventory.Where(i => i != null && !i.IsAir && i.type == ItemID.Wood).Sum(i => i.stack),
            ["trees"] = new JsonArray(trees.Values.OrderBy(t => t["distance"].GetValue<int>()).Take(12).Select(t => (JsonNode)t).ToArray()),
            ["player_cell"] = new JsonArray(cx, cy), ["hp"] = player.statLife
        };
        if (player.dead || !player.active) scene["error"] = "player_dead";
        else if (AxeSlot() < 0) scene["error"] = "no_axe";
        return scene;
    }, token);

    public async Task<JsonObject> Approach(JsonObject tree, CancellationToken token)
    {
        bool ready = await NativeAgent.OnMain(() =>
        {
            bool reached = Reach.CanMine(Main.LocalPlayer, X(tree), Y(tree));
            DiagLog.Write($"[harvest] approach {tree["id"]} in_reach={reached} from=({ActExecutor.OriginCx(Main.LocalPlayer)},{ActExecutor.OriginCy(Main.LocalPlayer)})");
            return reached;
        }, token);
        if (!ready)
        {
            var result = await AgentTools.Navigate(new JsonObject { ["x"] = X(tree), ["y"] = Y(tree), ["reach"] = true }, token, 20);
            ready = await NativeAgent.OnMain(() => Reach.CanMine(Main.LocalPlayer, X(tree), Y(tree)), token);
            if (!ready) return new JsonObject { ["error"] = "cannot_reach_tree", ["navigation"] = result };
        }
        return new JsonObject { ["ok"] = true };
    }

    public async Task<JsonObject> Chop(JsonObject tree, CancellationToken token)
    {
        var start = await NativeAgent.OnMain(() =>
        {
            var tile = Main.tile[X(tree), Y(tree)];
            if (!tile.HasTile || tile.TileType != TileID.Trees) return new JsonObject { ["error"] = "target_changed" };
            int found = AxeSlot();
            DiagLog.Write($"[harvest] chop {tree["id"]} axe_slot={found}");
            if (found < 0) return new JsonObject { ["error"] = "no_axe" };
            ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = X(tree), TargetWy = Y(tree), Slot = found,
                Strict = true, DurationTicks = 0 });
            return new JsonObject { ["ok"] = true };
        }, token);
        if (start["error"] != null) return start;
        var result = await AgentTools.Wait("/item_use_status", "active", "/item_use_stop", 8, token);
        await NativeAgent.OnMain(() => { DiagLog.Write($"[harvest] chop_result {tree["id"]} {result}"); return true; }, token);
        return result;
    }

    public async Task<JsonObject> Pickup(JsonObject tree, CancellationToken token)
    {
        await Task.Delay(300, token);
        var result = await AgentTools.Navigate(new JsonObject { ["x"] = X(tree), ["y"] = Y(tree) }, token, 20);
        if (result["done"]?.ToString() != "true") result["error"] = "cannot_approach_drops";
        await Task.Delay(300, token);
        return result;
    }
}
