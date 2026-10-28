using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class VanillaContactRegion
{
    public static VanillaKeys MoveToward(Player player, Rectangle destination, VanillaKeys intent)
    {
        var foot = player.Bottom;
        int direction = foot.X < destination.Left ? 1 : foot.X > destination.Right ? -1 : 0;
        var keys = (intent & ~(VanillaKeys.Left | VanillaKeys.Right | VanillaKeys.Jump))
            | (direction < 0 ? VanillaKeys.Left : direction > 0 ? VanillaKeys.Right : VanillaKeys.None);
        if ((intent & VanillaKeys.Jump) == 0) return keys;
        bool headroom = SafeBody(player.position - new Vector2(0, 2), player.width, player.height);
        if (!headroom) return keys;
        if (player.jump > 0 && !player.releaseJump) return keys | VanillaKeys.Jump;
        if (!player.releaseJump) return keys;
        bool grounded = player.velocity.Y == 0 && Supported(player.position, player.width, player.height);
        bool available = grounded;
        foreach (var jump in player.ExtraJumps) available |= jump.Available;
        bool higher = foot.Y > destination.Bottom + 1;
        float lookahead = Math.Max(8, Math.Abs(player.velocity.X) * 3);
        bool blocked = direction != 0 && !SafeBody(player.position + new Vector2(direction * lookahead, 0), player.width, player.height);
        return available && (grounded && (higher || blocked) || !grounded && player.velocity.Y >= 0 && (higher || blocked))
            ? keys | VanillaKeys.Jump : keys;
    }

    public static bool TryWalk(Player player, Vector2 goal, out VanillaKeys keys)
    {
        keys = VanillaKeys.None;
        if (player.dead || player.gravDir != 1 || player.wet || player.mount.Active
            || player.grapCount > 0 || player.dashDelay < 0 || player.velocity.Y != 0
            || player.runSlowdown <= 0 || Math.Abs(goal.Y - player.position.Y) > 16) return false;
        float distance = goal.X - player.position.X;
        int direction = Math.Sign(distance);
        float speed = Math.Max(Math.Abs(player.velocity.X), player.maxRunSpeed);
        float runway = Math.Max(32, speed * speed / player.runSlowdown + speed * 2);
        if (Math.Abs(distance) < runway || runway > 160) return false;
        for (float ahead = 0; ahead <= runway + 2; ahead += 2)
        {
            var position = player.position + new Vector2(direction * ahead, 0);
            if (!Supported(position, player.width, player.height)) return false;
            int bottom = (int)((position.Y + player.height) / 16);
            for (int x = (int)(position.X / 16); x <= (int)((position.X + player.width) / 16); x++)
            for (int y = (int)(position.Y / 16); y <= bottom; y++)
                if (Main.tile[x, y].LiquidAmount > 0 || Main.tile[x, y].HasTile
                    && Main.tile[x, y].TileType is TileID.Cobweb or TileID.HoneyBlock or TileID.PressurePlates or TileID.WeightedPressurePlate)
                    return false;
        }
        keys = direction < 0 ? VanillaKeys.Left : VanillaKeys.Right;
        return true;
    }

    public sealed class Progress
    {
        bool grounded, hasContact;
        Vector2 lastContact;
        int lastCost;

        public bool ReturnedWithoutProgress(Player player, int cost)
        {
            bool supported = player.velocity.Y == 0 && Supported(player.position, player.width, player.height);
            bool landed = supported && !grounded;
            grounded = supported;
            if (!landed) return false;
            bool repeated = hasContact && cost >= lastCost && Math.Abs(player.velocity.X) < .01f
                && ConnectedSupport(lastContact, player.position, player.width, player.height);
            lastContact = player.position; lastCost = cost; hasContact = true;
            return repeated;
        }

        public void Reset() { grounded = hasContact = false; }
    }

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
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
        {
            if (Hazard(x, y)) return false;
            var tile = Main.tile[x, y];
            if (!tile.HasTile || tile.IsActuated || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]) continue;
            float x1 = Math.Max(position.X, x * 16), x2 = Math.Min(position.X + width, x * 16 + 16);
            float y1 = Math.Max(position.Y, y * 16 + (tile.IsHalfBlock ? 8 : 0));
            float y2 = Math.Min(position.Y + height, y * 16 + 16);
            if (x1 >= x2 || y1 >= y2) continue;
            bool intersects = tile.Slope switch
            {
                SlopeType.SlopeDownLeft => y2 - y * 16 > x1 - x * 16,
                SlopeType.SlopeDownRight => y2 - y * 16 > x * 16 + 16 - x2,
                SlopeType.SlopeUpLeft => y1 - y * 16 < x * 16 + 16 - x1,
                SlopeType.SlopeUpRight => y1 - y * 16 < x2 - x * 16,
                _ => true
            };
            if (intersects) return false;
        }
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

    public static Vector2 SupportRange(Vector2 anchor, int width, int height)
    {
        float left = anchor.X, right = anchor.X;
        if (!Supported(anchor, width, height)) return new Vector2(left, right);
        while (left > anchor.X - 256 && Supported(new Vector2(left - 2, anchor.Y), width, height)) left -= 2;
        while (right < anchor.X + 256 && Supported(new Vector2(right + 2, anchor.Y), width, height)) right += 2;
        return new Vector2(left, right);
    }

    public static bool SafeDescent(Player player, Vector2 landing)
    {
        if (player.gravDir != 1 || player.velocity.X != 0 || player.velocity.Y < 0 || player.jump > 0
            || player.position.Y > landing.Y || landing.Y - player.position.Y > 512
            || !player.noFallDmg && (int)(landing.Y / 16) - player.fallStart > 25 + player.extraFall
            || !ConnectedSupport(landing, new Vector2(player.position.X, landing.Y), player.width, player.height)) return false;
        landing.X = player.position.X;
        if (player.position.Y < landing.Y && player.velocity.Y == 0
            && Supported(player.position, player.width, player.height)) return false;
        int steps = Math.Max(1, (int)Math.Ceiling((landing.Y - player.position.Y) / 2));
        for (int i = 0; i <= steps; i++)
            if (!SafeBody(Vector2.Lerp(player.position, landing, i / (float)steps), player.width, player.height)) return false;
        return true;
    }
}
