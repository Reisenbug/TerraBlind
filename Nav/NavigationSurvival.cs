using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class NavigationSurvival
{
    static int heldSlot = -1, previousSlot = -1, stashedSlot = -1;
    static bool holdingWeb, holdingPotion;
    static int heldType;
    static int gravityPhase, gravityTicks;
    static float originalGravity;
    static Point webTarget;
    public static int Interventions { get; private set; }

    public static void Reset(Player player)
    {
        if (stashedSlot >= 0 && (player.inventory[heldSlot].type == heldType || player.inventory[heldSlot].IsAir))
            (player.inventory[heldSlot], player.inventory[stashedSlot]) = (player.inventory[stashedSlot], player.inventory[heldSlot]);
        if (heldSlot >= 0 && player.selectedItem == heldSlot && previousSlot >= 0)
            player.selectedItem = previousSlot;
        heldSlot = previousSlot = stashedSlot = -1;
        holdingWeb = holdingPotion = false;
        gravityPhase = gravityTicks = 0;
    }

    public static bool Apply(Player player)
    {
        if (!RecedingNav.Active || player.dead)
        {
            Reset(player);
            NavigationAirPocket.Reset(player);
            return false;
        }
        if (NavigationAirPocket.Apply(player)) return true;
        if (gravityPhase != 0)
        {
            ClearControls(player);
            if (++gravityTicks > 12 || player.mount.Active || player.forcedGravity > 0 || !(player.gravControl || player.gravControl2))
            { gravityPhase = 0; return false; }
            if (gravityPhase == 1 && player.gravDir != originalGravity)
            { gravityPhase = 2; return true; }
            if (gravityPhase == 2 && player.gravDir == originalGravity)
            { gravityPhase = 0; return true; }
            player.controlUp = player.releaseUp;
            return true;
        }
        if (heldSlot >= 0)
        {
            if (player.velocity.Y == 0 && VanillaTerrainPlan.HasSupport(player))
            {
                Reset(player);
                return false;
            }
            if (holdingWeb)
            {
                ClearControls(player);
                if (WebCanReplace(webTarget) && player.inventory[heldSlot].type == ItemID.Cobweb)
                {
                    player.selectedItem = heldSlot;
                    player.delayUseItem = false; player.mouseInterface = false;
                    Cursor.AimTile(webTarget.X, webTarget.Y);
                    player.controlUseItem = true;
                }
                return true;
            }
            if (holdingPotion)
            {
                ClearControls(player);
                player.selectedItem = heldSlot;
                player.delayUseItem = false; player.mouseInterface = false;
                player.controlUseItem = !player.HasBuff(BuffID.Featherfall) && player.inventory[heldSlot].type == ItemID.FeatherfallPotion;
                return true;
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
        if (!DangerousLandingSoon(player, out var landing)) return false;
        for (int slot = 0; slot < 50; slot++)
        {
            if (player.inventory[slot].type is not (ItemID.Umbrella or ItemID.TragicUmbrella)) continue;
            HoldItem(player, slot);
            Interrupt(player, "umbrella");
            player.controlDown = player.gravDir > 0;
            player.controlUp = player.gravDir < 0;
            return true;
        }
        if (!player.AnyExtraJumpUsable())
        {
            if ((player.gravControl || player.gravControl2) && !player.mount.Active && player.forcedGravity == 0)
            {
                Interrupt(player, "gravity_pair");
                originalGravity = player.gravDir; gravityPhase = 1; gravityTicks = 0;
                player.controlUp = player.releaseUp;
                return true;
            }
            for (int slot = 0; slot < 50; slot++)
            {
                if (player.inventory[slot].type != ItemID.FeatherfallPotion) continue;
                HoldItem(player, slot); holdingPotion = true;
                Interrupt(player, "featherfall_potion");
                player.delayUseItem = false; player.mouseInterface = false;
                player.controlUseItem = true;
                return true;
            }
            if (player.gravDir != 1) return false;
            var target = new Point((int)((landing.X + player.width / 2f) / 16), (int)((landing.Y + player.height - 1) / 16));
            for (int slot = 0; slot < 50; slot++)
            {
                if (player.inventory[slot].type != ItemID.Cobweb) continue;
                Point? placement = null;
                int previous = player.selectedItem;
                try
                {
                    player.selectedItem = slot;
                    foreach (int offset in new[] { 0, 1, -1, -2 })
                    {
                        var candidate = new Point(target.X, target.Y + offset);
                        if (!WorldGen.InWorld(candidate.X, candidate.Y) || !WebCanReplace(candidate)
                            || !Reach.CanPlace(player, candidate.X, candidate.Y) || !LavaSafety.CanMine(candidate.X, candidate.Y)) continue;
                        if (Main.tile[candidate.X, candidate.Y].WallType == 0 && !ItemUseCoordinator.HasAnchor(candidate.X, candidate.Y)) continue;
                        placement = candidate; break;
                    }
                }
                finally { player.selectedItem = previous; }
                if (!placement.HasValue) return false;
                HoldItem(player, slot); holdingWeb = true; webTarget = placement.Value;
                Interrupt(player, "place_cobweb");
                player.delayUseItem = false; player.mouseInterface = false;
                Cursor.AimTile(webTarget.X, webTarget.Y); player.controlUseItem = true;
                return true;
            }
            return false;
        }
        bool press = player.releaseJump;
        Interrupt(player, press ? "extra_jump" : "release_jump");
        player.controlJump = press;
        return true;
    }

    static bool WebCanReplace(Point target)
    {
        var tile = Main.tile[target.X, target.Y];
        return !tile.HasTile || Main.tileCut[tile.TileType] || TileID.Sets.BreakableWhenPlacing[tile.TileType];
    }

    public static bool DangerousLandingSoon(Player player)
        => DangerousLandingSoon(player, out _);

    static bool DangerousLandingSoon(Player player, out Vector2 landing)
    {
        landing = player.position;
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
            {
                landing = position;
                return ((int)(position.Y / 16f) - player.fallStart) * player.gravDir > 25 + player.extraFall;
            }
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

    static void HoldItem(Player player, int slot)
    {
        previousSlot = player.selectedItem;
        heldType = player.inventory[slot].type;
        if (slot >= 10)
        {
            (player.inventory[9], player.inventory[slot]) = (player.inventory[slot], player.inventory[9]);
            stashedSlot = slot;
            slot = 9;
        }
        heldSlot = slot;
        player.selectedItem = slot;
    }

    static void ClearControls(Player player)
    {
        player.controlLeft = player.controlRight = player.controlJump = false;
        player.controlUp = player.controlDown = player.controlUseItem = false;
    }
}
