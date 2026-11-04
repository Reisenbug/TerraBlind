using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

internal sealed class VanillaExcavation
{
    internal MineRequest Request { get; private init; }
    readonly List<(int x, int y)> floor = new();
    readonly List<(int x, int y)> volume = new();

    static bool Floor(int x, int y)
    {
        if (!Predicates.InBounds(x, y)) return false;
        var tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated && Main.tileSolid[tile.TileType]
            && TileID.Sets.TouchDamageImmediate[tile.TileType] == 0
            && !Main.tileSolidTop[tile.TileType] && !tile.IsHalfBlock && tile.Slope == 0
            && tile.TileType is not TileID.Spikes and not TileID.WoodenSpikes and not TileID.Hellstone and not TileID.Meteorite;
    }

    internal bool Safe()
    {
        foreach (var cell in floor)
            if (!Floor(cell.x, cell.y)) return false;
        foreach (var cell in volume)
        {
            var tile = Main.tile[cell.x, cell.y];
            if (LavaSafety.IsLava(cell.x, cell.y)) return false;
            if (!tile.HasTile || tile.IsActuated) continue;
            if (TileID.Sets.TouchDamageImmediate[tile.TileType] > 0
                || tile.TileType is TileID.Spikes or TileID.WoodenSpikes or TileID.Hellstone or TileID.Meteorite)
                return false;
            if (Predicates.IsWall(cell.x, cell.y)
                && (!LavaSafety.CanMine(cell.x, cell.y) || !WorldGen.CanKillTile(cell.x, cell.y)
                    || DigTable.CostFrames(cell.x, cell.y) >= DigTable.Unmineable)) return false;
        }
        return true;
    }

    internal static VanillaExcavation Create(Player player, IReadOnlyList<(int x, int y)> corridor)
    {
        if (corridor.Count == 0 || player.velocity.Y != 0 || Math.Abs(player.velocity.X) > .01f) return null;
        var start = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
        var first = corridor[0];
        int dx = first.x - start.x, dy = first.y - start.y;
        if (Math.Abs(dx) + Math.Abs(dy) != 1 || dy < 0) return null;
        int length = 0;
        foreach (var cell in corridor)
        {
            if (length == 12 || cell != (start.x + dx * (length + 1), start.y + dy * (length + 1))) break;
            length++;
        }
        int left = (int)Math.Floor(player.Left.X / 16), right = (int)Math.Floor((player.Right.X - 1) / 16);
        for (int distance = length; distance > 0; distance--)
        {
            int endX = start.x + dx * distance, endY = start.y + dy * distance;
            var result = new VanillaExcavation { Request = new MineRequest {
                Dir = dy > 0 ? MineDir.Down : dx > 0 ? MineDir.Right : MineDir.Left,
                StartWx = start.x, StartWy = start.y, TargetWx = endX, TargetWy = endY,
                OnlyPlannedTiles = true, MineTiles = new() } };
            int x0 = Math.Min(left, left + dx * distance), x1 = Math.Max(right, right + dx * distance);
            int head = (int)Math.Floor(player.Top.Y / 16);
            bool bounds = true;
            for (int x = x0; x <= x1; x++)
            {
                result.floor.Add((x, endY + 1));
                for (int y = head; y <= endY; y++)
                {
                    if (!Predicates.InBounds(x, y)) { bounds = false; continue; }
                    result.volume.Add((x, y));
                    if (Predicates.IsWall(x, y)) result.Request.MineTiles.Add((x, y));
                }
            }
            if (bounds && result.Request.MineTiles.Count > 0 && result.Safe()) return result;
        }
        return null;
    }
}
