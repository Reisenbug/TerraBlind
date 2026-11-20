using System.Linq;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public static class CombatLoadout
{
    public static int PreferredType;
    public static int AmmoFallbackType;
    public static bool Usable(Item item) => item != null && !item.IsAir && item.damage > 0 && item.ammo == 0
        && item.pick == 0 && item.axe == 0 && item.hammer == 0;
    public static bool CanUse(Player player, Item item) => Usable(item)
        && (item.useAmmo == 0 || player.HasAmmo(item)) && item.mana * player.manaCost <= player.statMana;

    static Item PreferredWeapon(Player player) => player.inventory.Take(50)
        .FirstOrDefault(item => Usable(item) && item.type == PreferredType);

    internal static JsonObject AmmoSupply(Player player)
    {
        Item weapon = PreferredType == 0 ? player.HeldItem : PreferredWeapon(player);
        if (weapon == null && PreferredType > 0) ContentSamples.ItemsByType.TryGetValue(PreferredType, out weapon);
        if (!Usable(weapon) || weapon.useAmmo <= 0 && !weapon.consumable) return null;
        var stocks = weapon.useAmmo > 0 ? player.inventory.Take(58)
            .Where(item => !item.IsAir && ItemLoader.CanChooseAmmo(weapon, item, player)).ToArray()
            : player.inventory.Take(50).Where(item => !item.IsAir && item.type == weapon.type).ToArray();
        if (stocks.Any(item => !item.consumable)) return null;
        if (weapon.useAmmo > 0 && stocks.Length == 0 && player.HasAmmo(weapon)) return null;
        return new JsonObject { ["weapon_type"] = weapon.type, ["weapon"] = weapon.Name, ["ammo_family"] = weapon.useAmmo,
            ["units"] = stocks.Sum(item => item.stack), ["fallback_weapon_type"] = AmmoFallbackType };
    }

    public static int Select(Player player, NPC target = null, bool allowPickaxeFallback = false)
        => Select(player, target, allowPickaxeFallback, allowAlternative: false);

    internal static int Select(Player player, NPC target, bool allowPickaxeFallback, bool allowAlternative)
    {
        int preferred = PreferredType;
        if (preferred > 0 && AmmoFallbackType > 0)
        {
            var weapon = PreferredWeapon(player);
            if (weapon == null && ContentSamples.ItemsByType.TryGetValue(preferred, out var original) && original.consumable
                || weapon?.useAmmo > 0 && !player.HasAmmo(weapon)) preferred = AmmoFallbackType;
        }
        int best = -1;
        bool hasWeapon = false;
        for (int slot = 0; slot < 50; slot++)
        {
            if (slot == 10 && best >= 0 && (!allowAlternative || preferred == 0)) break;
            var item = player.inventory[slot];
            if (!CanUse(player, item)) continue;
            hasWeapon = true;
            if (target != null && !FightCoordinator.CanHitTarget(player, item, target.Center, target.width / 2f)) continue;
            if (preferred > 0)
            {
                if (item.type == preferred) return slot;
                if (!allowAlternative) continue;
                if (slot >= 10 && best is >= 0 and < 10) continue;
            }
            if (best < 0) best = slot;
            else
            {
                var previous = player.inventory[best];
                if (previous.consumable && !item.consumable || previous.consumable == item.consumable
                    && player.GetWeaponDamage(item) > player.GetWeaponDamage(previous)) best = slot;
            }
        }
        if (best >= 0 || !allowPickaxeFallback || hasWeapon || preferred > 0 || target == null) return best;
        for (int slot = 0; slot < 50; slot++)
        {
            if (slot == 10 && best >= 0) break;
            var pickaxe = player.inventory[slot];
            if (pickaxe == null || pickaxe.IsAir || pickaxe.pick <= 0 || pickaxe.damage <= 0
                || !FightCoordinator.CanHitTarget(player, pickaxe, target.Center, target.width / 2f)) continue;
            if (best < 0 || player.GetWeaponDamage(pickaxe) > player.GetWeaponDamage(player.inventory[best])) best = slot;
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
