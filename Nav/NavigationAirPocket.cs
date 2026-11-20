using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class NavigationAirPocket
{
    static readonly Queue<Point> dig = new();
    static readonly Queue<Point> seal = new();
    static bool active, sealedPocket, surfacing;
    static bool surfaceFailed, surfaceJumpStarted;
    static Vector2 surfaceOrigin, failedSurfaceOrigin;
    static float surfacePeakY;
    static int surfaceBestBreath, surfaceIdle;
    static int slot, blockSlot, savedSlot, idle, elapsed, retry;
    static float destinationX;
    static int plannedFrames;
    const int EscapeReserveFrames = 150;
    public static int Recoveries { get; private set; }

    internal static bool NeedsEmergencyItem(Player player, bool submerged)
        => submerged && !player.gills && !player.accMerman && !active && !surfacing && BreathBudget(player) <= 120;

    public static void Reset(Player player)
    {
        if (active) player.selectedItem = savedSlot;
        active = false;
        sealedPocket = false;
        surfacing = false;
        surfaceFailed = false;
        dig.Clear();
        seal.Clear();
    }

    public static bool Apply(Player player)
    {
        if (player.gills || player.accMerman || player.gravDir != 1 || AgentTools.ContactEscapeActive)
        { Reset(player); return false; }
        bool submerged = Collision.DrownCollision(player.position, player.width, player.height, player.gravDir);
        int budget = BreathBudget(player);
        if (!active && ApplySurface(player, submerged, budget)) return true;
        if (!active)
        {
            if (retry > 0) { retry--; return false; }
            if (!submerged || player.velocity.Y != 0) return false;
            if (!AgentTools.ControlAllows(ControlBehavior.Mining, ControlChannels.ItemUse, delegated: true)) return false;
            slot = -1;
            for (int i = 0; i < 10; i++)
                if (player.inventory[i].pick > 0 && (slot < 0 || player.inventory[i].pick > player.inventory[slot].pick)) slot = i;
            bool canSeal = AgentTools.ControlAllows(ControlBehavior.Placement, ControlChannels.ItemUse, delegated: true);
            if (slot < 0 || !FindPocket(player) && (!canSeal || !FindSealedPocket(player))) { retry = 60; return false; }
            if (budget > plannedFrames + EscapeReserveFrames)
            {
                dig.Clear(); seal.Clear(); sealedPocket = false;
                retry = Math.Clamp((budget - plannedFrames - EscapeReserveFrames) / 2, 1, 60);
                return false;
            }
            if (RecedingNav.Active) RecedingNav.InterruptForSurvival();
            savedSlot = player.selectedItem;
            active = true; idle = elapsed = 0;
            DiagLog.Write($"[survival-air] start breath={player.breath} target_x={destinationX} tiles={dig.Count} seal={seal.Count} planned_frames={plannedFrames} budget_frames={BreathBudget(player)} pick={player.inventory[slot].pick}");
            AgentChat.Say("Low air. Making an air pocket.");
        }
        player.controlLeft = player.controlRight = player.controlJump = player.controlDown = player.controlUp = player.controlUseItem = false;
        if (player.breath >= player.breathMax)
        {
            if (sealedPocket)
            {
                int missing = 6 - Predicates.Have(ItemID.WoodPlatform);
                int wood = Predicates.Have(ItemID.Wood);
                if (missing > 0 && wood > 0)
                {
                    Recipe.FindRecipes();
                    int crafted = CraftCoordinator.Craft(ItemID.WoodPlatform, Math.Min(missing, wood * 2));
                    DiagLog.Write($"[survival-air] escape_platforms crafted={crafted} result={CraftCoordinator.LastStop}");
                }
            }
            Recoveries++;
            DiagLog.Write($"[survival-air] recovered frames={elapsed} hp={player.statLife}");
            Reset(player);
            return true;
        }
        if (++elapsed > plannedFrames || ++idle > 180)
        {
            DiagLog.Write($"[survival-air] stalled remaining={dig.Count} breath={player.breath}");
            Reset(player); retry = 60;
            return true;
        }
        while (dig.Count > 0 && !Main.tile[dig.Peek().X, dig.Peek().Y].HasTile)
        { dig.Dequeue(); idle = 0; }
        while (seal.Count > 0 && Main.tile[seal.Peek().X, seal.Peek().Y].HasTile)
        { seal.Dequeue(); idle = 0; }
        if (seal.Count > 0)
        {
            var target = seal.Peek();
            if (player.inventory[blockSlot].IsAir || !Reach.CanPlace(player, target.X, target.Y)
                || !LavaSafety.CanMine(target.X, target.Y))
            { Reset(player); retry = 60; return true; }
            player.selectedItem = blockSlot;
            player.delayUseItem = false; player.mouseInterface = false;
            Cursor.AimTile(target.X, target.Y);
            player.controlUseItem = true;
            return true;
        }
        if (dig.Count > 0)
        {
            var target = dig.Peek();
            if (!CanDig(player, target.X, target.Y)
                || DigTable.CostFrames(target.X, target.Y, player, player.inventory[slot]) + 60 > BreathBudget(player))
            { Reset(player); retry = 60; return true; }
            player.selectedItem = slot;
            player.delayUseItem = false; player.mouseInterface = false;
            Cursor.AimTile(target.X, target.Y);
            player.controlUseItem = true;
        }
        else
        {
            player.controlRight = player.position.X < destinationX - 2;
            player.controlLeft = player.position.X > destinationX + 2;
            player.controlJump = player.releaseJump && player.velocity.Y == 0;
            if (!Collision.DrownCollision(player.position, player.width, player.height, player.gravDir)) idle = 0;
        }
        return true;
    }

    static bool ApplySurface(Player player, bool submerged, int budget)
    {
        if (player.breath >= player.breathMax)
        {
            if (surfacing) DiagLog.Write($"[survival-air] surfaced breath={player.breath}");
            surfacing = surfaceFailed = false;
            return false;
        }
        bool canSwim = player.accFlipper || player.wingTime > 0;
        if (!surfacing)
        {
            if (!submerged) return false;
            if (surfaceFailed && Vector2.DistanceSquared(player.position, failedSurfaceOrigin) < 32 * 32) return false;
            if (budget > EscapeReserveFrames + 110) return false;
            var ascent = AirRise(player, canSwim);
            if (ascent.Pixels == 0 || budget > EscapeReserveFrames + ascent.Frames) return false;
            if (RecedingNav.Active) RecedingNav.InterruptForSurvival();
            surfacing = true;
            surfaceOrigin = player.position;
            surfaceJumpStarted = player.velocity.Y != 0;
            surfacePeakY = player.position.Y;
            surfaceBestBreath = player.breath;
            surfaceIdle = 0;
            DiagLog.Write($"[survival-air] surfacing breath={player.breath} budget_frames={budget} rise={ascent.Pixels} estimated_frames={ascent.Frames} x={surfaceOrigin.X:0.##} y={surfaceOrigin.Y:0.##}");
            AgentChat.Say("Low air. Surfacing.");
        }
        else
        {
            if (player.position.Y < surfacePeakY - 1 || player.breath > surfaceBestBreath)
            {
                surfacePeakY = Math.Min(surfacePeakY, player.position.Y);
                surfaceBestBreath = Math.Max(surfaceBestBreath, player.breath);
                surfaceIdle = 0;
            }
            else surfaceIdle++;
            surfaceJumpStarted |= player.velocity.Y != 0;
            if (submerged && (surfaceIdle >= 30 || !canSwim && surfaceJumpStarted
                && player.velocity.Y >= 0 && !player.AnyExtraJumpUsable()))
            {
                surfacing = false;
                surfaceFailed = true;
                failedSurfaceOrigin = surfaceOrigin;
                DiagLog.Write($"[survival-air] ascent_failed breath={player.breath} rise={surfaceOrigin.Y - surfacePeakY:0.##}");
                return false;
            }
        }
        player.controlLeft = player.controlRight = player.controlDown = player.controlUseItem = false;
        player.controlUp = submerged && !player.gravControl && !player.gravControl2;
        player.controlJump = player.accFlipper ? submerged && player.releaseJump
            : canSwim || (submerged && (surfaceJumpStarted || player.releaseJump))
                || surfaceJumpStarted && player.velocity.Y < 0;
        if (submerged && !canSwim && player.AnyExtraJumpUsable() && player.velocity.Y >= 0 && player.velocity.Y != 0)
            player.controlJump = player.releaseJump;
        return true;
    }

    static (int Pixels, int Frames) AirRise(Player player, bool canSwim)
    {
        bool extraJump = player.AnyExtraJumpUsable();
        if (!canSwim && player.velocity.Y != 0 && player.jump <= 0 && !extraJump) return default;
        float waterScale = player.ignoreWater || player.trident ? 1f : player.honeyWet || player.shimmerWet ? 0.25f : 0.5f;
        float speed = Player.jumpSpeed * waterScale;
        int hold = player.velocity.Y == 0 ? Player.jumpHeight : player.jump;
        int reach = canSwim ? 128 : Math.Min(128, Math.Max(extraJump ? 32 : 0, (int)(hold * speed)));
        bool collisionUp = Collision.up, collisionDown = Collision.down;
        try
        {
            for (int pixels = 4; pixels <= reach; pixels += 4)
            {
                var rise = new Vector2(0, -pixels);
                if (Collision.TileCollision(player.position, rise, player.width, player.height).Y != rise.Y) return default;
                if (!Collision.DrownCollision(player.position + rise, player.width, player.height, player.gravDir))
                    return (pixels, 24 + (int)Math.Ceiling(pixels / Math.Max(1f, canSwim ? 2f : speed)));
            }
        }
        finally { Collision.up = collisionUp; Collision.down = collisionDown; }
        return default;
    }

    static bool FindPocket(Player player)
    {
        int floor = (int)(player.Bottom.Y / 16);
        foreach (int direction in new[] { 1, -1 })
        {
            int entrance = direction > 0 ? (int)Math.Ceiling(player.Right.X / 16) : (int)(player.Left.X / 16) - 1;
            int left = direction > 0 ? entrance : entrance - 2;
            int pocketLeft = direction > 0 ? entrance + 1 : left;
            if (left < 2 || left + 4 >= Main.maxTilesX || floor < 7 || floor >= Main.maxTilesY - 1) continue;
            bool valid = true;
            for (int x = left; x < left + 3; x++)
                if (!SolidSeal(x, floor - 6)) valid = false;
            int back = direction > 0 ? left + 3 : left - 1;
            for (int y = floor - 5; y <= floor; y++)
                if (!SolidSeal(back, y)) valid = false;
            for (int x = left; x < left + 3; x++)
            for (int y = floor - 5; y <= floor; y++)
            {
                if (!SolidSeal(x, y) || !LavaSafety.CanMine(x, y)
                    || y < floor && !CanDig(player, x, y)) valid = false;
            }
            if (!valid) continue;
            for (int y = floor - 3; y < floor; y++) dig.Enqueue(new Point(entrance, y));
            for (int y = floor - 3; y >= floor - 5; y--)
            for (int x = pocketLeft; x < pocketLeft + 2; x++) dig.Enqueue(new Point(x, y));
            for (int y = floor - 2; y < floor; y++)
            for (int x = pocketLeft; x < pocketLeft + 2; x++) dig.Enqueue(new Point(x, y));
            destinationX = pocketLeft * 16 + (32 - player.width) / 2f;
            if (FitsBudget(player)) return true;
            dig.Clear(); seal.Clear();
        }
        return false;
    }

    static bool FindSealedPocket(Player player)
    {
        int left = (int)(player.Left.X / 16), floor = (int)(player.Bottom.Y / 16);
        if (left < 2 || left + 3 >= Main.maxTilesX || floor < 5 || floor + 5 >= Main.maxTilesY
            || player.Right.X > (left + 2) * 16 || Math.Abs(player.Bottom.Y - floor * 16) > 1) return false;
        blockSlot = -1;
        for (int i = 0; i < 10; i++)
            if (player.inventory[i].stack >= 10
                && player.inventory[i].type is ItemID.Wood or ItemID.DirtBlock or ItemID.StoneBlock)
            { blockSlot = i; break; }
        if (blockSlot < 0) return false;
        int depth = 0;
        for (int candidate = 2; candidate <= 4 && depth == 0; candidate++)
        {
            bool valid = true;
            int water = 6 * 255;
            for (int x = left - 1; x <= left + 2; x++)
            for (int y = floor; y <= floor + candidate; y++)
            {
                bool shell = x == left - 1 || x == left + 2 || y == floor + candidate;
                var tile = Main.tile[x, y];
                if ((shell || tile.HasTile) && !SolidSeal(x, y) || !LavaSafety.CanMine(x, y)
                    || !shell && tile.HasTile && !CanDig(player, x, y)) valid = false;
                if (!shell) water += tile.LiquidAmount;
            }
            if (valid && water <= ((3 + candidate) * 2 - 3) * 255) depth = candidate;
        }
        if (depth == 0) return false;
        for (int x = left - 1; x <= left + 2; x++)
        for (int y = floor - 4; y < floor; y++)
        {
            bool shell = x == left - 1 || x == left + 2 || y == floor - 4;
            if (Main.tile[x, y].HasTile && (!shell || !SolidSeal(x, y)) || !LavaSafety.CanMine(x, y)) return false;
        }
        for (int y = floor - 1; y >= floor - 4; y--)
        { seal.Enqueue(new Point(left - 1, y)); seal.Enqueue(new Point(left + 2, y)); }
        seal.Enqueue(new Point(left, floor - 4)); seal.Enqueue(new Point(left + 1, floor - 4));
        for (int y = floor; y < floor + depth; y++)
        for (int x = left; x <= left + 1; x++) dig.Enqueue(new Point(x, y));
        destinationX = player.position.X;
        if (!FitsBudget(player)) { dig.Clear(); seal.Clear(); return false; }
        sealedPocket = true;
        DiagLog.Write($"[survival-air] sealed_depth={depth} left={left} floor={floor}");
        return true;
    }

    static int BreathBudget(Player player) => Math.Max(0, player.breath * Math.Max(1, player.breathCDMax) - player.breathCD);

    static bool CanDig(Player player, int x, int y)
        => Reach.CanMine(player, x, y, player.inventory[slot])
            && LavaSafety.CanMine(x, y) && DigTable.MineableWith(x, y, player.inventory[slot].pick);

    static bool FitsBudget(Player player)
    {
        int frames = 120;
        var visited = new HashSet<Point>();
        foreach (var tile in dig)
        {
            if (!visited.Add(tile)) continue;
            if (!CanDig(player, tile.X, tile.Y)) return false;
            frames += DigTable.CostFrames(tile.X, tile.Y, player, player.inventory[slot]) + 2;
            if (frames > BreathBudget(player)) return false;
        }
        if (seal.Count > 0)
        {
            var block = player.inventory[blockSlot];
            if (block.stack < seal.Count) return false;
            int previousSlot = player.selectedItem;
            try
            {
                player.selectedItem = blockSlot;
                foreach (var tile in seal)
                {
                    if (!Reach.CanPlace(player, tile.X, tile.Y) || !LavaSafety.CanMine(tile.X, tile.Y)) return false;
                    frames += Math.Max(1, block.useTime) + 2;
                }
            }
            finally { player.selectedItem = previousSlot; }
        }
        plannedFrames = frames;
        return frames <= BreathBudget(player);
    }

    static bool SolidSeal(int x, int y)
    {
        var tile = Main.tile[x, y];
        return tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType]
            && !tile.IsActuated && !tile.IsHalfBlock && tile.Slope == SlopeType.Solid;
    }
}
