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
        DiagLog.Write($"[agent_damage] kind={kind} damage={damage} hp={Player.statLife} x={Player.Center.X / 16:F2} y={Player.Center.Y / 16:F2} projectile_type={source.SourceProjectileType} projectile_index={source.SourceProjectileLocalIndex} npc_index={source.SourceNPCIndex} other={source.SourceOtherIndex}");
        DiagLog.Write("[agent_damage_terrain] " + TerrainInspection.Read(12).ToJsonString());
    }
}
