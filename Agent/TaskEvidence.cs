using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public class AgentTaskEvidence : GlobalNPC
{
    static readonly Dictionary<int, int> Kills = new();
    static readonly HashSet<(int X, int Y)> LootedChests = new();
    internal static void Reset() { Kills.Clear(); LootedChests.Clear(); }

    internal sealed record ChestObservation(int X, int Y, Dictionary<int, int> Contents, Dictionary<int, int> Inventory);
    static Dictionary<int, int> Counts(IEnumerable<Item> items) => items.Where(i => i != null && !i.IsAir)
        .GroupBy(i => i.type).ToDictionary(g => g.Key, g => g.Sum(i => i.stack));

    internal static ChestObservation ObserveChest(int x, int y)
    {
        var chest = Main.chest.FirstOrDefault(c => c != null && x >= c.x && x < c.x + 2 && y >= c.y && y < c.y + 2);
        return chest == null ? null : new ChestObservation(chest.x, chest.y, Counts(chest.item), Counts(Main.LocalPlayer.inventory));
    }

    internal static bool VerifyChest(ChestObservation before)
    {
        if (before == null || Main.netMode != NetmodeID.SinglePlayer) return false;
        var after = ObserveChest(before.X, before.Y);
        if (after == null) return false;
        bool transferred = before.Contents.Any(item => item.Value > after.Contents.GetValueOrDefault(item.Key)
            && after.Inventory.GetValueOrDefault(item.Key) > before.Inventory.GetValueOrDefault(item.Key));
        return transferred && LootedChests.Add((before.X, before.Y));
    }

    public override void OnKill(NPC npc)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
        {
            Kills[npc.type] = Kills.GetValueOrDefault(npc.type) + 1;
            if (Config.I.AgentEnabled && AgentNetwork.CanAutomate && AgentTools.Watches.Subscribed("nearby_npc_killed")
                && Microsoft.Xna.Framework.Vector2.Distance(npc.Center, Main.LocalPlayer.Center) / 16 <= AgentTools.Watches.Radius)
                AgentTools.Watches.Emit("nearby_npc_killed", npc.type + ":" + Kills[npc.type], new JsonObject
                { ["npc_type"] = npc.type, ["x"] = npc.Center.X / 16, ["y"] = npc.Center.Y / 16,
                    ["killer_verified"] = false, ["drops_verified"] = false });
        }
    }

    internal static int Measure(JsonObject condition)
    {
        string kind = condition["kind"].GetValue<string>();
        int type = condition["type"]?.GetValue<int>() ?? 0;
        return kind switch
        {
            "permanent_health" => Main.LocalPlayer.statLifeMax,
            "position" => AtPosition(condition) ? 1 : 0,
            "biome" => AtBiome(condition) ? 1 : 0,
            "item" => Main.LocalPlayer.inventory.Where(item => item != null && !item.IsAir && item.type == type).Sum(item => item.stack),
            "equipped" => Enumerable.Range(0, 10).Where(slot => Main.LocalPlayer.IsItemSlotUnlockedAndUsable(slot)
                && Main.LocalPlayer.armor[slot].type == type).Sum(slot => Main.LocalPlayer.armor[slot].stack),
            "npc_alive" => Main.npc.Count(npc => npc != null && npc.active && npc.life > 0 && npc.type == type),
            "npc_kills" when Main.netMode == NetmodeID.SinglePlayer => Kills.GetValueOrDefault(type),
            "npc_kills" => throw new InvalidOperationException("当前本次击杀证据仅支持单人游戏；联机需要服务器确认，不能用历史Boss进度代替。"),
            "chests_looted" when Main.netMode == NetmodeID.SinglePlayer => LootedChests.Count,
            "chests_looted" => throw new InvalidOperationException("开箱转移证据目前仅支持单人游戏。"),
            _ => throw new InvalidOperationException("未知完成条件。")
        };
    }

    static bool AtBiome(JsonObject condition)
    {
        var p = Main.LocalPlayer;
        bool biome = condition["name"].ToString() switch
        {
            "jungle" => p.ZoneJungle,
            "desert" => p.ZoneDesert,
            "snow" => p.ZoneSnow,
            "ocean" => p.ZoneBeach,
            "dungeon" => p.ZoneDungeon,
            "corruption" => p.ZoneCorrupt,
            "crimson" => p.ZoneCrimson,
            "hallow" => p.ZoneHallow,
            _ => throw new InvalidOperationException("biome name需要jungle/desert/snow/ocean/dungeon/corruption/crimson/hallow。")
        };
        bool depth = condition["depth"]?.ToString() switch
        {
            null or "any" => true,
            "sky" => p.ZoneSkyHeight,
            "surface" => p.ZoneOverworldHeight,
            "below_surface" => p.ZoneDirtLayerHeight || p.ZoneRockLayerHeight,
            "underground" => p.ZoneDirtLayerHeight,
            "cavern" => p.ZoneRockLayerHeight,
            "underworld" => p.ZoneUnderworldHeight,
            _ => throw new InvalidOperationException("未知biome depth。")
        };
        return p.active && !p.dead && p.statLife > 0 && biome && depth;
    }

    static bool AtPosition(JsonObject condition)
    {
        var player = Main.LocalPlayer;
        int x = condition["x"].GetValue<int>(), y = condition["y"].GetValue<int>();
        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
            throw new InvalidOperationException("position目标超出当前世界。");
        float radius = condition["radius"].GetValue<int>() * 16f;
        float dx = player.Center.X - (x + 0.5f) * 16f, dy = player.Center.Y - (y + 0.5f) * 16f;
        return player.active && !player.dead && player.statLife > 0 && dx * dx + dy * dy <= radius * radius;
    }
}
