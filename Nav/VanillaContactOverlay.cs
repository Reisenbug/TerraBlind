using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace TerraBlind;

internal sealed class VanillaContactOverlay
{
    internal readonly record struct Surface(float Left, float Right, float Y);
    readonly List<Surface> surfaces = new();
    readonly List<Rectangle> hazards = new();
    readonly List<Rectangle> unverified = new();
    ulong updated;
    Point origin;

    internal static bool Hazard(int x, int y)
    {
        var tile = Main.tile[x, y];
        return LavaSafety.IsLava(x, y) || tile.HasTile && !tile.IsActuated
            && (TileID.Sets.TouchDamageImmediate[tile.TileType] > 0
                || tile.TileType is TileID.Spikes or TileID.WoodenSpikes or TileID.Hellstone or TileID.Meteorite);
    }

    internal static bool SafeBody(Vector2 position, int width, int height)
    {
        if (Collision.SolidCollision(position, width, height)) return false;
        int left = (int)Math.Floor(position.X / 16), right = (int)Math.Floor((position.X + width - .01f) / 16);
        int top = (int)Math.Floor(position.Y / 16), bottom = (int)Math.Floor((position.Y + height) / 16);
        if (left < 1 || top < 1 || right >= Main.maxTilesX - 1 || bottom >= Main.maxTilesY - 1) return false;
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
            if (Hazard(x, y)) return false;
        return true;
    }

    void Refresh(Player player)
    {
        var center = player.Center.ToTileCoordinates();
        if (Main.GameUpdateCount - updated < 15 && Math.Abs(center.X - origin.X) + Math.Abs(center.Y - origin.Y) < 8) return;
        updated = Main.GameUpdateCount; origin = center;
        surfaces.Clear(); hazards.Clear(); unverified.Clear();
        if (player.gravDir != 1) return;
        var samples = new SortedDictionary<float, SortedSet<int>>();
        for (int x = Math.Max(1, center.X - 26); x <= Math.Min(Main.maxTilesX - 2, center.X + 26); x++)
        for (int y = Math.Max(1, center.Y - 18); y <= Math.Min(Main.maxTilesY - 2, center.Y + 24); y++)
        {
            var tile = Main.tile[x, y];
            if (Hazard(x, y)) { hazards.Add(new Rectangle(x * 16, y * 16, 16, 16)); continue; }
            if (!tile.HasTile || tile.IsActuated || !Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType]) continue;
            if (tile.Slope != 0) { unverified.Add(new Rectangle(x * 16, y * 16, 16, 16)); continue; }
            if (Main.tileSolidTop[tile.TileType] && tile.TileFrameY != 0) continue;
            float feet = y * 16 + (tile.IsHalfBlock ? 8 : 0);
            int min = (int)Math.Ceiling((x * 16 - player.width / 2f + 1) / 4) * 4;
            int max = (int)Math.Floor(((x + 1) * 16 + player.width / 2f - 1) / 4) * 4;
            for (int cx = min; cx <= max; cx += 4)
            {
                var position = new Vector2(cx - player.width / 2f, feet - player.height);
                if (!SafeBody(position, player.width, player.height)) continue;
                if (Collision.TileCollision(position, Vector2.UnitY, player.width, player.height).Y != 0) continue;
                if (!samples.TryGetValue(feet, out var row)) samples[feet] = row = new SortedSet<int>();
                row.Add(cx);
            }
        }
        foreach (var row in samples)
        {
            int start = int.MinValue, end = int.MinValue;
            foreach (int x in row.Value)
            {
                if (start != int.MinValue && x != end + 4) { surfaces.Add(new Surface(start, end, row.Key)); start = int.MinValue; }
                if (start == int.MinValue) start = x;
                end = x;
            }
            if (start != int.MinValue) surfaces.Add(new Surface(start, end, row.Key));
        }
    }

    static void Line(SpriteBatch batch, Vector2 from, Vector2 to, Color color, float width = 2)
    {
        var delta = to - from;
        batch.Draw(TextureAssets.MagicPixel.Value, from - Main.screenPosition, new Rectangle(0, 0, 1, 1), color,
            delta.ToRotation(), Vector2.Zero, new Vector2(Math.Max(2, delta.Length()), width), SpriteEffects.None, 0);
    }

    internal void Draw(SpriteBatch batch, Player player, JsonArray frames, int first)
    {
        Refresh(player);
        foreach (var box in hazards)
            batch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(box.X - (int)Main.screenPosition.X,
                box.Y - (int)Main.screenPosition.Y, box.Width, box.Height), Color.Red * .45f);
        foreach (var box in unverified)
            Line(batch, new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Top), Color.Gray);
        var contacts = frames == null ? Array.Empty<Vector2>() : frames.Skip(Math.Max(0, first))
            .Where(frame => frame["grounded"]?.GetValue<bool>() == true)
            .Select(frame => new Vector2(frame["x"].GetValue<float>() + player.width / 2f,
                frame["y"].GetValue<float>() + player.height)).ToArray();
        foreach (var surface in surfaces)
        {
            Line(batch, new Vector2(surface.Left, surface.Y), new Vector2(surface.Right, surface.Y), Color.LimeGreen);
            foreach (var p in contacts.Where(p => p.X >= surface.Left && p.X <= surface.Right && Math.Abs(p.Y - surface.Y) < .1f))
                Line(batch, p - new Vector2(1, 4), p + new Vector2(1, -4), Color.Cyan);
        }
        if (contacts.Length > 0)
        {
            var target = contacts[^1];
            Line(batch, target - new Vector2(6, 0), target + new Vector2(6, 0), Color.Yellow, 4);
        }
        var body = player.Hitbox;
        Line(batch, new Vector2(body.Left, body.Top), new Vector2(body.Right, body.Top), Color.White);
        Line(batch, new Vector2(body.Left, body.Bottom), new Vector2(body.Right, body.Bottom), Color.White);
        Line(batch, new Vector2(body.Left, body.Top), new Vector2(body.Left, body.Bottom), Color.White);
        Line(batch, new Vector2(body.Right, body.Top), new Vector2(body.Right, body.Bottom), Color.White);
    }
}
