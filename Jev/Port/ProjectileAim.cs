using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind.JevariaCombat;

public sealed class ProjectileAim : IAttackDriver
{
    private bool _weaponSelected;
    private int _previousSlot = -1;
    private int _autoSlot = -1;
    internal string Status { get; private set; } = "idle";

    public int? SelectFirstWeapon(Player player)
    {
        if (_weaponSelected)
        {
            if (_autoSlot >= 0 && player.selectedItem != _autoSlot)
                _autoSlot = -1;
            return null;
        }

        for (int slot = 0; slot < 10; slot++)
        {
            Item item = player.inventory[slot];
            if (item.IsAir || item.damage <= 0 || item.useStyle == ItemUseStyleID.None ||
                item.pick != 0 || item.axe != 0 || item.hammer != 0) continue;
            _weaponSelected = true;
            _previousSlot = player.selectedItem;
            _autoSlot = slot;
            player.selectedItem = slot;
            return slot;
        }
        return null;
    }

    public void RestoreWeapon(Player player)
    {
        if (_autoSlot >= 0 && player.selectedItem == _autoSlot && _previousSlot >= 0)
            player.selectedItem = _previousSlot;
        _weaponSelected = false;
        _previousSlot = _autoSlot = -1;
    }

    public bool TryAttack(Player player, CombatSnapshot snapshot, out Vector2 target)
    {
        target = snapshot.Boss.Center;
        player.controlUseItem = false;
        Status = "no firing solution";
        Item weapon = player.HeldItem;
        int type = snapshot.ShotProjectileType;
        if (type == ProjectileID.None || snapshot.ShotSpeed <= 0f) return false;
        ContentSamples.ProjectilesByType.TryGetValue(type, out var shot);
        if (shot?.aiStyle == ProjAIStyleID.Flail) return TryFlail(player, snapshot.Boss, shot, out target);
        Vector2 origin = player.MountedCenter;
        if (weapon.type == ItemID.DartPistol)
        {
            int facing = snapshot.Boss.Center.X >= origin.X ? 1 : -1;
            origin += new Vector2(-4f * facing, -2f * player.gravDir);
        }
        if (type is ProjectileID.ThrowingKnife or ProjectileID.Shuriken)
        {
            if (!TryThrown(origin, snapshot.Boss, shot, snapshot.ShotSpeed, out target)) return false;
        }
        else
        {
            float lifetime = shot is { timeLeft: > 0 } ? shot.timeLeft / (float)(shot.extraUpdates + 1) : float.PositiveInfinity;
            if (!Intercept.TrySolve(origin, snapshot.Boss.Center, snapshot.Boss.Velocity, snapshot.ShotSpeed, out target, lifetime)
                || !FightCoordinator.CanHitTargetFrom(player, weapon, origin, target)) return false;
        }
        Cursor.AimPx(target.X, target.Y);
        player.controlUseItem = true;
        Status = "firing";
        return true;
    }

    static bool TryThrown(Vector2 origin, CombatEntity boss, Projectile shot, float speed, out Vector2 aim)
    {
        aim = boss.Center;
        if (shot == null) return false;
        var reach = (boss.Size + new Vector2(shot.width, shot.height)) * .5f;
        float drag = 1, horizontal = 0, drop = 0, fall = 0;
        for (int frame = 1; frame <= 60; frame++)
        {
            if (frame >= 20) { drag *= .97f; fall += .4f; }
            horizontal += drag;
            drop += fall;
            Vector2 offset = boss.Center + boss.Velocity * frame - origin - new Vector2(0, drop);
            var launch = new Vector2(offset.X / horizontal, offset.Y / frame).SafeNormalize(Vector2.UnitX) * speed;
            if (fall + launch.Y > 16 || Math.Abs(launch.X * horizontal - offset.X) > reach.X
                || Math.Abs(launch.Y * frame - offset.Y) > reach.Y) continue;
            Vector2 point = origin - new Vector2(shot.width, shot.height) * .5f, velocity = launch;
            for (int tick = 1; tick <= frame; tick++)
            {
                if (tick >= 20) { velocity.X *= .97f; velocity.Y += .4f; }
                var next = point + velocity;
                if (!Collision.CanHitLine(point, shot.width, shot.height, next, shot.width, shot.height)) break;
                if (tick == frame) { aim = origin + launch * 40f; return true; }
                point = next;
            }
        }
        return false;
    }

    bool TryFlail(Player player, CombatEntity boss, Projectile shot, out Vector2 aim)
    {
        aim = boss.Center;
        var (frames, speed) = shot.type switch
        {
            ProjectileID.Mace or ProjectileID.FlamingMace => (13, 12f),
            ProjectileID.BallOHurt => (15, 14f), ProjectileID.TheMeatball => (15, 15f),
            ProjectileID.BlueMoon => (15, 16f), ProjectileID.Sunfury => (15, 17f),
            ProjectileID.TheDaoofPow => (13, 21f), ProjectileID.DripplerFlail => (13, 22f),
            ProjectileID.FlowerPow => (13, 23f), _ => (10, 24f)
        };
        Vector2 origin = player.MountedCenter;
        bool clear = Intercept.TrySolve(origin, boss.Center, boss.Velocity - player.velocity,
            speed * player.GetTotalAttackSpeed(DamageClass.Melee), out aim, frames)
            && Collision.CanHitLine(origin, 4, 4, boss.Center, 4, 4);
        Projectile active = null;
        if (player.ownedProjectileCounts[shot.type] > 0 || shot.type == ProjectileID.FlamingMace && player.ownedProjectileCounts[ProjectileID.Mace] > 0)
            foreach (var candidate in Main.projectile)
                if (candidate.active && candidate.owner == player.whoAmI && (candidate.type == shot.type
                    || shot.type == ProjectileID.FlamingMace && candidate.type == ProjectileID.Mace)) { active = candidate; break; }
        if (active == null && !clear || active != null && active.ai[0] != 0f)
        {
            Status = active == null ? "flail out of reach" : "flail in flight or retracting";
            return false;
        }
        Cursor.AimPx(aim.X, aim.Y);
        player.controlUseItem = active == null || !clear;
        Status = player.controlUseItem ? "spinning flail" : "releasing flail";
        return true;
    }

    public void Release(Player player) { }
}

