using Terraria;

namespace TerraBlind;

public static class CombatLoadout
{
    public static int PreferredType;
    public static bool Usable(Item item) => item != null && !item.IsAir && item.damage > 0 && item.ammo == 0
        && item.pick == 0 && item.axe == 0 && item.hammer == 0;

    public static int Select(Player player)
    {
        int best = -1;
        for (int slot = 0; slot < 10; slot++)
        {
            var item = player.inventory[slot];
            if (!Usable(item)) continue;
            if (PreferredType > 0)
            {
                if (item.type == PreferredType) return slot;
                continue;
            }
            if (best < 0 || player.GetWeaponDamage(item) > player.GetWeaponDamage(player.inventory[best])) best = slot;
        }
        return best;
    }
}
