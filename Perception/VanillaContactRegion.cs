using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class VanillaContactRegion
{
    public static bool Hazard(int x, int y)
    {
        var tile = Main.tile[x, y];
        return LavaSafety.IsLava(x, y) || tile.HasTile && !tile.IsActuated
            && (TileID.Sets.TouchDamageImmediate[tile.TileType] > 0
                || tile.TileType is TileID.Spikes or TileID.WoodenSpikes or TileID.Hellstone or TileID.Meteorite);
    }

    public static bool SafeBody(Vector2 position, int width, int height)
    {
        int left = (int)Math.Floor(position.X / 16), right = (int)Math.Floor((position.X + width - .01f) / 16);
        int top = (int)Math.Floor(position.Y / 16), bottom = (int)Math.Floor((position.Y + height) / 16);
        if (left < 1 || top < 1 || right >= Main.maxTilesX - 1 || bottom >= Main.maxTilesY - 1) return false;
        if (Collision.SolidCollision(position, width, height)) return false;
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
            if (Hazard(x, y)) return false;
        return true;
    }

    public static bool Supported(Vector2 position, int width, int height)
    {
        if (!SafeBody(position, width, height)) return false;
        int y = (int)Math.Floor((position.Y + height) / 16);
        for (int x = (int)Math.Floor(position.X / 16); x <= (int)Math.Floor((position.X + width - .01f) / 16); x++)
            if (Main.tile[x, y].HasTile && !Main.tile[x, y].IsActuated && Main.tile[x, y].Slope != 0) return false;
        bool up = Collision.up, down = Collision.down;
        try { return Collision.TileCollision(position, Vector2.UnitY, width, height).Y == 0; }
        finally { Collision.up = up; Collision.down = down; }
    }

    public static bool ConnectedSupport(Vector2 from, Vector2 to, int width, int height)
    {
        if (Math.Abs(from.Y - to.Y) > .01f || Math.Abs(from.X - to.X) > 256) return false;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(from.X - to.X) / 2));
        for (int i = 0; i <= steps; i++)
            if (!Supported(Vector2.Lerp(from, to, i / (float)steps), width, height)) return false;
        return true;
    }
}
