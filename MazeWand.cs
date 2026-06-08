using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class MazeWand : ModItem
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.RodofDiscord;

        const int MoveDown = 1, MoveSide = 1, MoveUp = 3;
        const int DigDown = 20, DigSide = 30, DigUp = 40;

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.rare = ItemRarityID.Green;
            Item.maxStack = 1;
            Item.noMelee = true;
        }

        public override bool AltFunctionUse(Player player) => true;

        public override bool? UseItem(Player player)
        {
            if (player != Main.LocalPlayer) return null;

            int gx = (int)((Main.mouseX + Main.screenPosition.X) / 16f);
            int gy = (int)((Main.mouseY + Main.screenPosition.Y) / 16f);
            int sx = (int)((player.Center.X) / 16f);
            int sy = (int)((player.position.Y + player.height) / 16f) - 1;

            var field = BuildField(gx, gy, sx, sy);

            if (player.altFunctionUse == 2)
            {
                DrawHeatmap(field, sx, sy, gx, gy);
                return true;
            }

            var (path, breaks) = DescendPath(field, sx, sy, gx, gy);
            DiagLog.Write($"[maze] start=({sx},{sy}) goal=({gx},{gy}) path={path.Count} breaks={breaks} fieldSize={field.Count} startInField={field.ContainsKey((sx, sy))}");

            var tiles = new List<(int, int, Color)>();
            foreach (var (x, y) in path)
                tiles.Add((x, y, PathPlanner.IsBlockPublic(x, y) ? new Color(255, 60, 60) : new Color(40, 200, 255)));
            PathVisSystem.SetTiles(tiles);
            return true;
        }

        // Geometric 2D maze (route restored 2026-06): node = every tile cell, 4-connected, physics ignored.
        // Direction-aware cost (walk cheap, dig expensive). This is the TREND field — the greedy executor reads its
        // cost gradient, not the drawn path. Dead-ends are a separate problem handled at the execution layer.
        public static Dictionary<(int, int), int> BuildField(int gx, int gy, int sx, int sy)
        {
            int minX = System.Math.Min(sx, gx) - 120, maxX = System.Math.Max(sx, gx) + 120;
            int minY = System.Math.Min(sy, gy) - 120, maxY = System.Math.Max(sy, gy) + 120;

            var dist = new Dictionary<(int, int), int>();
            var closed = new HashSet<(int, int)>();
            var pq = new SortedSet<(int cost, int x, int y)>();
            dist[(gx, gy)] = 0;
            pq.Add((0, gx, gy));

            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (pq.Count > 0)
            {
                var cur = pq.Min;
                pq.Remove(cur);
                var (cost, cx, cy) = cur;
                if (!closed.Add((cx, cy))) continue;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dxs[i], ny = cy + dys[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;
                    if (closed.Contains((nx, ny))) continue;
                    int nc = cost + StepCost(cx, cy, nx, ny);
                    if (dist.TryGetValue((nx, ny), out int old) && nc >= old) continue;
                    dist[(nx, ny)] = nc;
                    pq.Add((nc, nx, ny));
                }
            }
            DiagLog.Write($"[ss-field] dist={dist.Count} ms={sw.Elapsed.TotalMilliseconds:0}");
            return dist;
        }

        // forward cost of moving FROM (nx,ny) TO (cx,cy): direction is (cx,cy)-(nx,ny), price set by the cell
        // being entered (cx,cy). reverse BFS expands neighbor nx,ny so we cost the forward step toward goal.
        static int StepCost(int cx, int cy, int nx, int ny)
        {
            bool wall = PathPlanner.IsBlockPublic(cx, cy);
            if (cx != nx) return wall ? DigSide : MoveSide;
            if (cy > ny) return wall ? DigDown : MoveDown;   // y+ is down → moving down into (cx,cy)
            return wall ? DigUp : MoveUp;
        }

        static (List<(int, int)>, int) DescendPath(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy)
        {
            var path = new List<(int, int)>();
            int breaks = 0;
            var cur = (sx, sy);
            if (!field.ContainsKey(cur)) return (path, breaks);

            var seen = new HashSet<(int, int)>();
            for (int step = 0; step < 2000; step++)
            {
                path.Add(cur);
                if (cur == (gx, gy)) break;
                if (!seen.Add(cur)) break;
                int bestD = field[cur]; var best = cur;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (cur.Item1 + dx, cur.Item2 + dy);
                    if (field.TryGetValue(n, out int dn) && dn < bestD) { bestD = dn; best = n; }
                }
                if (best == cur) break;
                cur = best;
            }
            return (path, breaks);
        }

        static void DrawHeatmap(Dictionary<(int, int), int> field, int sx, int sy, int gx, int gy)
        {
            // normalize by the start cell's cost so the gradient spreads across the actual start→goal range;
            // cells farther than start clamp to red. (using global max washes everything green — a few far
            // dig-heavy cells blow up the scale.)
            float scale = field.TryGetValue((sx, sy), out int sc) && sc > 0 ? sc : 1f;
            var tiles = new List<(int, int, Color)>();
            foreach (var kv in field)
            {
                float t = System.Math.Min(1f, kv.Value / scale);
                var c = new Color(t, 1f - t, 0.2f) * 0.5f;
                tiles.Add((kv.Key.Item1, kv.Key.Item2, c));
            }
            DiagLog.Write($"[maze-field] fieldSize={field.Count} scale={scale} startInField={field.ContainsKey((sx, sy))}");
            PathVisSystem.SetTiles(tiles);
        }

        public override void AddRecipes()
        {
            CreateRecipe().AddIngredient(ItemID.DirtBlock, 1).AddTile(TileID.WorkBenches).Register();
        }
    }
}
