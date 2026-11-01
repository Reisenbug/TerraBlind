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
    static bool active;
    static int slot, blockSlot, savedSlot, idle, elapsed, retry;
    static float destinationX;
    public static int Recoveries { get; private set; }

    public static void Reset(Player player)
    {
        if (active) player.selectedItem = savedSlot;
        active = false;
        dig.Clear();
        seal.Clear();
    }

    public static bool Apply(Player player)
    {
        if (!active)
        {
            if (retry > 0) { retry--; return false; }
            if (player.breath >= player.breathMax - 25 || player.gills || player.accMerman
                || player.gravDir != 1 || player.velocity.Y != 0 || !player.wet) return false;
            slot = -1;
            for (int i = 0; i < 10; i++)
                if (player.inventory[i].pick > 0 && (slot < 0 || player.inventory[i].pick > player.inventory[slot].pick)) slot = i;
            if (slot < 0 || !FindPocket(player) && !FindSealedPocket(player)) { retry = 60; return false; }
            RecedingNav.InterruptForSurvival();
            savedSlot = player.selectedItem;
            active = true; idle = elapsed = 0;
            DiagLog.Write($"[survival-air] start breath={player.breath} target_x={destinationX} tiles={dig.Count} seal={seal.Count}");
        }
        player.controlLeft = player.controlRight = player.controlJump = player.controlDown = player.controlUp = player.controlUseItem = false;
        if (player.breath >= player.breathMax)
        {
            Recoveries++;
            DiagLog.Write($"[survival-air] recovered frames={elapsed} hp={player.statLife}");
            Reset(player);
            return true;
        }
        if (++elapsed > 900 || ++idle > 180)
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
            if (!LavaSafety.CanMine(target.X, target.Y) || !Reach.CanMine(player, target.X, target.Y)
                || !WorldGen.CanKillTile(target.X, target.Y))
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

    static bool FindPocket(Player player)
    {
        int floor = (int)(player.Bottom.Y / 16);
        foreach (int direction in new[] { 1, -1 })
        {
            int entrance = direction > 0 ? (int)Math.Ceiling(player.Right.X / 16) : (int)(player.Left.X / 16) - 1;
            int left = direction > 0 ? entrance : entrance - 2;
            int pocketLeft = direction > 0 ? entrance + 1 : left;
            if (left < 1 || left + 3 >= Main.maxTilesX || floor < 6 || floor >= Main.maxTilesY - 1) continue;
            bool valid = true;
            for (int x = left; x < left + 3; x++)
            for (int y = floor - 5; y <= floor; y++)
            {
                var tile = Main.tile[x, y];
                if (!tile.HasTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]
                    || tile.IsActuated || !LavaSafety.CanMine(x, y)
                    || y < floor && (!Reach.CanMine(player, x, y) || !WorldGen.CanKillTile(x, y))) valid = false;
            }
            if (!valid) continue;
            for (int y = floor - 3; y < floor; y++) dig.Enqueue(new Point(entrance, y));
            for (int y = floor - 3; y >= floor - 5; y--)
            for (int x = pocketLeft; x < pocketLeft + 2; x++) dig.Enqueue(new Point(x, y));
            for (int y = floor - 2; y < floor; y++)
            for (int x = pocketLeft; x < pocketLeft + 2; x++) dig.Enqueue(new Point(x, y));
            destinationX = pocketLeft * 16 + (32 - player.width) / 2f;
            return true;
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
        for (int x = left - 1; x <= left + 2; x++)
        for (int y = floor; y <= floor + 4; y++)
        {
            var tile = Main.tile[x, y];
            if (!tile.HasTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]
                || tile.IsActuated || tile.IsHalfBlock || tile.Slope != Terraria.ID.SlopeType.Solid
                || !LavaSafety.CanMine(x, y) || !WorldGen.CanKillTile(x, y)) return false;
        }
        for (int x = left - 1; x <= left + 2; x++)
        for (int y = floor - 4; y < floor; y++)
            if (Main.tile[x, y].HasTile || !LavaSafety.CanMine(x, y)) return false;
        for (int y = floor - 1; y >= floor - 4; y--)
        { seal.Enqueue(new Point(left - 1, y)); seal.Enqueue(new Point(left + 2, y)); }
        seal.Enqueue(new Point(left, floor - 4)); seal.Enqueue(new Point(left + 1, floor - 4));
        for (int y = floor; y < floor + 4; y++)
        for (int x = left; x <= left + 1; x++) dig.Enqueue(new Point(x, y));
        destinationX = player.position.X;
        return true;
    }
}
