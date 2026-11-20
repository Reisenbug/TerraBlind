using Microsoft.Xna.Framework;
using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class NavigationSurvival
{
    static int heldSlot = -1, previousSlot = -1, stashedSlot = -1;
    static bool holdingPlacement, holdingPotion;
    static int heldType;
    static int webFallStart;
    static int gravityPhase, gravityTicks;
    static float originalGravity;
    static Point webTarget;
    static int lavaRedirect;
    public static int Interventions { get; private set; }

    public static void Reset(Player player)
    {
        if (stashedSlot >= 0 && (player.inventory[heldSlot].type == heldType || player.inventory[heldSlot].IsAir))
            (player.inventory[heldSlot], player.inventory[stashedSlot]) = (player.inventory[stashedSlot], player.inventory[heldSlot]);
        if (heldSlot >= 0 && player.selectedItem == heldSlot && previousSlot >= 0)
            player.selectedItem = previousSlot;
        heldSlot = previousSlot = stashedSlot = -1;
        holdingPlacement = holdingPotion = false;
        gravityPhase = gravityTicks = 0;
    }

    internal static void ApplyLavaRedirect(Player player)
    {
        const int horizon = 45;
        bool navigating = RecedingNav.Active;
        if (!(navigating || AgentTools.AutonomyActive) || player.dead || player.lavaImmune || player.lavaTime > 0 || player.gravDir != 1
            || player.velocity.Y <= 0 || player.mount.Active || player.grappling[0] >= 0
            || !AgentTools.ControlAllows(ControlBehavior.Survival, ControlChannels.Movement, delegated: true))
        { lavaRedirect = 0; return; }

        if (!navigating && lavaRedirect == 0)
        {
            int horizontal = (int)System.MathF.Ceiling(horizon * System.Math.Max(System.MathF.Abs(player.velocity.X),
                System.Math.Max(player.maxRunSpeed, player.accRunSpeed)));
            int vertical = (int)System.MathF.Ceiling(horizon * System.Math.Max(player.velocity.Y, player.maxFallSpeed));
            if (!Collision.LavaCollision(player.position - new Vector2(horizontal, 0),
                player.width + 2 * horizontal, player.height + vertical)) return;
        }

        (bool Lava, bool Safe) Forecast(int direction)
        {
            var position = player.position;
            var velocity = player.velocity;
            bool up = Collision.up, down = Collision.down;
            try
            {
                for (int frame = 0; frame < horizon; frame++)
                {
                    if (direction != 0)
                    {
                        float desired = direction * System.Math.Max(player.maxRunSpeed, player.accRunSpeed);
                        velocity.X += System.Math.Clamp(desired - velocity.X, -player.runAcceleration, player.runAcceleration);
                    }
                    velocity.Y = System.Math.Min(velocity.Y + player.gravity, player.maxFallSpeed);
                    var movement = Collision.TileCollision(position, velocity, player.width, player.height,
                        player.controlDown, player.controlDown);
                    if (player.waterWalk || player.waterWalk2)
                        movement = Collision.WaterCollision(position, movement, player.width, player.height, player.controlDown, false, player.waterWalk);
                    position += movement;
                    if (Collision.LavaCollision(position, player.width, player.height)) return (true, false);
                    if (!AgentTools.Control.BoundsAllowed(ControlBehavior.Navigation, position.X / 16, position.Y / 16,
                        player.width / 16f, player.height / 16f)) return (false, false);
                    if (movement.Y < velocity.Y)
                        return (false, FallSafety.LandingDamage(player, position, player.fallStart) < player.statLife);
                    if (movement.X != velocity.X) velocity.X = 0;
                }
                return (false, false);
            }
            finally { Collision.up = up; Collision.down = down; }
        }

        int current = player.controlRight ? 1 : player.controlLeft ? -1 : 0;
        if (lavaRedirect == 0 || !Forecast(lavaRedirect).Safe)
        {
            lavaRedirect = 0;
            if (!Forecast(current).Lava) return;
            int away = player.velocity.X > 0 ? -1 : 1;
            foreach (int direction in new[] { away, -away })
            {
                if (direction == current || !Forecast(direction).Safe) continue;
                lavaRedirect = direction;
                if (navigating) RecedingNav.InterruptForSurvival();
                DiagLog.Write($"[survival] action=lava_redirect navigation={navigating} direction={direction} pos=({player.position.X:0.##},{player.position.Y:0.##}) vx={player.velocity.X:0.##} vy={player.velocity.Y:0.##}");
                break;
            }
        }
        if (lavaRedirect == 0) return;
        player.controlRight = lavaRedirect > 0;
        player.controlLeft = lavaRedirect < 0;
    }

    public static bool Apply(Player player)
    {
        if (!(RecedingNav.Active || AgentTools.AutonomyActive) || player.dead || Manual.On
            || !AgentTools.ControlAllows(ControlBehavior.Survival, ControlChannels.All, delegated: true))
        {
            Reset(player);
            NavigationAirPocket.Reset(player);
            return false;
        }
        if (NavigationAirPocket.Apply(player)) return true;
        if (FallSafety.Immune(player))
        {
            Reset(player);
            return false;
        }
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
        if (heldSlot >= 0 && holdingPlacement && heldType == ItemID.Cobweb && player.fallStart > webFallStart)
        {
            DiagLog.Write($"[survival-placement] outcome=fall_reset target=({webTarget.X},{webTarget.Y}) fall_start={player.fallStart}");
            Reset(player);
        }
        if (heldSlot >= 0 && holdingPlacement && (heldType == ItemID.Cobweb
            ? player.position.Y >= (webTarget.Y + 1) * 16 : player.Bottom.Y > webTarget.Y * 16))
        {
            DiagLog.Write($"[survival-placement] outcome=missed target=({webTarget.X},{webTarget.Y}) pos=({player.position.X:0.##},{player.position.Y:0.##}) vx={player.velocity.X:0.##} vy={player.velocity.Y:0.##}");
            Reset(player);
        }
        if (heldSlot >= 0)
        {
            if (player.velocity.Y == 0 && VanillaTerrainPlan.HasSupport(player))
            {
                if (holdingPlacement) DiagLog.Write($"[survival-placement] outcome=landed target=({webTarget.X},{webTarget.Y}) fall_start={player.fallStart}");
                Reset(player);
                return false;
            }
            if (holdingPlacement)
            {
                ClearControls(player);
                if (WebCanReplace(webTarget) && player.inventory[heldSlot].type == heldType)
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
                if (player.HasBuff(BuffID.Featherfall) || player.inventory[heldSlot].type != heldType)
                {
                    Reset(player);
                    return false;
                }
                ClearControls(player);
                player.selectedItem = heldSlot;
                player.delayUseItem = false; player.mouseInterface = false;
                player.controlUseItem = true;
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
        if (TryPlatformCatch(player)) return true;
        if (!DangerousLandingSoon(player, out var landing))
        {
            if (player.gravDir != 1 || player.velocity.Y <= 0 || player.AnyExtraJumpUsable()) return false;
            int cobweb = FindCobweb(player);
            if (cobweb < 0) return false;
            var forecast = ProjectLanding(player, 36);
            if (!forecast.Landed || forecast.Damage < player.statLife) return false;
            for (int frame = 4; frame < forecast.Frames && frame <= 12; frame += 2)
            {
                var intercept = ProjectLanding(player, frame).Position;
                if (TryPlaceCobweb(player, cobweb, intercept, early: true)) return true;
            }
            return false;
        }
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
            int cobweb = FindCobweb(player);
            return cobweb >= 0 && TryPlaceCobweb(player, cobweb, landing, early: false);
        }
        bool press = player.releaseJump;
        Interrupt(player, press ? "extra_jump" : "release_jump");
        player.controlJump = press;
        return true;
    }

    static int FindCobweb(Player player)
    {
        for (int slot = 0; slot < 50; slot++)
            if (player.inventory[slot].type == ItemID.Cobweb && !player.inventory[slot].IsAir) return slot;
        return -1;
    }

    static bool TryPlaceCobweb(Player player, int slot, Vector2 position, bool early)
    {
        int center = (int)((position.X + player.width / 2f) / 16);
        int bottom = (int)((position.Y + player.height - 1) / 16);
        Point? placement = null;
        int previous = player.selectedItem;
        try
        {
            player.selectedItem = slot;
            foreach (int offset in early ? new[] { 0 } : new[] { -1, -2, 0, 1 })
            {
                int y = bottom + offset;
                if (early && y * 16 < player.Bottom.Y) continue;
                foreach (int x in new[] { center, (int)(position.X / 16), (int)((position.X + player.width - 1) / 16) })
                {
                    var candidate = new Point(x, y);
                    if (!WorldGen.InWorld(x, y) || !WebCanReplace(candidate) || !Reach.CanPlace(player, x, y)
                        || !LavaSafety.CanMine(x, y) || AgentTools.Control.ForbiddenAt(ControlBehavior.Placement, x, y)) continue;
                    if (Main.tile[x, y].WallType == 0 && !ItemUseCoordinator.HasAnchor(x, y)) continue;
                    placement = candidate; break;
                }
                if (placement.HasValue) break;
            }
        }
        finally { player.selectedItem = previous; }
        if (!placement.HasValue) return false;
        HoldItem(player, slot); holdingPlacement = true; webTarget = placement.Value;
        webFallStart = player.fallStart;
        Interrupt(player, early ? "place_cobweb_early" : "place_cobweb");
        player.delayUseItem = false; player.mouseInterface = false;
        Cursor.AimTile(webTarget.X, webTarget.Y); player.controlUseItem = true;
        return true;
    }

    static bool WebCanReplace(Point target)
    {
        var tile = Main.tile[target.X, target.Y];
        return !tile.HasTile || Main.tileCut[tile.TileType] || TileID.Sets.BreakableWhenPlacing[tile.TileType];
    }

    static bool TryPlatformCatch(Player player)
    {
        if (FallSafety.Immune(player) || player.gravDir != 1 || player.AnyExtraJumpUsable()
            || player.itemAnimation > 0 || player.itemTime > 0) return false;
        var projected = ProjectLanding(player, 60);
        if (projected.Damage <= 0) return false;
        float nextVelocity = System.Math.Min(player.velocity.Y + player.gravity, player.maxFallSpeed);
        float followingVelocity = System.Math.Min(nextVelocity + player.gravity, player.maxFallSpeed);
        int y = (int)System.Math.Ceiling((player.Bottom.Y + nextVelocity + followingVelocity) / 16);
        int catchDamage = FallSafety.LandingDamage(player, new Vector2(player.position.X, y * 16 - player.height), player.fallStart);
        if (catchDamage >= player.statLife || catchDamage > 0 && catchDamage >= projected.Damage) return false;
        int frames = 0;
        float descent = 0, velocity = player.velocity.Y;
        while (descent < y * 16 - player.Bottom.Y && frames < 60)
        {
            velocity = System.Math.Min(velocity + player.gravity, player.maxFallSpeed);
            descent += velocity; frames++;
        }
        float left = player.position.X + player.velocity.X * frames;
        for (int slot = 0; slot < 50; slot++)
        {
            if (player.inventory[slot].IsAir || player.inventory[slot].createTile != TileID.Platforms) continue;
            int previous = player.selectedItem;
            Point? placement = null;
            try
            {
                player.selectedItem = slot;
                foreach (int x in new[] { (int)((left + player.width / 2f) / 16), (int)(left / 16), (int)((left + player.width - 1) / 16) })
                {
                    var candidate = new Point(x, y);
                    if (WorldGen.InWorld(x, y) && WebCanReplace(candidate) && Reach.CanPlace(player, x, y)
                        && !LavaSafety.IsLava(x, y) && VanillaTerrainPlan.HasPlatformAnchor(x, y))
                    { placement = candidate; break; }
                }
            }
            finally { player.selectedItem = previous; }
            if (!placement.HasValue) continue;
            HoldItem(player, slot); holdingPlacement = true; webTarget = placement.Value;
            DiagLog.Write($"[survival-platform-choice] projected_damage={projected.Damage} catch_damage={catchDamage} projected_frames={projected.Frames} target=({webTarget.X},{webTarget.Y})");
            Interrupt(player, "place_platform");
            player.delayUseItem = false; player.mouseInterface = false;
            Cursor.AimTile(webTarget.X, webTarget.Y); player.controlUseItem = true;
            return true;
        }
        return false;
    }

    public static bool DangerousLandingSoon(Player player)
        => DangerousLandingSoon(player, out _);

    static bool DangerousLandingSoon(Player player, out Vector2 landing, bool includeOpenFall = false, int horizon = 12)
    {
        var projected = ProjectLanding(player, horizon);
        landing = projected.Position;
        return projected.Damage > 0 && (projected.Landed || includeOpenFall);
    }

    public static JsonObject AssessFall(Player player)
    {
        var projected = ProjectLanding(player, 60);
        return new JsonObject { ["immune"] = FallSafety.Immune(player), ["landed"] = projected.Landed,
            ["frames"] = projected.Frames, ["damage"] = projected.Damage,
            ["lethal"] = projected.Damage >= player.statLife && projected.Damage > 0,
            ["landing_pixels"] = new JsonObject { ["x"] = projected.Position.X, ["y"] = projected.Position.Y },
            ["assumption"] = "Current horizontal velocity and gravity with native tile collisions; no new controls, tile changes or entity collisions. An unlanded tail is a 60-frame bound, not a confirmed landing." };
    }

    static (Vector2 Position, bool Landed, int Frames, int Damage) ProjectLanding(Player player, int horizon)
    {
        if (FallSafety.Immune(player) || player.velocity.Y * player.gravDir <= 0)
            return (player.position, false, 0, 0);
        var position = player.position;
        var velocity = player.velocity;
        int fallStart = player.fallStart;
        for (int frame = 0; frame < horizon; frame++)
        {
            velocity.Y += player.gravity * player.gravDir;
            if (velocity.Y * player.gravDir > player.maxFallSpeed)
                velocity.Y = player.maxFallSpeed * player.gravDir;
            var allowed = Collision.TileCollision(position, velocity, player.width, player.height,
                false, false, (int)player.gravDir);
            if (player.gravDir == 1 && (player.waterWalk || player.waterWalk2))
                allowed = Collision.WaterCollision(position, allowed, player.width, player.height, player.controlDown, false, player.waterWalk);
            position += allowed;
            if (allowed.Y * player.gravDir < velocity.Y * player.gravDir)
                return (position, true, frame + 1, FallSafety.LandingDamage(player, position, fallStart));
            fallStart = FallSafety.ResetDistance(player, position, fallStart, velocity.Y, frame + 1);
        }
        return (position, false, horizon, FallSafety.LandingDamage(player, position, fallStart));
    }

    static void Interrupt(Player player, string action)
    {
        Interventions++;
        RecedingNav.InterruptForSurvival();
        ClearControls(player);
        DiagLog.Write($"[survival] action={action} pos=({player.position.X:0.##},{player.position.Y:0.##}) vx={player.velocity.X:0.##} vy={player.velocity.Y:0.##} fall_start={player.fallStart} target=({webTarget.X},{webTarget.Y})");
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
