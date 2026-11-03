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
    internal static void Reset() => Kills.Clear();

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
            "item" => Main.LocalPlayer.inventory.Where(item => item != null && !item.IsAir && item.type == type).Sum(item => item.stack),
            "equipped" => Enumerable.Range(0, 10).Where(slot => Main.LocalPlayer.IsItemSlotUnlockedAndUsable(slot)
                && Main.LocalPlayer.armor[slot].type == type).Sum(slot => Main.LocalPlayer.armor[slot].stack),
            "npc_alive" => Main.npc.Count(npc => npc != null && npc.active && npc.life > 0 && npc.type == type),
            "npc_kills" when Main.netMode == NetmodeID.SinglePlayer => Kills.GetValueOrDefault(type),
            "npc_kills" => throw new InvalidOperationException("当前本次击杀证据仅支持单人游戏；联机需要服务器确认，不能用历史Boss进度代替。"),
            _ => throw new InvalidOperationException("未知完成条件。")
        };
    }
}
