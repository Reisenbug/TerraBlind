using System;
using System.Collections.Generic;
using Terraria;

namespace TerraBlind
{
    public static class WaypointPlanner
    {
        public const int Spacing = 20;

        // generate waypoints from (sx,sy) toward (gx,gy), every `Spacing` tiles in dominant direction.
        // y is interpolated linearly; last waypoint is the goal itself.
        public static List<(int wx, int wy)> Generate(int sx, int sy, int gx, int gy)
        {
            var list = new List<(int, int)>();
            int dx = gx - sx;
            int dy = gy - sy;
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (dist <= Spacing) { list.Add((gx, gy)); return list; }
            int steps = (dist + Spacing - 1) / Spacing;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                int wx = sx + (int)Math.Round(dx * t);
                int wy = sy + (int)Math.Round(dy * t);
                list.Add((wx, wy));
            }
            return list;
        }
    }
}
