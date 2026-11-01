using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class NavigationSurvival
{
    static int heldSlot = -1, previousSlot = -1, stashedSlot = -1;
    public static int Interventions { get; private set; }

    public static void Reset(Player player)
    {
        if (stashedSlot >= 0 && player.inventory[heldSlot].type is ItemID.Umbrella or ItemID.TragicUmbrella)
            (player.inventory[heldSlot], player.inventory[stashedSlot]) = (player.inventory[stashedSlot], player.inventory[heldSlot]);
        if (heldSlot >= 0 && player.selectedItem == heldSlot && previousSlot >= 0)
            player.selectedItem = previousSlot;
        heldSlot = previousSlot = stashedSlot = -1;
    }

    public static bool Apply(Player player)
    {
        if (!RecedingNav.Active || player.dead)
        {
            Reset(player);
            return false;
        }
        if (heldSlot >= 0)
        {
            if (player.velocity.Y == 0 && VanillaTerrainPlan.HasSupport(player))
            {
                Reset(player);
                return false;
            }
            if (player.inventory[heldSlot].type is ItemID.Umbrella or ItemID.TragicUmbrella)
            {
                ClearControls(player);
                player.selectedItem = heldSlot;
                player.controlDown = player.gravDir > 0;
                player.controlUp = player.gravDir < 0;
                return true;
            }
            Reset(player);
        }
        if (!DangerousLandingSoon(player)) return false;
        for (int slot = 0; slot < 50; slot++)
        {
            if (player.inventory[slot].type is not (ItemID.Umbrella or ItemID.TragicUmbrella)) continue;
            previousSlot = player.selectedItem;
            if (slot >= 10)
            {
                (player.inventory[9], player.inventory[slot]) = (player.inventory[slot], player.inventory[9]);
                stashedSlot = slot;
                slot = 9;
            }
            heldSlot = slot;
            Interrupt(player, "umbrella");
            player.selectedItem = slot;
            player.controlDown = player.gravDir > 0;
            player.controlUp = player.gravDir < 0;
            return true;
        }
        if (!player.AnyExtraJumpUsable()) return false;
        bool press = player.releaseJump;
        Interrupt(player, press ? "extra_jump" : "release_jump");
        player.controlJump = press;
        return true;
    }

    public static bool DangerousLandingSoon(Player player)
    {
        if (player.stoned || player.noFallDmg || player.equippedWings != null || player.slowFall
            || player.wet || player.velocity.Y * player.gravDir <= 0) return false;
        var position = player.position;
        var velocity = player.velocity;
        for (int frame = 0; frame < 12; frame++)
        {
            velocity.Y += player.gravity * player.gravDir;
            if (velocity.Y * player.gravDir > player.maxFallSpeed)
                velocity.Y = player.maxFallSpeed * player.gravDir;
            var allowed = Collision.TileCollision(position, velocity, player.width, player.height,
                false, false, (int)player.gravDir);
            position += allowed;
            if (allowed.Y * player.gravDir < velocity.Y * player.gravDir)
                return ((int)(position.Y / 16f) - player.fallStart) * player.gravDir > 25 + player.extraFall;
        }
        return false;
    }

    static void Interrupt(Player player, string action)
    {
        Interventions++;
        RecedingNav.InterruptForSurvival();
        ClearControls(player);
        DiagLog.Write($"[survival] action={action} pos=({player.position.X:0.##},{player.position.Y:0.##}) vy={player.velocity.Y:0.##} fall_start={player.fallStart}");
    }

    static void ClearControls(Player player)
    {
        player.controlLeft = player.controlRight = player.controlJump = false;
        player.controlUp = player.controlDown = player.controlUseItem = false;
    }
}
