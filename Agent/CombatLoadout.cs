using Terraria;

namespace TerraBlind;

public static class CombatLoadout
{
    public static int PreferredType;
    public static bool Usable(Item item) => item != null && !item.IsAir && item.damage > 0 && item.ammo == 0
        && item.pick == 0 && item.axe == 0 && item.hammer == 0;
    public static bool CanUse(Player player, Item item) => Usable(item)
        && (item.useAmmo == 0 || player.HasAmmo(item)) && item.mana * player.manaCost <= player.statMana;

    public static int Select(Player player, NPC target = null)
    {
        int best = -1;
        for (int slot = 0; slot < 50; slot++)
        {
            if (slot == 10 && best >= 0) break;
            var item = player.inventory[slot];
            if (!CanUse(player, item)) continue;
            if (target != null && !FightCoordinator.CanHitTarget(player, item, target.Center, target.width / 2f)) continue;
            if (PreferredType > 0)
            {
                if (item.type == PreferredType) return slot;
                continue;
            }
            if (best < 0) best = slot;
            else
            {
                var previous = player.inventory[best];
                if (previous.consumable && !item.consumable || previous.consumable == item.consumable
                    && player.GetWeaponDamage(item) > player.GetWeaponDamage(previous)) best = slot;
            }
        }
        return best;
    }

    public static int ReadySlot(Player player, int slot)
    {
        if (slot < 10) return slot;
        if (player != Main.LocalPlayer || player.itemAnimation != 0 || player.itemTime != 0
            || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning) return -1;
        int destination = -1;
        for (int i = 9; i >= 0; i--)
        {
            var item = player.inventory[i];
            if (i == player.selectedItem || item.favorited) continue;
            if (item.IsAir) { destination = i; break; }
            if (destination < 0) destination = i;
        }
        if (destination < 0) return -1;
        PvpInventory.SwapSlots(slot, destination);
        return destination;
    }
}
