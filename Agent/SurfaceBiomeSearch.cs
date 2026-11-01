using System;

namespace TerraBlind;

public readonly record struct BiomeCell(bool Solid, int Weight, bool Hazard = false);
public readonly record struct BiomeEntrance(int X, int Y, int LocalCount, int TotalCount, long Scanned);

public static class SurfaceBiomeSearch
{
    public static BiomeEntrance? Find(int width, int height, int surface, int originX, int scanWidth, int scanHeight,
        int threshold, Func<int, int, BiomeCell> read)
    {
        int limit = Math.Min(height, surface + scanHeight / 2 + 1);
        var prefix = new int[limit + 1, width + 1];
        var ground = new int[width];
        Array.Fill(ground, -1);
        for (int y = 0; y < limit; y++)
        for (int x = 0; x < width; x++)
        {
            var cell = read(x, y);
            if (y < surface && ground[x] < 0 && cell.Solid) ground[x] = y;
            prefix[y + 1, x + 1] = cell.Weight + prefix[y, x + 1] + prefix[y + 1, x] - prefix[y, x];
        }
        BiomeEntrance? best = null;
        int distance = int.MaxValue;
        for (int x = 1; x < width - 1; x++)
        {
            int floor = ground[x];
            if (floor < 3 || Math.Abs(x - originX) >= distance) continue;
            bool clear = true;
            for (int dx = -1; dx <= 1 && clear; dx++)
            for (int dy = 1; dy <= 3 && clear; dy++)
            {
                var cell = read(x + dx, floor - dy);
                clear = !cell.Solid && !cell.Hazard;
            }
            if (!clear) continue;
            int left = Math.Max(0, x - scanWidth / 2), right = Math.Min(width, x - scanWidth / 2 + scanWidth);
            int top = Math.Max(0, floor - 2 - scanHeight / 2), bottom = Math.Min(limit, floor - 2 - scanHeight / 2 + scanHeight);
            int count = prefix[bottom, right] - prefix[top, right] - prefix[bottom, left] + prefix[top, left];
            if (count < threshold) continue;
            distance = Math.Abs(x - originX);
            best = new BiomeEntrance(x, floor - 1, count, prefix[limit, width], (long)limit * width);
        }
        return best;
    }
}
