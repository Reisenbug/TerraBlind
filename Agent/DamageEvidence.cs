using System.Text.Json.Nodes;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerraBlind;

public class AgentDamageEvidence : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info) => Record("hurt", info.Damage, info.DamageSource);

    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
        => Record("death", damage, damageSource);

    void Record(string kind, double damage, PlayerDeathReason source)
    {
        if (Player.whoAmI != Main.myPlayer) return;
        AgentTools.LastDamage = new JsonObject { ["kind"] = kind, ["tick"] = (long)Main.GameUpdateCount,
            ["damage"] = damage, ["hp_at_hook"] = Player.statLife,
            ["pos_tiles"] = new JsonObject { ["x"] = Player.Center.X / 16, ["y"] = Player.Center.Y / 16 },
            ["projectile_type"] = source.SourceProjectileType,
            ["npc_type"] = source.SourceNPCIndex >= 0 && source.SourceNPCIndex < Main.maxNPCs ? Main.npc[source.SourceNPCIndex].type : -1,
            ["other_source"] = source.SourceOtherIndex };
        if (kind == "death")
        {
            AgentTools.LastDamage["death_text"] = source.GetDeathText(Player.name).ToString();
            AgentTools.LastDeath = (JsonObject)AgentTools.LastDamage.DeepClone();
        }
        DiagLog.Write($"[agent_damage] kind={kind} damage={damage} hp={Player.statLife} x={Player.Center.X / 16:F2} y={Player.Center.Y / 16:F2} projectile_type={source.SourceProjectileType} projectile_index={source.SourceProjectileLocalIndex} npc_index={source.SourceNPCIndex} other={source.SourceOtherIndex}");
        DiagLog.Write("[agent_damage_terrain] " + TerrainInspection.Read(12).ToJsonString());
    }
}
