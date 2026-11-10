using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class FallSafety
{
    public static bool Immune(Player player)
        => player.creativeGodMode || player.shimmering || player.mount.CanFly() || player.mount.Type == 1
            || !player.stoned && (player.noFallDmg || player.equippedWings != null
                || player.mount.Active && player.mount.FallDamage == 0);

    public static int LandingDamage(Player player, Vector2 position, int fallStart)
    {
        int distance = (int)(((int)(position.Y / 16) - fallStart) * player.gravDir);
        if (Immune(player) || player.mount.Cart && Minecart.OnTrack(position, player.width, player.height)) return 0;
        int y = (int)((player.gravDir == 1 ? position.Y + player.height + 1 : position.Y - 1) / 16);
        for (int x = (int)(position.X / 16); x <= (int)((position.X + player.width) / 16); x++)
            if (WorldGen.InWorld(x, y) && Main.tile[x, y].HasTile
                && TileID.Sets.NegatesFallDamage[Main.tile[x, y].TileType]) return 0;
        if (player.stoned) return Math.Max(0, (distance - 2) * 20);
        if (player.noFallDmg || player.equippedWings != null) return 0;
        int damage = Math.Max(0, distance - 25 - player.extraFall) * 10;
        return player.mount.Active ? (int)(damage * player.mount.FallDamage) : damage;
    }

    public static int ResetDistance(Player player, Vector2 position, int fallStart, float vy, int elapsedFrames = 0)
    {
        float scale = Main.maxTilesX / 4200f;
        scale *= scale;
        float spaceGravity = (float)((position.Y / 16 - (60 + 10 * scale))
            / (Main.worldSurface / (Main.remixWorld ? 1 : 6)));
        if (vy * player.gravDir <= 0 || player.jump > elapsedFrames || player.rocketDelay > elapsedFrames
            || player.slowFall || player.tongued || spaceGravity < .8f || Wet(player, position))
            return (int)(position.Y / 16);
        return fallStart;
    }

    public static bool SafeTrajectory(Player player, Vector2 start, IReadOnlyList<PhysicsSimulator.ControlInput> frames)
    {
        if (Immune(player)) return true;
        int fallStart = Vector2.DistanceSquared(start, player.position) < 1 && player.velocity.Y * player.gravDir > 0
            ? player.fallStart : (int)(start.Y / 16);
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var position = new Vector2(frame.Px, frame.Py);
            if (frame.Vy == 0 && LandingDamage(player, position, fallStart) > 0) return false;
            fallStart = ResetDistance(player, position, fallStart, frame.Vy, i + 1);
        }
        return frames.Count == 0 || LandingDamage(player,
            new Vector2(frames[^1].Px, frames[^1].Py), fallStart) == 0;
    }

    static bool Wet(Player player, Vector2 position)
    {
        int width = Math.Min(10, player.width), height = player.height / 2;
        float left = position.X + player.width / 2 - width / 2;
        float top = position.Y + player.height / 2 - height / 2;
        for (int x = Math.Max(0, (int)(left / 16)); x <= Math.Min(Main.maxTilesX - 1, (int)((left + width) / 16)); x++)
        for (int y = Math.Max(0, (int)(top / 16)); y <= Math.Min(Main.maxTilesY - 1, (int)((top + height) / 16)); y++)
        {
            var tile = Main.tile[x, y];
            float liquidTop = y * 16 + (256 - tile.LiquidAmount) / 16f;
            if (tile.LiquidAmount > 0 && left + width > x * 16 && left < (x + 1) * 16
                && top + height > liquidTop && top < liquidTop + 16 - (int)((256 - tile.LiquidAmount) / 16f)) return true;
            if (tile.HasTile && tile.Slope != SlopeType.Solid && y > 0 && Main.tile[x, y - 1].LiquidAmount > 0
                && left + width > x * 16 && left < (x + 1) * 16 && top + height > y * 16 && top < (y + 1) * 16) return true;
        }
        return false;
    }
}
