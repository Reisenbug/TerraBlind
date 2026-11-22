using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace TerraBlind;

public sealed class PvpShotEvidence : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source) => PvpDuel.TraceShot("spawn", projectile);

    public override bool OnTileCollide(Projectile projectile, Vector2 oldVelocity)
    {
        PvpDuel.TraceShot("tile_contact", projectile);
        return true;
    }

    public override void OnKill(Projectile projectile, int timeLeft) => PvpDuel.TraceShot("end", projectile);
}

public sealed class PvpHurtEvidence : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info)
    {
        if (!info.PvP || !PvpDuel.Active) return;
        var source = info.DamageSource;
        int attacker = source.SourcePlayerIndex;
        if ((Player.whoAmI != Main.myPlayer || attacker != PvpDuel.Target)
            && (Player.whoAmI != PvpDuel.Target || attacker != Main.myPlayer)) return;
        int slot = source.SourceProjectileLocalIndex;
        Projectile shot = slot >= 0 && slot < Main.projectile.Length ? Main.projectile[slot] : null;
        int identity = shot != null && shot.owner == attacker && shot.type == source.SourceProjectileType ? shot.identity : -1;
        string witness = Player.whoAmI == Main.myPlayer ? "receiver" : "shooter";
        DiagLog.Write($"[pvp-hit] session={PvpDuel.Session} attacker={attacker} victim={Player.whoAmI} " +
            $"projectile={source.SourceProjectileType}:{identity} damage={info.Damage} witness={witness}");
    }
}
