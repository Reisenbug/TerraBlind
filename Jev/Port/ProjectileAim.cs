using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind.JevariaCombat;

public sealed class ProjectileAim : IAttackDriver
{
    private bool _weaponSelected;
    private int _previousSlot = -1;
    private int _autoSlot = -1;

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
        if (player.HeldItem.shoot == ProjectileID.None || snapshot.ShotSpeed <= 0f) return false;
        Vector2 origin = player.MountedCenter;
        if (player.HeldItem.type == ItemID.DartPistol)
        {
            int facing = snapshot.Boss.Center.X >= origin.X ? 1 : -1;
            origin += new Vector2(-4f * facing, -2f * player.gravDir);
        }
        if (!Intercept.TrySolve(origin, snapshot.Boss.Center,
                snapshot.Boss.Velocity, snapshot.ShotSpeed, out target)) return false;
        Main.mouseX = (int)(target.X - Main.screenPosition.X);
        Main.mouseY = (int)(target.Y - Main.screenPosition.Y);
        player.controlUseItem = true;
        return true;
    }

    public void Release(Player player) { }
}

