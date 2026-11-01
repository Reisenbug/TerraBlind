using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class NavigationAirPocket
{
    static readonly Queue<Point> dig = new();
    static bool active;
    static int slot, savedSlot, idle, elapsed, retry;
    static float destinationX;
    public static int Recoveries { get; private set; }

    public static void Reset(Player player)
    {
        if (active) player.selectedItem = savedSlot;
        active = false;
        dig.Clear();
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
            if (slot < 0 || !FindPocket(player)) { retry = 60; return false; }
            RecedingNav.InterruptForSurvival();
            savedSlot = player.selectedItem;
            active = true; idle = elapsed = 0;
            DiagLog.Write($"[survival-air] start breath={player.breath} target_x={destinationX} tiles={dig.Count}");
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
}
